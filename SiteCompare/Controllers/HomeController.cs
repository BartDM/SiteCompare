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
        var recentJobs = _jobService.GetAllJobs().Take(5).ToList();
        ViewBag.RecentJobs = recentJobs;
        return View(new StartComparisonViewModel());
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
