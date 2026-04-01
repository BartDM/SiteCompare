using SiteCompare.Models;

namespace SiteCompare.Services;

public interface IComparisonJobService
{
    ComparisonJob CreateJob(string prdBaseUrl, string tstBaseUrl, string sitemapPath, double threshold, int viewportWidth, int viewportHeight, bool ignoreWhitespaceDifferences = false, int maxUrls = 0);
    ComparisonJob? GetJob(string jobId);
    IEnumerable<ComparisonJob> GetAllJobs();
    Task StartJobAsync(string jobId, CancellationToken cancellationToken = default);
    void CancelJob(string jobId);
}
