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
        if (!ModelState.IsValid)
        {
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (!Uri.TryCreate(model.PrdBaseUrl, UriKind.Absolute, out _))
        {
            ModelState.AddModelError(nameof(model.PrdBaseUrl), "Please enter a valid PRD URL (e.g. https://www.example.com).");
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (!Uri.TryCreate(model.TstBaseUrl, UriKind.Absolute, out _))
        {
            ModelState.AddModelError(nameof(model.TstBaseUrl), "Please enter a valid TST URL (e.g. https://tst.example.com).");
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (string.IsNullOrWhiteSpace(model.SitemapPath) || !model.SitemapPath.StartsWith('/'))
        {
            ModelState.AddModelError(nameof(model.SitemapPath), "Sitemap path must start with '/'.");
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (model.SitemapPath.Contains("..") || model.SitemapPath.Contains('\\'))
        {
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
            model.IgnoreWhitespaceDifferences);

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

        return RedirectToAction(nameof(Status), new { id = job.Id });
    }

    public IActionResult Status(string id)
    {
        var job = _jobService.GetJob(id);
        if (job == null)
        {
            return NotFound();
        }

        return View(job);
    }

    [HttpGet]
    public IActionResult StatusJson(string id)
    {
        var job = _jobService.GetJob(id);
        if (job == null)
        {
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
        var job = _jobService.GetJob(id);
        if (job == null)
        {
            return NotFound();
        }

        ViewBag.Filter = filter;
        ViewBag.Search = search;
        ViewBag.SortBy = sortBy;

        return View(job);
    }

    public IActionResult Detail(string id, string path)
    {
        var job = _jobService.GetJob(id);
        if (job == null)
        {
            return NotFound();
        }

        var comparison = job.Results.FirstOrDefault(r =>
            r.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));

        if (comparison == null)
        {
            return NotFound();
        }

        ViewBag.Job = job;
        return View(comparison);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Cancel(string id)
    {
        _jobService.CancelJob(id);
        return RedirectToAction(nameof(Status), new { id });
    }

    public IActionResult History()
    {
        var jobs = _jobService.GetAllJobs().ToList();
        return View(jobs);
    }
}
