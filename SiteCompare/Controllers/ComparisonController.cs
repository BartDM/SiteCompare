using Microsoft.AspNetCore.Mvc;
using SiteCompare.Models;
using SiteCompare.Services;

namespace SiteCompare.Controllers;

public class ComparisonController : Controller
{
    private readonly IComparisonJobService _jobService;
    private readonly ILogger<ComparisonController> _logger;

    public ComparisonController(IComparisonJobService jobService, ILogger<ComparisonController> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Start(StartComparisonViewModel model)
    {
        _logger.LogDebug("Received comparison start request for PRD={PrdUrl}, TST={TstUrl}, Sitemap={SitemapPath}",
            Sanitize(model.PrdBaseUrl), Sanitize(model.TstBaseUrl), Sanitize(model.SitemapPath));

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Comparison start request rejected: model validation failed");
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (!Uri.TryCreate(model.PrdBaseUrl, UriKind.Absolute, out _))
        {
            _logger.LogWarning("Comparison start request rejected: invalid PRD URL {PrdUrl}", Sanitize(model.PrdBaseUrl));
            ModelState.AddModelError(nameof(model.PrdBaseUrl), "Please enter a valid PRD URL (e.g. https://www.example.com).");
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (!Uri.TryCreate(model.TstBaseUrl, UriKind.Absolute, out _))
        {
            _logger.LogWarning("Comparison start request rejected: invalid TST URL {TstUrl}", Sanitize(model.TstBaseUrl));
            ModelState.AddModelError(nameof(model.TstBaseUrl), "Please enter a valid TST URL (e.g. https://tst.example.com).");
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (string.IsNullOrWhiteSpace(model.SitemapPath) || !model.SitemapPath.StartsWith('/'))
        {
            _logger.LogWarning("Comparison start request rejected: sitemap path '{SitemapPath}' does not start with '/'", Sanitize(model.SitemapPath));
            ModelState.AddModelError(nameof(model.SitemapPath), "Sitemap path must start with '/'.");
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (model.SitemapPath.Contains("..") || model.SitemapPath.Contains('\\'))
        {
            _logger.LogWarning("Comparison start request rejected: sitemap path '{SitemapPath}' contains illegal characters", Sanitize(model.SitemapPath));
            ModelState.AddModelError(nameof(model.SitemapPath), "Sitemap path may not contain '..' or backslashes.");
            return View("~/Views/Home/Index.cshtml", model);
        }

        var job = _jobService.CreateJob(
            model.PrdBaseUrl,
            model.TstBaseUrl,
            model.SitemapPath,
            model.DifferenceThreshold,
            model.ViewportWidth,
            model.ViewportHeight,
            model.IgnoreWhitespaceDifferences,
            model.MaxUrls);

        _logger.LogInformation("Created comparison job {JobId} for PRD={PrdUrl} vs TST={TstUrl}",
            job.Id, Sanitize(job.PrdBaseUrl), Sanitize(job.TstBaseUrl));

        // Fire-and-forget background task
        _ = Task.Run(async () =>
        {
            try
            {
                await _jobService.StartJobAsync(job.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in background job {JobId}", job.Id);
            }
        });

        _logger.LogDebug("Redirecting to status page for job {JobId}", job.Id);
        return RedirectToAction(nameof(Status), new { id = job.Id });
    }

    public IActionResult Status(string id)
    {
        _logger.LogDebug("Status requested for job {JobId}", Sanitize(id));
        var job = _jobService.GetJob(id);
        if (job == null)
        {
            _logger.LogWarning("Status requested for unknown job {JobId}", Sanitize(id));
            return NotFound();
        }

        _logger.LogDebug("Returning status '{Status}' for job {JobId}", job.Status, Sanitize(id));
        return View(job);
    }

    [HttpGet]
    public IActionResult StatusJson(string id)
    {
        _logger.LogDebug("JSON status requested for job {JobId}", Sanitize(id));
        var job = _jobService.GetJob(id);
        if (job == null)
        {
            _logger.LogWarning("JSON status requested for unknown job {JobId}", Sanitize(id));
            return NotFound();
        }

        return Json(new
        {
            status = job.Status.ToString(),
            progress = job.Progress,
            totalPages = job.TotalPages,
            currentPage = job.CurrentPage,
            errorMessage = job.ErrorMessage,
            completedAt = job.CompletedAt
        });
    }

    public IActionResult Results(string id, string? filter = null, string? search = null, string? sortBy = null)
    {
        _logger.LogDebug("Results requested for job {JobId} (filter={Filter}, search={Search}, sortBy={SortBy})",
            Sanitize(id), Sanitize(filter), Sanitize(search), Sanitize(sortBy));
        var job = _jobService.GetJob(id);
        if (job == null)
        {
            _logger.LogWarning("Results requested for unknown job {JobId}", Sanitize(id));
            return NotFound();
        }

        ViewBag.Filter = filter;
        ViewBag.Search = search;
        ViewBag.SortBy = sortBy;

        return View(job);
    }

    public IActionResult Detail(string id, string path)
    {
        _logger.LogDebug("Detail requested for job {JobId}, path '{Path}'", Sanitize(id), Sanitize(path));
        var job = _jobService.GetJob(id);
        if (job == null)
        {
            _logger.LogWarning("Detail requested for unknown job {JobId}", Sanitize(id));
            return NotFound();
        }

        var comparison = job.Results.FirstOrDefault(r =>
            r.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));

        if (comparison == null)
        {
            _logger.LogWarning("Detail requested for unknown path '{Path}' in job {JobId}", Sanitize(path), Sanitize(id));
            return NotFound();
        }

        ViewBag.Job = job;
        return View(comparison);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Cancel(string id)
    {
        _logger.LogInformation("Cancel requested for job {JobId}", Sanitize(id));
        _jobService.CancelJob(id);
        return RedirectToAction(nameof(Status), new { id });
    }

    public IActionResult History()
    {
        _logger.LogDebug("Job history requested");
        var jobs = _jobService.GetAllJobs().ToList();
        _logger.LogDebug("Returning {Count} jobs in history", jobs.Count);
        return View(jobs);
    }

    private static string Sanitize(string? value) =>
        value is null ? "(null)" : value.Replace('\r', ' ').Replace('\n', ' ');
}
