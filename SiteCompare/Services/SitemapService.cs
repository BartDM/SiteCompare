using System.Xml.Linq;

namespace SiteCompare.Services;

public class SitemapService : ISitemapService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SitemapService> _logger;

    public SitemapService(IHttpClientFactory httpClientFactory, ILogger<SitemapService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("SitemapClient");
        _logger = logger;
    }

    private const int MaxSitemapDepth = 10;
    private const int MaxTotalUrls = 50_000;

    public async Task<List<string>> GetAllUrlsAsync(string baseUrl, string sitemapPath, CancellationToken cancellationToken = default)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startUrl = baseUrl.TrimEnd('/') + sitemapPath;

        // Determine the allowed host so we only follow sitemaps on the same origin
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var startUri)
            || (startUri.Scheme != Uri.UriSchemeHttp && startUri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogError("Invalid sitemap start URL: {Url}", startUrl);
            return urls.ToList();
        }

        var allowedHost = startUri.Host;

        _logger.LogInformation("Starting sitemap crawl from {Url} (allowed host: {Host})", startUrl, allowedHost);

        await ProcessSitemapUrlAsync(startUrl, allowedHost, urls, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 0, cancellationToken);

        _logger.LogInformation("Sitemap crawl completed. Found {Count} URLs", urls.Count);
        return urls.OrderBy(u => u).ToList();
    }

    private async Task ProcessSitemapUrlAsync(
        string url,
        string allowedHost,
        HashSet<string> collectedUrls,
        HashSet<string> visitedSitemaps,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxSitemapDepth)
        {
            _logger.LogWarning("Sitemap recursion depth exceeded at {Url}", url);
            return;
        }

        if (collectedUrls.Count >= MaxTotalUrls)
        {
            _logger.LogWarning("Maximum URL count ({Max}) reached; stopping sitemap crawl", MaxTotalUrls);
            return;
        }

        // Validate that the sitemap URL is on the allowed host (prevents SSRF via malicious sitemaps)
        if (!IsAllowedUrl(url, allowedHost))
        {
            _logger.LogWarning("Skipping sitemap URL from disallowed host: {Url}", url);
            return;
        }

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
                    // Skip URLs that belong to the sollicitatie-flow
                    if (pageUrl.Contains("/sollicitatie-flow/", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping sollicitatie-flow URL: {Url}", pageUrl);
                        continue;
                    }

                    // Only collect page URLs on the allowed host
                    if (IsAllowedUrl(pageUrl, allowedHost))
                    {
                        collectedUrls.Add(pageUrl);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping off-host page URL: {Url}", pageUrl);
                    }
                }

                foreach (var childSitemap in childSitemaps)
                {
                    await ProcessSitemapUrlAsync(childSitemap, allowedHost, collectedUrls, visitedSitemaps, depth + 1, cancellationToken);
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

    private static bool IsAllowedUrl(string url, string allowedHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return string.Equals(uri.Host, allowedHost, StringComparison.OrdinalIgnoreCase);
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
