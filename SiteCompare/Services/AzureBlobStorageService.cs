using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace SiteCompare.Services;

public class AzureBlobStorageService : IAzureBlobStorageService
{
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<AzureBlobStorageService> _logger;
    private readonly SemaphoreSlim _containerInitLock = new(1, 1);
    private volatile bool _containerEnsured;

    public AzureBlobStorageService(IConfiguration configuration, ILogger<AzureBlobStorageService> logger)
    {
        _logger = logger;

        var connectionString = configuration["AzureStorage:ConnectionString"]
            ?? throw new InvalidOperationException("AzureStorage:ConnectionString is not configured.");
        var containerName = configuration["AzureStorage:ContainerName"] ?? "screenshots";

        _containerClient = new BlobContainerClient(connectionString, containerName);
    }

    public async Task<string> UploadScreenshotAsync(string blobPath, byte[] data, CancellationToken cancellationToken = default)
    {
        await EnsureContainerExistsAsync(cancellationToken);

        var blobClient = _containerClient.GetBlobClient(blobPath);

        using var stream = new MemoryStream(data, writable: false);
        await blobClient.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "image/png" }
        }, cancellationToken);

        _logger.LogDebug("Uploaded blob {BlobPath} ({Bytes} bytes)", blobPath, data.Length);

        return blobClient.Uri.ToString();
    }

    private async Task EnsureContainerExistsAsync(CancellationToken cancellationToken)
    {
        if (_containerEnsured)
            return;

        await _containerInitLock.WaitAsync(cancellationToken);
        try
        {
            if (_containerEnsured)
                return;

            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: cancellationToken);
            _containerEnsured = true;
        }
        finally
        {
            _containerInitLock.Release();
        }
    }
}
