using System.Xml.Linq;

namespace SiteCompare.Services;

public class SitemapService : ISitemapService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SitemapService> _logger;

    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public SitemapService(IHttpClientFactory httpClientFactory, ILogger<SitemapService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("SitemapClient");
        _logger = logger;
    }

    public async Task<List<string>> GetAllUrlsAsync(string baseUrl, string sitemapPath, CancellationToken cancellationToken = default)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startUrl = baseUrl.TrimEnd('/') + sitemapPath;

        _logger.LogInformation("Starting sitemap crawl from {Url}", startUrl);

        await ProcessSitemapUrlAsync(startUrl, urls, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken);

        _logger.LogInformation("Sitemap crawl completed. Found {Count} URLs", urls.Count);
        return urls.OrderBy(u => u).ToList();
    }

    private async Task ProcessSitemapUrlAsync(
        string url,
        HashSet<string> collectedUrls,
        HashSet<string> visitedSitemaps,
        CancellationToken cancellationToken)
    {
        if (!visitedSitemaps.Add(url))
        {
            _logger.LogDebug("Already visited sitemap {Url}, skipping", url);
            return;
        }

        try
        {
            _logger.LogDebug("Fetching sitemap: {Url}", url);
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch sitemap {Url}: HTTP {StatusCode}", url, response.StatusCode);
                return;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (TryParseAsSitemapXml(content, url, out var parsedUrls, out var childSitemaps))
            {
                foreach (var pageUrl in parsedUrls)
                {
                    collectedUrls.Add(pageUrl);
                }

                foreach (var childSitemap in childSitemaps)
                {
                    await ProcessSitemapUrlAsync(childSitemap, collectedUrls, visitedSitemaps, cancellationToken);
                }
            }
            else
            {
                _logger.LogWarning("Could not parse content from {Url} as sitemap XML", url);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing sitemap {Url}", url);
        }
    }

    private bool TryParseAsSitemapXml(
        string content,
        string sourceUrl,
        out List<string> pageUrls,
        out List<string> childSitemaps)
    {
        pageUrls = new List<string>();
        childSitemaps = new List<string>();

        try
        {
            var doc = XDocument.Parse(content);
            var root = doc.Root;

            if (root == null)
                return false;

            var localName = root.Name.LocalName;

            if (localName.Equals("sitemapindex", StringComparison.OrdinalIgnoreCase))
            {
                // Sitemap index — contains links to other sitemaps
                var sitemapElements = root.Elements()
                    .Where(e => e.Name.LocalName.Equals("sitemap", StringComparison.OrdinalIgnoreCase));

                foreach (var sitemap in sitemapElements)
                {
                    var loc = sitemap.Elements()
                        .FirstOrDefault(e => e.Name.LocalName.Equals("loc", StringComparison.OrdinalIgnoreCase))?.Value.Trim();

                    if (!string.IsNullOrEmpty(loc))
                    {
                        childSitemaps.Add(loc);
                    }
                }

                return true;
            }

            if (localName.Equals("urlset", StringComparison.OrdinalIgnoreCase))
            {
                // Standard sitemap — contains page URLs
                var urlElements = root.Elements()
                    .Where(e => e.Name.LocalName.Equals("url", StringComparison.OrdinalIgnoreCase));

                foreach (var urlEl in urlElements)
                {
                    var loc = urlEl.Elements()
                        .FirstOrDefault(e => e.Name.LocalName.Equals("loc", StringComparison.OrdinalIgnoreCase))?.Value.Trim();

                    if (!string.IsNullOrEmpty(loc))
                    {
                        pageUrls.Add(loc);
                    }
                }

                return true;
            }

            _logger.LogWarning("Unknown XML root element '{LocalName}' in sitemap from {Url}", localName, sourceUrl);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Content from {Url} is not valid XML", sourceUrl);
            return false;
        }
    }
}
