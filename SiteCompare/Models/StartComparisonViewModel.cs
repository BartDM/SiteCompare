namespace SiteCompare.Models;

public class StartComparisonViewModel
{
    public string PrdBaseUrl { get; set; } = "https://www.example.com";
    public string TstBaseUrl { get; set; } = "https://tst.example.com";
    public string SitemapPath { get; set; } = "/nl/sitemaps/";
    public double DifferenceThreshold { get; set; } = 0.5;
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;
    public bool IgnoreWhitespaceDifferences { get; set; } = false;
}
