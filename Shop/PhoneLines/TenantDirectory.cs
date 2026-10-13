using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>
/// <see cref="ITenantDirectory"/> over the TenantSaaS table <c>TenantPhoneEndpoints</c>, read with the site's
/// <c>STORAGE_CONNECTION_STRING</c> (the same account the salon app writes it to): PartitionKey = tenant id,
/// RowKey = "voice"; where that salon answers calls, reported by the salon itself.
/// Every salon binary can write the table, so its rows are claims, not facts: the phone-line service logs
/// what it buys against. Leaf only: one table call per method; an unreachable table reads as "nothing".
/// </summary>
public sealed class TenantDirectory : ITenantDirectory
{
    private const string EndpointTable = "TenantPhoneEndpoints";
    private const string VoiceRow = "voice";

    private readonly ILogger<TenantDirectory> _logger;

    public TenantDirectory(ILogger<TenantDirectory> logger) => _logger = logger;

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

    private static TableClient? Client(string table)
    {
        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(conn) ? null : new TableClient(conn, table);
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
