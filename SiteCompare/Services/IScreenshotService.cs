namespace SiteCompare.Services;

public interface IScreenshotService
{
    Task<ScreenshotResult?> TakeScreenshotAsync(string url, int width, int height, CancellationToken cancellationToken = default);
    Task InitializeAsync();
}

public record ScreenshotResult(byte[] Data, string FinalUrl, int HttpStatusCode);
