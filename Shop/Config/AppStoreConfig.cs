using Azure.Data.Tables;

namespace MapleKiosk.Web.Shop.Config;

/// <summary>
/// Config-free access to checkout settings. Each value resolves env var first,
/// then the Azure Table key-value store (partition <c>AppStore</c>, column
/// <c>Value</c>) read via the site's existing <c>STORAGE_CONNECTION_STRING</c>.
/// Mirrors the monorepo IKeyVault convention without dragging in its stack, so
/// no secrets live in committed config. Cached per process after first read.
/// </summary>
public sealed class AppStoreConfig
{
    private readonly ILogger<AppStoreConfig> _logger;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TableClient? _table;
    private bool _tableResolved;

    public AppStoreConfig(ILogger<AppStoreConfig> logger) => _logger = logger;

    public Task<string> GetApiKeyAsync() => GetAsync("APPSTORE_API_KEY", "ApiKey");
    public Task<string> GetCatalogJsonAsync() => GetAsync("APPSTORE_CATALOG", "Catalog");
    public Task<string> GetStripeSecretKeyAsync() => GetAsync("APPSTORE_STRIPE_SECRET_KEY", "StripeSecretKey");
    public Task<string> GetStripeWebhookSecretAsync() => GetAsync("APPSTORE_STRIPE_WEBHOOK_SECRET", "StripeWebhookSecret");
    public Task<string> GetVietQrBankCodeAsync() => GetAsync("APPSTORE_VIETQR_BANK_CODE", "VietQrBankCode");
    public Task<string> GetVietQrAccountNumberAsync() => GetAsync("APPSTORE_VIETQR_ACCOUNT_NUMBER", "VietQrAccountNumber");
    public Task<string> GetVietQrAccountNameAsync() => GetAsync("APPSTORE_VIETQR_ACCOUNT_NAME", "VietQrAccountName");
    public Task<string> GetBankAggregatorSecretAsync() => GetAsync("APPSTORE_SEPAY_SECRET", "SepaySecret");
    public Task<string> GetOutboundWebhookUrlAsync() => GetAsync("APPSTORE_OUTBOUND_WEBHOOK_URL", "OutboundWebhookUrl");
    public Task<string> GetOutboundWebhookSecretAsync() => GetAsync("APPSTORE_OUTBOUND_WEBHOOK_SECRET", "OutboundWebhookSecret");
    public Task<string> GetOrderInboxAsync() => GetAsync("APPSTORE_ORDER_INBOX", "OrderInbox");

    private async Task<string> GetAsync(string envVar, string rowKey)
    {
        // Source of truth is Azure Storage (keyvalue table). Env var is the only
        // non-storage fallback (operator/systemd); nothing is read from appsettings.
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        if (_cache.TryGetValue(rowKey, out var cached)) return cached;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(rowKey, out cached)) return cached;

            var value = "";
            var table = GetTable();
            if (table is not null)
            {
                try
                {
                    var resp = await table.GetEntityIfExistsAsync<TableEntity>("AppStore", rowKey).ConfigureAwait(false);
                    if (resp.HasValue && resp.Value!.TryGetValue("Value", out var v))
                        value = v?.ToString()?.Trim() ?? "";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AppStore config read failed for {RowKey}.", rowKey);
                }
            }

            _cache[rowKey] = value;
            return value;
        }
        finally { _lock.Release(); }
    }

    private TableClient? GetTable()
    {
        if (_tableResolved) return _table;
        _tableResolved = true;

        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — AppStore config falls back to env vars only.");
            return null;
        }

        var tableName = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Production", StringComparison.OrdinalIgnoreCase)
            ? "keyvalueProduction"
            : "keyvalue";

        _table = new TableClient(conn, tableName);
        return _table;
    }
}
