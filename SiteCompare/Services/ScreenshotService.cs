using Microsoft.Playwright;

namespace SiteCompare.Services;

public class ScreenshotService : IScreenshotService, IAsyncDisposable
{
    private readonly ILogger<ScreenshotService> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _semaphore = new(4, 4); // Max 4 concurrent screenshots

    public ScreenshotService(ILogger<ScreenshotService> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu"
            }
        });

        _logger.LogInformation("Playwright browser initialized");
    }

    public async Task<byte[]?> TakeScreenshotAsync(string url, int width, int height, CancellationToken cancellationToken = default)
    {
        if (_browser == null)
        {
            throw new InvalidOperationException("Browser is not initialized. Call InitializeAsync() first.");
        }

        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            _logger.LogDebug("Taking screenshot of {Url}", url);

            var uri = new Uri(url);
            var cookieDomain = uri.Host;

            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = height },
                IgnoreHTTPSErrors = true
            });

            // Inject cookie consent cookie so the banner is suppressed before the page loads
            await context.AddCookiesAsync(new[]
            {
                new Cookie
                {
                    Name = "CookieConsent",
                    Value = "{stamp:%27efjUKhnYspkBkWsl26uugwz5XI7N0sskgnBwq4i6jcN1uHYJlLgAYg==%27%2Cnecessary:true%2Cpreferences:true%2Cstatistics:true%2Cmarketing:true%2Cmethod:%27explicit%27%2Cver:6%2Cutc:1775044048173%2Cregion:%27be%27}",
                    Domain = cookieDomain,
                    Path = "/"
                }
            });

            try
            {
                var page = await context.NewPageAsync();

                try
                {
                    _ = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.NetworkIdle,
                        Timeout = 30000
                    });

                    // Wait at least 3 seconds for the cookie banner to appear, then dismiss it if still present
                    await page.WaitForTimeoutAsync(3000);

                    try
                    {
                        var acceptButton = page.Locator("button.c-button.cb-accept.cb-view");
                        if (await acceptButton.IsVisibleAsync())
                        {
                            _logger.LogDebug("Cookie banner detected on {Url}, clicking accept button", url);
                            await acceptButton.ClickAsync();
                            // Wait for the banner to disappear
                            await page.WaitForTimeoutAsync(500);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Cookie banner check failed on {Url}, continuing anyway", url);
                    }

                    var screenshot = await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        FullPage = true,
                        Type = ScreenshotType.Png
                    });

                    _logger.LogDebug("Screenshot taken for {Url} ({Bytes} bytes)", url, screenshot.Length);
                    return screenshot;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to take screenshot of {Url}", url);
                    return null;
                }
                finally
                {
                    await page.CloseAsync();
                }
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _semaphore.Dispose();
    }
}
