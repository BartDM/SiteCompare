using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SiteCompare.Models;
using SiteCompare.Services;

namespace SiteCompare.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IComparisonJobService _jobService;

    public HomeController(ILogger<HomeController> logger, IComparisonJobService jobService)
    {
        _logger = logger;
        _jobService = jobService;
    }

    public IActionResult Index()
    {
        _logger.LogDebug("Loading home page");
        var recentJobs = _jobService.GetAllJobs().Take(5).ToList();
        _logger.LogDebug("Home page loaded with {Count} recent jobs", recentJobs.Count);
        ViewBag.RecentJobs = recentJobs;
        return View(new StartComparisonViewModel());
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var requestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        _logger.LogError("Error page shown for request {RequestId}", requestId);
        return View(new ErrorViewModel { RequestId = requestId });
    }
}
