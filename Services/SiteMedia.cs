using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.Net.Http.Headers;

namespace MapleKiosk.Web.Services;

/// <summary>
/// GET /media/{path} — streams marketing media (product films) from the private blob container
/// <c>site-media</c> via the site's <c>STORAGE_CONNECTION_STRING</c>. The storage account doesn't allow
/// public containers, so the site serves them itself: range requests (seeking in a video) are supported,
/// and the long cache lifetime lets Cloudflare cache them at the edge. Videos stay out of the git repo.
/// </summary>
public static partial class SiteMedia
{
    public const string Container = "site-media";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9/_-]*\.(mp4|webm|jpg|png|webp)$")]
    private static partial Regex SafePath();

    public static IEndpointRouteBuilder MapSiteMedia(this IEndpointRouteBuilder app)
    {
        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        var container = string.IsNullOrWhiteSpace(conn) ? null : new BlobContainerClient(conn, Container);

        app.MapGet("/media/{**path}", async (string path, HttpContext http, CancellationToken ct) =>
        {
            if (container is null || path.Contains("..") || !SafePath().IsMatch(path)) return Results.NotFound();

            var blob = container.GetBlobClient(path);
            Azure.Response<Azure.Storage.Blobs.Models.BlobProperties> props;
            try { props = await blob.GetPropertiesAsync(cancellationToken: ct); }
            catch (Azure.RequestFailedException e) when (e.Status == 404) { return Results.NotFound(); }

            http.Response.Headers.CacheControl = "public, max-age=604800";
            var etag = new EntityTagHeaderValue($"\"{props.Value.ETag.ToString().Trim('"')}\"");
            return Results.File(await blob.OpenReadAsync(cancellationToken: ct), props.Value.ContentType,
                lastModified: props.Value.LastModified, entityTag: etag, enableRangeProcessing: true);
        }).AllowAnonymous();

        return app;
    }
}
