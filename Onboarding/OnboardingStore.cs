using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace MapleKiosk.Web.Onboarding;

/// <summary>Azure Table row for an <see cref="OnboardingRecord"/>. One partition
/// (low volume); RowKey is the token. Form answers and the file list are JSON.</summary>
public sealed class OnboardingEntity : ITableEntity
{
    public const string Partition = "onboarding";

    public string PartitionKey { get; set; } = Partition;
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string BusinessName { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string Industry { get; set; } = "";
    public string Language { get; set; } = "en";
    public string? OrderRef { get; set; }
    public string Status { get; set; } = nameof(OnboardingStatus.Sent);
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string FormJson { get; set; } = "{}";
    public string FilesJson { get; set; } = "[]";
    public bool IncludesWebsite { get; set; }

    public static OnboardingEntity FromRecord(OnboardingRecord r) => new()
    {
        RowKey = r.Token,
        BusinessName = r.BusinessName,
        ContactEmail = OnboardingRecord.CanonicalEmail(r.ContactEmail),
        Industry = r.Industry,
        Language = r.Language,
        OrderRef = r.OrderRef,
        Status = r.Status.ToString(),
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        SubmittedAt = r.SubmittedAt,
        FormJson = JsonSerializer.Serialize(r.Form),
        FilesJson = JsonSerializer.Serialize(r.Files),
        IncludesWebsite = r.IncludesWebsite
    };

    public OnboardingRecord ToRecord() => new()
    {
        Token = RowKey,
        BusinessName = BusinessName,
        ContactEmail = ContactEmail,
        Industry = Industry,
        Language = Language,
        OrderRef = OrderRef,
        Status = Enum.TryParse<OnboardingStatus>(Status, out var s) ? s : OnboardingStatus.Sent,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        SubmittedAt = SubmittedAt,
        Form = JsonSerializer.Deserialize<OnboardingForm>(FormJson) ?? new(),
        Files = JsonSerializer.Deserialize<List<OnboardingFile>>(FilesJson) ?? new(),
        IncludesWebsite = IncludesWebsite
    };
}

/// <summary>
/// Persists onboarding records (table <c>onboarding</c>) and the customer's
/// uploaded files (private blob container <c>onboarding-files</c>, one folder per
/// token) via the site's <c>STORAGE_CONNECTION_STRING</c>. Files are only served
/// back through the signed-in download endpoint, never by public URL.
/// </summary>
public sealed class OnboardingStore
{
    public const string TableName = "onboarding";
    public const string Container = "onboarding-files";
    public const string BriefTableName = "onboardingwebsite";
    private const string BriefPartition = "website";

    private readonly ILogger<OnboardingStore> _logger;
    private readonly TableClient? _table;
    private readonly TableClient? _briefs;
    private readonly BlobContainerClient? _blobs;

    public OnboardingStore(ILogger<OnboardingStore> logger)
    {
        _logger = logger;

        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — onboarding is unavailable.");
            return;
        }

        _table = new TableClient(conn, TableName);
        _briefs = new TableClient(conn, BriefTableName);
        _blobs = new BlobContainerClient(conn, Container);
        try
        {
            _table.CreateIfNotExists();
            _briefs.CreateIfNotExists();
            _blobs.CreateIfNotExists(PublicAccessType.None);
        }
        catch (Exception ex) { _logger.LogError(ex, "Could not ensure onboarding table/container exist."); }
    }

    public bool IsConfigured => _table is not null;

    public async Task<IReadOnlyList<OnboardingRecord>> GetAllAsync(CancellationToken ct = default)
    {
        if (_table is null) return Array.Empty<OnboardingRecord>();
        var list = new List<OnboardingRecord>();
        await foreach (var e in _table.QueryAsync<OnboardingEntity>(
            filter: $"PartitionKey eq '{OnboardingEntity.Partition}'", cancellationToken: ct).ConfigureAwait(false))
        {
            list.Add(e.ToRecord());
        }
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task<OnboardingRecord?> FindAsync(string? token, CancellationToken ct = default)
    {
        if (_table is null || !OnboardingRecord.IsValidToken(token)) return null;
        var res = await _table.GetEntityIfExistsAsync<OnboardingEntity>(OnboardingEntity.Partition, token!, cancellationToken: ct)
            .ConfigureAwait(false);
        return res.HasValue ? res.Value!.ToRecord() : null;
    }

    /// <summary>Records whose customer login email matches (case-insensitive;
    /// emails are stored lowercase). Drives both the login allowlist and the
    /// customer's "my setup" page.</summary>
    public async Task<IReadOnlyList<OnboardingRecord>> FindByEmailAsync(string? email, CancellationToken ct = default)
    {
        if (_table is null || string.IsNullOrWhiteSpace(email)) return Array.Empty<OnboardingRecord>();
        var canonical = OnboardingRecord.CanonicalEmail(email);
        var list = new List<OnboardingRecord>();
        await foreach (var e in _table.QueryAsync<OnboardingEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {OnboardingEntity.Partition} and ContactEmail eq {canonical}"),
            cancellationToken: ct).ConfigureAwait(false))
        {
            list.Add(e.ToRecord());
        }
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task SaveAsync(OnboardingRecord record, CancellationToken ct = default)
    {
        if (_table is null) throw new InvalidOperationException("Onboarding storage is not configured.");
        await _table.UpsertEntityAsync(OnboardingEntity.FromRecord(record), TableUpdateMode.Replace, ct).ConfigureAwait(false);
    }

    // --- Website brief: its own row so the brief and the setup form never overwrite each other ---

    public async Task<WebsiteBrief> GetBriefAsync(string token, CancellationToken ct = default)
    {
        if (_briefs is null || !OnboardingRecord.IsValidToken(token)) return new();
        var res = await _briefs.GetEntityIfExistsAsync<TableEntity>(BriefPartition, token, cancellationToken: ct).ConfigureAwait(false);
        return res.HasValue && res.Value!.GetString("BriefJson") is { } json
            ? JsonSerializer.Deserialize<WebsiteBrief>(json) ?? new()
            : new();
    }

    public async Task SaveBriefAsync(string token, WebsiteBrief brief, CancellationToken ct = default)
    {
        if (_briefs is null) throw new InvalidOperationException("Onboarding storage is not configured.");
        var entity = new TableEntity(BriefPartition, token) { ["BriefJson"] = JsonSerializer.Serialize(brief) };
        await _briefs.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct).ConfigureAwait(false);
    }

    public async Task<OnboardingFile> UploadFileAsync(string token, Stream content, string fileName, string contentType,
        CancellationToken ct = default)
    {
        if (_blobs is null) throw new InvalidOperationException("File storage is not configured.");

        var file = new OnboardingFile
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = Path.GetFileName(fileName),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Size = content.CanSeek ? content.Length : 0
        };

        await _blobs.GetBlobClient(BlobName(token, file.Id))
            .UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = file.ContentType } }, ct)
            .ConfigureAwait(false);

        _logger.LogInformation("Onboarding file uploaded for {Token}: {File}", token, file.FileName);
        return file;
    }

    public async Task<Stream?> OpenFileAsync(string token, string fileId, CancellationToken ct = default)
    {
        if (_blobs is null) return null;
        var blob = _blobs.GetBlobClient(BlobName(token, fileId));
        if (!await blob.ExistsAsync(ct).ConfigureAwait(false)) return null;
        return await blob.OpenReadAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(string token, string fileId, CancellationToken ct = default)
    {
        if (_blobs is null) return;
        await _blobs.GetBlobClient(BlobName(token, fileId)).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    private static string BlobName(string token, string fileId) => $"{token}/{fileId}";
}
