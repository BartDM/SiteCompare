namespace SiteCompare.Services;

public interface ISitemapService
{
    Task<List<string>> GetAllUrlsAsync(string baseUrl, string sitemapPath, CancellationToken cancellationToken = default);
}
