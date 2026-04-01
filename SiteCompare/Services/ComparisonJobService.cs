using System.Collections.Concurrent;
using SiteCompare.Models;

namespace SiteCompare.Services;

public class ComparisonJobService : IComparisonJobService
{
    private readonly ConcurrentDictionary<string, ComparisonJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();
    private readonly ISitemapService _sitemapService;
    private readonly IScreenshotService _screenshotService;
    private readonly IImageComparisonService _imageComparisonService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ComparisonJobService> _logger;

    public ComparisonJobService(
        ISitemapService sitemapService,
        IScreenshotService screenshotService,
        IImageComparisonService imageComparisonService,
        IWebHostEnvironment environment,
        ILogger<ComparisonJobService> logger)
    {
        _sitemapService = sitemapService;
        _screenshotService = screenshotService;
        _imageComparisonService = imageComparisonService;
        _environment = environment;
        _logger = logger;
    }

    public ComparisonJob CreateJob(string prdBaseUrl, string tstBaseUrl, string sitemapPath, double threshold, int viewportWidth, int viewportHeight)
    {
        var job = new ComparisonJob
        {
            PrdBaseUrl = prdBaseUrl.TrimEnd('/'),
            TstBaseUrl = tstBaseUrl.TrimEnd('/'),
            SitemapPath = sitemapPath,
            DifferenceThreshold = threshold,
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight
        };

        _jobs[job.Id] = job;
        return job;
    }

    public ComparisonJob? GetJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IEnumerable<ComparisonJob> GetAllJobs() =>
        _jobs.Values.OrderByDescending(j => j.StartedAt);

    public void CancelJob(string jobId)
    {
        if (_cancellationSources.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            _logger.LogInformation("Cancelled job {JobId}", jobId);
        }
    }

    public async Task StartJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            _logger.LogWarning("Job {JobId} not found", jobId);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationSources[jobId] = cts;

        job.Status = ComparisonStatus.Running;
        job.StartedAt = DateTime.UtcNow;

        try
        {
            await _screenshotService.InitializeAsync();

            // Step 1: Fetch all pages from sitemap
            job.CurrentPage = "Fetching sitemap...";
            _logger.LogInformation("Job {JobId}: Fetching sitemap from {Url}", jobId, job.PrdBaseUrl + job.SitemapPath);

            var allUrls = await _sitemapService.GetAllUrlsAsync(
                job.PrdBaseUrl,
                job.SitemapPath,
                cts.Token);

            if (allUrls.Count == 0)
            {
                job.Status = ComparisonStatus.Failed;
                job.ErrorMessage = "No URLs found in sitemap. Check that the sitemap path is correct and accessible.";
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogWarning("Job {JobId}: No URLs found in sitemap", jobId);
                return;
            }

            job.TotalPages = allUrls.Count;
            _logger.LogInformation("Job {JobId}: Found {Count} URLs to compare", jobId, allUrls.Count);

            // Create screenshot directory for this job
            var screenshotDir = Path.Combine(_environment.WebRootPath, "screenshots", jobId);
            Directory.CreateDirectory(screenshotDir);

            // Step 2: Compare each page
            int processed = 0;
            foreach (var prdUrl in allUrls)
            {
                cts.Token.ThrowIfCancellationRequested();

                var relativePath = GetRelativePath(prdUrl, job.PrdBaseUrl);
                var tstUrl = job.TstBaseUrl + relativePath;

                job.CurrentPage = relativePath;

                var comparison = new PageComparison
                {
                    RelativePath = relativePath,
                    PrdUrl = prdUrl,
                    TstUrl = tstUrl
                };

                job.Results.Add(comparison);

                try
                {
                    await ComparePageAsync(comparison, job, screenshotDir, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    comparison.Status = PageComparisonStatus.Error;
                    comparison.ErrorMessage = "Cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    comparison.Status = PageComparisonStatus.Error;
                    comparison.ErrorMessage = ex.Message;
                    _logger.LogError(ex, "Job {JobId}: Error comparing page {Url}", jobId, prdUrl);
                }

                processed++;
                job.Progress = (int)((double)processed / job.TotalPages * 100);
            }

            job.Status = ComparisonStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.CurrentPage = string.Empty;

            _logger.LogInformation(
                "Job {JobId} completed: {Total} pages, {Different} different, {Error} errors",
                jobId, job.TotalPages, job.DifferentPages, job.ErrorPages);
        }
        catch (OperationCanceledException)
        {
            job.Status = ComparisonStatus.Failed;
            job.ErrorMessage = "Job was cancelled.";
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Job {JobId} was cancelled", jobId);
        }
        catch (Exception ex)
        {
            job.Status = ComparisonStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Job {JobId} failed", jobId);
        }
        finally
        {
            _cancellationSources.TryRemove(jobId, out _);
        }
    }

    private async Task ComparePageAsync(
        PageComparison comparison,
        ComparisonJob job,
        string screenshotDir,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Comparing: {Prd} vs {Tst}", comparison.PrdUrl, comparison.TstUrl);

        // Take screenshots in parallel
        var prdTask = _screenshotService.TakeScreenshotAsync(
            comparison.PrdUrl, job.ViewportWidth, job.ViewportHeight, cancellationToken);
        var tstTask = _screenshotService.TakeScreenshotAsync(
            comparison.TstUrl, job.ViewportWidth, job.ViewportHeight, cancellationToken);

        await Task.WhenAll(prdTask, tstTask);

        var prdScreenshot = prdTask.Result;
        var tstScreenshot = tstTask.Result;

        if (prdScreenshot == null || tstScreenshot == null)
        {
            comparison.Status = PageComparisonStatus.Error;
            comparison.ErrorMessage = prdScreenshot == null
                ? "Failed to capture PRD screenshot"
                : "Failed to capture TST screenshot";
            return;
        }

        // Save screenshots
        var pageDir = Path.Combine(screenshotDir, SanitizePathSegment(comparison.RelativePath));
        Directory.CreateDirectory(pageDir);
        EnsurePathIsInsideBase(screenshotDir, pageDir);

        var prdPath = Path.Combine(pageDir, "prd.png");
        var tstPath = Path.Combine(pageDir, "tst.png");
        var diffPath = Path.Combine(pageDir, "diff.png");

        await File.WriteAllBytesAsync(prdPath, prdScreenshot, cancellationToken);
        await File.WriteAllBytesAsync(tstPath, tstScreenshot, cancellationToken);

        // Compare images
        var result = _imageComparisonService.Compare(prdScreenshot, tstScreenshot);

        if (result.DiffImage != null)
        {
            await File.WriteAllBytesAsync(diffPath, result.DiffImage, cancellationToken);
        }

        // Build web-accessible URLs relative to wwwroot
        var urlBase = $"/screenshots/{job.Id}/{SanitizePathSegment(comparison.RelativePath)}";
        comparison.PrdScreenshotUrl = $"{urlBase}/prd.png";
        comparison.TstScreenshotUrl = $"{urlBase}/tst.png";
        comparison.DiffImageUrl = $"{urlBase}/diff.png";
        comparison.DifferencePercentage = result.DifferencePercentage;
        comparison.HasDifferences = result.DifferencePercentage > job.DifferenceThreshold;
        comparison.Status = PageComparisonStatus.Success;
    }

    private static string GetRelativePath(string absoluteUrl, string baseUrl)
    {
        try
        {
            var pageUri = new Uri(absoluteUrl);
            return pageUri.PathAndQuery;
        }
        catch
        {
            return absoluteUrl.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase)
                ? absoluteUrl[baseUrl.Length..]
                : absoluteUrl;
        }
    }

    private static string SanitizePathSegment(string path)
    {
        // Whitelist: only alphanumeric, hyphen, dot, tilde — no directory separators
        var sanitized = System.Text.RegularExpressions.Regex.Replace(path, @"[^a-zA-Z0-9\-._~]", "_");

        // Remove leading/trailing dots and underscores to avoid hidden-file names
        sanitized = sanitized.Trim('.', '_');

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "root";

        return sanitized.Length > 120 ? sanitized[..120] : sanitized;
    }

    private static void EnsurePathIsInsideBase(string basePath, string filePath)
    {
        var fullBase = Path.GetFullPath(basePath) + Path.DirectorySeparatorChar;
        var fullFile = Path.GetFullPath(filePath);
        if (!fullFile.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes the base directory: {filePath}");
        }
    }
}
