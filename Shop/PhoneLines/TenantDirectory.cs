using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>
/// <see cref="ITenantDirectory"/> over the TenantSaaS tables, read with the site's <c>STORAGE_CONNECTION_STRING</c>
/// (the same account the salon app writes them to).
/// <list type="bullet">
/// <item><c>TenantActivation</c>: PartitionKey = the owner's email (lower-case); a row with <c>IsActivated</c>
/// and a <c>TenantId</c> is a salon that email owns.</item>
/// <item><c>TenantPhoneEndpoints</c>: PartitionKey = tenant id, RowKey = "voice"; where that salon answers
/// calls, reported by the salon itself.</item>
/// </list>
/// Every salon binary can write both tables, so their rows are claims, not facts: the phone-line service logs
/// what it buys against. Leaf only: one table call per method; an unreachable table reads as "nothing".
/// </summary>
public sealed class TenantDirectory : ITenantDirectory
{
    private const string ActivationTable = "TenantActivation";
    private const string EndpointTable = "TenantPhoneEndpoints";
    private const string VoiceRow = "voice";

    private readonly ILogger<TenantDirectory> _logger;

    public TenantDirectory(ILogger<TenantDirectory> logger) => _logger = logger;

    public async Task<IReadOnlyList<OwnedSalon>> ListOwnedAsync(string ownerEmail, CancellationToken ct = default)
    {
        var client = Client(ActivationTable);
        if (client is null) return [];
        try
        {
            return await QueryOwnedAsync(client, ownerEmail, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant directory: listing the salons of {Email} failed.", ownerEmail);
            return [];
        }
    }

    public async Task<SalonPhoneEndpoint?> GetPhoneEndpointAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        var client = Client(EndpointTable);
        if (client is null) return null;

        try
        {
            var hit = await client.GetEntityIfExistsAsync<EndpointRow>(tenantId.Trim(), VoiceRow, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!hit.HasValue || hit.Value is not { } row) return null;
            return new SalonPhoneEndpoint(row.PartitionKey, row.SalonName ?? "", row.BaseUrl ?? "", row.IncomingUrl ?? "",
                row.FallbackUrl ?? "", row.StatusUrl ?? "", row.ReportedAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant directory: reading the phone endpoint of {TenantId} failed.", tenantId);
            return null;
        }
    }

    /// <summary>Whether <paramref name="email"/> owns any activated salon: the sign-in allowlist's salon-owner check
    /// (static, like the other allowlist lookups, so it needs no DI). False when storage is unreachable.</summary>
    public static async Task<bool> OwnsAnySalonAsync(string email, CancellationToken ct = default)
    {
        var client = Client(ActivationTable);
        if (client is null) return false;
        try
        {
            return (await QueryOwnedAsync(client, email, ct).ConfigureAwait(false)).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<OwnedSalon>> QueryOwnedAsync(TableClient client, string ownerEmail, CancellationToken ct)
    {
        var pk = (ownerEmail ?? "").Trim().ToLowerInvariant();
        if (pk.Length == 0) return [];

        var owned = new List<OwnedSalon>();
        await foreach (var row in client.QueryAsync<ActivationRow>(r => r.PartitionKey == pk, cancellationToken: ct).ConfigureAwait(false))
        {
            if (!row.IsActivated || string.IsNullOrWhiteSpace(row.TenantId)) continue;
            if (owned.Any(o => string.Equals(o.TenantId, row.TenantId.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            owned.Add(new OwnedSalon(row.TenantId.Trim(), string.IsNullOrWhiteSpace(row.Product) ? null : row.Product));
        }
        return owned;
    }

    private static TableClient? Client(string table)
    {
        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(conn) ? null : new TableClient(conn, table);
    }

    private sealed class ActivationRow : ITableEntity
    {
        public string PartitionKey { get; set; } = "";
        public string RowKey { get; set; } = "";
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string? TenantId { get; set; }
        public bool IsActivated { get; set; }
        public string? Product { get; set; }
    }

    private sealed class EndpointRow : ITableEntity
    {
        public string PartitionKey { get; set; } = "";
        public string RowKey { get; set; } = "";
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string? SalonName { get; set; }
        public string? BaseUrl { get; set; }
        public string? IncomingUrl { get; set; }
        public string? FallbackUrl { get; set; }
        public string? StatusUrl { get; set; }
        public DateTimeOffset? ReportedAtUtc { get; set; }
    }
}
