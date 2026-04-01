namespace SiteCompare.Models;

public class PageComparison
{
    public string RelativePath { get; set; } = string.Empty;
    public string PrdUrl { get; set; } = string.Empty;
    public string TstUrl { get; set; } = string.Empty;
    public double DifferencePercentage { get; set; }
    public bool HasDifferences { get; set; }
    public string? PrdScreenshotUrl { get; set; }
    public string? TstScreenshotUrl { get; set; }
    public string? DiffImageUrl { get; set; }
    public PageComparisonStatus Status { get; set; } = PageComparisonStatus.Pending;
    public string? ErrorMessage { get; set; }
    public int PrdStatusCode { get; set; }
    public int TstStatusCode { get; set; }
}

public enum PageComparisonStatus
{
    Pending,
    Success,
    Error
}
