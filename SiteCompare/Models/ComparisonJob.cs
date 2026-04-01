namespace SiteCompare.Models;

public class ComparisonJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PrdBaseUrl { get; set; } = string.Empty;
    public string TstBaseUrl { get; set; } = string.Empty;
    public string SitemapPath { get; set; } = "/nl/sitemaps/";
    public double DifferenceThreshold { get; set; } = 0.5;
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;
    public bool IgnoreWhitespaceDifferences { get; set; } = false;
    public int MaxUrls { get; set; } = 0;
    public ComparisonStatus Status { get; set; } = ComparisonStatus.Pending;
    public int Progress { get; set; }
    public int TotalPages { get; set; }
    public string CurrentPage { get; set; } = string.Empty;
    public List<PageComparison> Results { get; set; } = new();
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public int DifferentPages => Results.Count(r => r.HasDifferences);
    public int SamePages => Results.Count(r => !r.HasDifferences && r.Status == PageComparisonStatus.Success);
    public int ErrorPages => Results.Count(r => r.Status == PageComparisonStatus.Error);
}

public enum ComparisonStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
