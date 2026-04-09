using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SiteCompare.Models;
using StackExchange.Redis;

namespace SiteCompare.Services;

public class ComparisonJobService : IComparisonJobService
{
    private const string RedisJobsSetKey = "sitecompare:jobs";
    private static string RedisJobKey(string jobId) => $"sitecompare:job:{jobId}";

    private readonly ConcurrentDictionary<string, ComparisonJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();
    private readonly ISitemapService _sitemapService;
    private readonly IScreenshotService _screenshotService;
    private readonly IImageComparisonService _imageComparisonService;
    private readonly IAzureBlobStorageService _blobStorageService;
    private readonly IDatabase? _redisDb;
    private readonly ILogger<ComparisonJobService> _logger;

    public ComparisonJobService(
        ISitemapService sitemapService,
        IScreenshotService screenshotService,
        IImageComparisonService imageComparisonService,
        IAzureBlobStorageService blobStorageService,
        IServiceProvider serviceProvider,
        ILogger<ComparisonJobService> logger)
    {
        _sitemapService = sitemapService;
        _screenshotService = screenshotService;
        _imageComparisonService = imageComparisonService;
        _blobStorageService = blobStorageService;
        _redisDb = serviceProvider.GetService<IConnectionMultiplexer>()?.GetDatabase();
        _logger = logger;
    }

    public ComparisonJob CreateJob(string prdBaseUrl, string tstBaseUrl, string sitemapPath, double threshold, int viewportWidth, int viewportHeight, bool ignoreWhitespaceDifferences = false, int maxUrls = 0)
    {
        var job = new ComparisonJob
        {
            PrdBaseUrl = prdBaseUrl.TrimEnd('/'),
            TstBaseUrl = tstBaseUrl.TrimEnd('/'),
            SitemapPath = sitemapPath,
            DifferenceThreshold = threshold,
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
            IgnoreWhitespaceDifferences = ignoreWhitespaceDifferences,
            MaxUrls = maxUrls
        };

        _jobs[job.Id] = job;
        _ = PersistJobToRedisAsync(job);
        return job;
    }

    public ComparisonJob? GetJob(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
            return job;

        return LoadJobFromRedis(jobId);
    }

    public IEnumerable<ComparisonJob> GetAllJobs()
    {
        LoadAllJobsFromRedis();
        return _jobs.Values.OrderByDescending(j => j.StartedAt);
    }

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
                await PersistJobToRedisAsync(job);
                return;
            }

            // TODO: Remove this limit before production use
            //allUrls = allUrls.Take(20).ToList();

            job.TotalPages = allUrls.Count;
            _logger.LogInformation("Job {JobId}: Found {Count} URLs to compare", jobId, allUrls.Count);

            // Apply URL limit when set (> 0), so users can run quick test comparisons
            if (job.MaxUrls > 0 && allUrls.Count > job.MaxUrls)
            {
                allUrls = allUrls.Take(job.MaxUrls).ToList();
                job.TotalPages = allUrls.Count;
                _logger.LogInformation("Job {JobId}: URL list capped to {Max} URLs", jobId, job.MaxUrls);
            }

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
                    await ComparePageAsync(comparison, job, cts.Token);
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
            await PersistJobToRedisAsync(job);

            _logger.LogInformation(
                "Job {JobId} completed: {Total} pages, {Different} different, {Error} errors",
                jobId, job.TotalPages, job.DifferentPages, job.ErrorPages);
        }
        catch (OperationCanceledException)
        {
            job.Status = ComparisonStatus.Failed;
            job.ErrorMessage = "Job was cancelled.";
            job.CompletedAt = DateTime.UtcNow;
            await PersistJobToRedisAsync(job);
            _logger.LogInformation("Job {JobId} was cancelled", jobId);
        }
        catch (Exception ex)
        {
            job.Status = ComparisonStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            await PersistJobToRedisAsync(job);
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

        // Upload screenshots to Azure Blob Storage
        var blobBase = $"{job.Id}/{SanitizePathSegment(comparison.RelativePath)}";

        var prdUploadTask = _blobStorageService.UploadScreenshotAsync($"{blobBase}/prd.png", prdScreenshot, cancellationToken);
        var tstUploadTask = _blobStorageService.UploadScreenshotAsync($"{blobBase}/tst.png", tstScreenshot, cancellationToken);

        // Compare images
        var result = _imageComparisonService.Compare(prdScreenshot, tstScreenshot, job.IgnoreWhitespaceDifferences);

        Task<string>? diffUploadTask = result.DiffImage != null
            ? _blobStorageService.UploadScreenshotAsync($"{blobBase}/diff.png", result.DiffImage, cancellationToken)
            : null;

        await Task.WhenAll(
            prdUploadTask,
            tstUploadTask,
            diffUploadTask ?? Task.FromResult(string.Empty));

        comparison.PrdScreenshotUrl = await prdUploadTask;
        comparison.TstScreenshotUrl = await tstUploadTask;
        comparison.DiffImageUrl = diffUploadTask != null ? await diffUploadTask : null;
        comparison.DifferencePercentage = result.DifferencePercentage;
        comparison.HasDifferences = result.DifferencePercentage > job.DifferenceThreshold;
        comparison.Status = PageComparisonStatus.Success;
    }

    private async Task PersistJobToRedisAsync(ComparisonJob job)
    {
        if (_redisDb == null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(job);
            await _redisDb.StringSetAsync(RedisJobKey(job.Id), json);
            await _redisDb.SetAddAsync(RedisJobsSetKey, job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist job {JobId} to Redis", job.Id);
        }
    }

    private ComparisonJob? LoadJobFromRedis(string jobId)
    {
        if (_redisDb == null)
            return null;

        try
        {
            var json = (string?)_redisDb.StringGet(RedisJobKey(jobId));
            if (json == null)
                return null;

            var job = JsonSerializer.Deserialize<ComparisonJob>(json);
            if (job != null)
                _jobs[job.Id] = job;

            return job;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load job from Redis");
            return null;
        }
    }

    private void LoadAllJobsFromRedis()
    {
        if (_redisDb == null)
            return;

        try
        {
            var jobIds = _redisDb.SetMembers(RedisJobsSetKey);
            foreach (var id in jobIds)
            {
                var jobId = (string?)id;
                if (jobId != null && !_jobs.ContainsKey(jobId))
                    LoadJobFromRedis(jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load jobs from Redis");
        }
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
}
