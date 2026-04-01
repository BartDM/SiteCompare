using Microsoft.Playwright;

namespace SiteCompare.Services;

public class ScreenshotService : IScreenshotService, IAsyncDisposable
{
    private readonly ILogger<ScreenshotService> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _semaphore = new(4, 4); // Max 4 concurrent screenshots
    private readonly SemaphoreSlim _initializeLock = new(1, 1);

    public ScreenshotService(ILogger<ScreenshotService> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await EnsureBrowserInitializedAsync();
    }

    private async Task EnsureBrowserInitializedAsync()
    {
        if (_browser is { IsConnected: true })
            return;

        await _initializeLock.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true })
                return;

            if (_browser != null)
            {
                await SafeCloseBrowserAsync(_browser);
                _browser = null;
            }

            _playwright?.Dispose();

            _logger.LogDebug("Initializing Playwright browser");
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize Playwright browser — screenshot capture is unavailable");
                throw;
            }

            _browser.Disconnected += (_, _) => _logger.LogWarning("Playwright browser disconnected");
            _logger.LogInformation("Playwright browser initialized");
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<byte[]?> TakeScreenshotAsync(string url, int width, int height, CancellationToken cancellationToken = default)
    {
        await EnsureBrowserInitializedAsync();

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await TryTakeScreenshotAsync(url, width, height, cancellationToken);
        }
        catch (PlaywrightException ex) when (IsTargetClosed(ex))
        {
            _logger.LogWarning(ex, "Browser target closed while capturing {Url}, reinitializing and retrying once", url);
            await EnsureBrowserInitializedAsync();
            return await TryTakeScreenshotAsync(url, width, height, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<byte[]?> TryTakeScreenshotAsync(string url, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var browser = _browser;
        if (browser == null || !browser.IsConnected)
        {
            _logger.LogWarning("Cannot capture screenshot for {Url}: browser is not available", url);
            return null;
        }

        _logger.LogDebug("Taking screenshot of {Url}", url);

        var uri = new Uri(url);
        var cookieDomain = uri.Host;

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            IgnoreHTTPSErrors = true
        });

        try
        {
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

            var page = await context.NewPageAsync();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                _ = await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 30000
                });

                await page.WaitForTimeoutAsync(3000);

                try
                {
                    var acceptButton = page.Locator("button.c-button.cb-accept.cb-view");
                    if (await acceptButton.IsVisibleAsync())
                    {
                        _logger.LogDebug("Cookie banner detected on {Url}, clicking accept button", url);
                        await acceptButton.ClickAsync();
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to take screenshot of {Url}", url);
                return null;
            }
            finally
            {
                await SafeClosePageAsync(page);
            }
        }
        finally
        {
            await SafeCloseContextAsync(context);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogDebug("Disposing Playwright browser");
        if (_browser != null)
        {
            await SafeCloseBrowserAsync(_browser);
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        _initializeLock.Dispose();
        _semaphore.Dispose();
        _logger.LogInformation("Playwright browser disposed");
    }

    private async Task SafeClosePageAsync(IPage? page)
    {
        if (page == null)
            return;

        try
        {
            await page.CloseAsync();
        }
        catch (PlaywrightException ex) when (IsTargetClosed(ex))
        {
            // Already closed by browser/context shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring page close error");
        }
    }

    private async Task SafeCloseContextAsync(IBrowserContext? context)
    {
        if (context == null)
            return;

        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException ex) when (IsTargetClosed(ex))
        {
            // Already closed by browser shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring context close error");
        }
    }

    private async Task SafeCloseBrowserAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync();
        }
        catch (PlaywrightException ex) when (IsTargetClosed(ex))
        {
            // Already closed.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring browser close error");
        }
    }

    private static bool IsTargetClosed(PlaywrightException ex) =>
        ex.Message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase);
}
