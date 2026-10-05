using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MapleKiosk.Web.Onboarding;

/// <summary>
/// One customer's onboarding as a zip for the web designer: <c>index.html</c> (setup answers and
/// website brief, linking the files inside the zip), every uploaded file under <c>files/</c> with
/// its original name, and <c>data/</c> with the stored rows verbatim so nothing the customer typed
/// is left out. Strictly read-only: it never saves, moves or deletes anything.
/// </summary>
public sealed class DesignerPackage
{
    private readonly OnboardingStore _store;
    private readonly ILogger<DesignerPackage> _logger;

    public DesignerPackage(OnboardingStore store, ILogger<DesignerPackage> logger)
    {
        _store = store;
        _logger = logger;
    }

    public static string ZipName(OnboardingRecord r)
        => $"{Slug(r.Form.BusinessName is { Length: > 0 } n ? n : r.BusinessName)}-designer-package-{DateTime.UtcNow:yyyyMMdd}.zip";

    /// <summary>Writes the zip to <paramref name="output"/> (a seekable stream; ZipArchive writes synchronously).</summary>
    public async Task WriteAsync(OnboardingRecord r, Stream output, CancellationToken ct = default)
    {
        var brief = await _store.GetBriefAsync(r.Token, ct).ConfigureAwait(false);
        var (rawRecord, rawBrief) = await _store.GetRawRowsAsync(r.Token, ct).ConfigureAwait(false);

        // Every file the customer gave us, once each, in a folder by where they gave it.
        var files = new List<(OnboardingFile File, string Folder)>();
        if (r.Form.Logo is { } logo) files.Add((logo, "logo"));
        files.AddRange(brief.Files.Select(f => (f, "brand")));
        files.AddRange(r.Files.Select(f => (f, "setup")));
        files = files.DistinctBy(x => x.File.Id).ToList();

        var paths = new Dictionary<string, string>(); // file id → path in the zip
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var (file, folder) in files)
        {
            var path = Unique($"files/{folder}/{SafeFileName(file.FileName, file.Id)}", used);
            await using var blob = await _store.OpenFileAsync(r.Token, file.Id, ct).ConfigureAwait(false);
            if (blob is null)
            {
                missing.Add($"{folder}: {file.FileName} (id {file.Id}, {file.Size:N0} bytes, uploaded {file.UploadedAt:yyyy-MM-dd HH:mm} UTC)");
                continue;
            }
            // Photos and PDFs are already compressed.
            var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
            entry.LastWriteTime = file.UploadedAt;
            await using (var dest = entry.Open())
                await blob.CopyToAsync(dest, ct).ConfigureAwait(false);
            paths[file.Id] = path;
        }

        string Link(OnboardingFile f) => paths.TryGetValue(f.Id, out var p) ? p : "#missing";

        await WriteTextAsync(zip, "index.html", IndexHtml(r, brief, Link, missing));

        // The stored rows as they are: if a field isn't shown above, it's still here.
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        await WriteTextAsync(zip, "data/setup-form.raw.json", JsonSerializer.Serialize(rawRecord, json));
        if (rawBrief is not null) await WriteTextAsync(zip, "data/website-brief.raw.json", JsonSerializer.Serialize(rawBrief, json));
        await WriteTextAsync(zip, "data/setup-form.json", JsonSerializer.Serialize(r, json));
        await WriteTextAsync(zip, "data/website-brief.json", JsonSerializer.Serialize(brief, json));

        if (missing.Count > 0)
        {
            await WriteTextAsync(zip, "MISSING-FILES.txt",
                "These files are listed on the customer's record but weren't found in storage:\r\n\r\n" + string.Join("\r\n", missing) + "\r\n");
            _logger.LogWarning("Designer package for {Token}: {Count} file(s) not found in storage.", r.Token, missing.Count);
        }

        _logger.LogInformation("Designer package built for {Business} ({Token}): {Files} file(s).", r.Form.BusinessName, r.Token, paths.Count);
    }

    private static string IndexHtml(OnboardingRecord r, WebsiteBrief brief, Func<OnboardingFile, string> link, List<string> missing)
    {
        var sb = new StringBuilder()
            .Append("<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>")
            .Append($"<title>{Enc(r.Form.BusinessName)} — designer package</title></head>")
            .Append("<body style='font-family:Arial,Helvetica,sans-serif;color:#111;max-width:860px;margin:24px auto;padding:0 16px'>")
            .Append($"<p style='color:#666'>Exported {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC from MapleKiosk onboarding. Files are in the <code>files</code> folder; ")
            .Append("<code>data</code> has everything the customer entered, as stored.</p>");

        if (missing.Count > 0)
            sb.Append($"<p style='color:#c0392b'><strong>{missing.Count} file(s) could not be found in storage</strong> — see MISSING-FILES.txt.</p>");

        if (r.IncludesWebsite || brief.UpdatedAt is not null || brief.Files.Count > 0)
            sb.Append("<h1 style='font-size:22px'>Website brief</h1>").Append(OnboardingService.BriefHtml(r, brief, link));
        else
            sb.Append("<p style='color:#b26a00'>This customer's scope has no website brief.</p>");

        sb.Append("<h1 style='font-size:22px;margin-top:40px'>Business setup</h1>")
          .Append(OnboardingService.SummaryHtml(r, link))
          .Append("</body></html>");
        return sb.ToString();
    }

    private static async Task WriteTextAsync(ZipArchive zip, string path, string text)
    {
        await using var s = zip.CreateEntry(path).Open();
        await s.WriteAsync(new UTF8Encoding(false).GetBytes(text));
    }

    private static string SafeFileName(string name, string fallback)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        n = new string(n.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return n.Length == 0 ? fallback : n.Length > 120 ? n[..80] + Path.GetExtension(n) : n;
    }

    private static string Unique(string path, HashSet<string> used)
    {
        if (used.Add(path)) return path;
        var dir = Path.GetDirectoryName(path)!.Replace('\\', '/');
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = $"{dir}/{stem} ({i}){ext}";
            if (used.Add(p)) return p;
        }
    }

    private static string Slug(string s)
    {
        var slug = new string((s ?? "").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "customer" : slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
