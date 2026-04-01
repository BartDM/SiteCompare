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

            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = height },
                IgnoreHTTPSErrors = true
            });

            try
            {
                var page = await context.NewPageAsync();

                try
                {
                    var response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.NetworkIdle,
                        Timeout = 30000
                    });

                    // Wait a moment for any animations or lazy-loaded content
                    await page.WaitForTimeoutAsync(1500);

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
