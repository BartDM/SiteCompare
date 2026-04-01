namespace SiteCompare.Services;

public interface IScreenshotService
{
    Task<byte[]?> TakeScreenshotAsync(string url, int width, int height, CancellationToken cancellationToken = default);
    Task InitializeAsync();
}
