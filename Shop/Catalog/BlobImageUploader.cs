using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>
/// Uploads catalog plan images to Blob storage (container <c>catalog-images</c>,
/// public-blob read so the URLs render in the storefront) using the site's
/// <c>STORAGE_CONNECTION_STRING</c>. Returns the public blob URL to store on the
/// product's <c>ImageUrl</c>.
/// </summary>
public sealed class BlobImageUploader
{
    public const string Container = "catalog-images";

    private readonly ILogger<BlobImageUploader> _logger;
    private readonly BlobContainerClient? _container;

    public BlobImageUploader(ILogger<BlobImageUploader> logger)
    {
        _logger = logger;
        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — catalog image upload disabled.");
            return;
        }
        _container = new BlobContainerClient(conn, Container);
    }

    public bool IsConfigured => _container is not null;

    public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        if (_container is null) throw new InvalidOperationException("Image storage is not configured.");

        await _container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct).ConfigureAwait(false);

        var ext = Path.GetExtension(fileName);
        var blobName = $"{Guid.NewGuid():N}{ext}";
        var blob = _container.GetBlobClient(blobName);

        await blob.UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct)
            .ConfigureAwait(false);

        _logger.LogInformation("Catalog image uploaded: {Blob}", blobName);
        return blob.Uri.ToString();
    }
}
