using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>
/// Uploads catalog item images (JPG, PNG or WebP) to the private blob container the site already
/// serves through /media (<see cref="SiteMedia"/>), under <c>catalog/</c>. The storage account doesn't
/// allow public containers, so the site serves them itself, and that public URL is what the shop
/// renders and what Stripe is given for the product's image. Returns the site path (/media/catalog/…)
/// to store on the item's <c>ImageUrl</c>.
/// </summary>
public sealed class BlobImageUploader
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

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
        _container = new BlobContainerClient(conn, SiteMedia.Container);
    }

    public bool IsConfigured => _container is not null;

    public static bool IsSupported(string contentType) => Types.ContainsKey(contentType);

    public async Task<string> UploadAsync(Stream content, string contentType, CancellationToken ct = default)
    {
        if (_container is null) throw new InvalidOperationException("Image storage is not configured.");
        if (!Types.TryGetValue(contentType, out var ext)) throw new ArgumentException("Use a JPG, PNG or WebP image.");

        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct).ConfigureAwait(false);

        var path = $"catalog/{Guid.NewGuid():N}{ext}";
        await _container.GetBlobClient(path).UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType.ToLowerInvariant() } }, ct)
            .ConfigureAwait(false);

        _logger.LogInformation("Catalog image uploaded: {Path}", path);
        return "/media/" + path;
    }
}
