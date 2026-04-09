namespace SiteCompare.Services;

public interface IAzureBlobStorageService
{
    Task<string> UploadScreenshotAsync(string blobPath, byte[] data, CancellationToken cancellationToken = default);
}
