using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>
/// Azure-Table-backed product catalog. Serves the checkout read path
/// (<see cref="IAppCatalog"/>) and the admin write path (Get all / Upsert /
/// Delete). One row per product in table <c>appstorecatalog</c>, via the site's
/// <c>STORAGE_CONNECTION_STRING</c>.
/// </summary>
public sealed partial class CatalogStore : IAppCatalog
{
    public const string TableName = "appstorecatalog";

    private readonly ILogger<CatalogStore> _logger;
    private readonly TableClient? _table;

    public CatalogStore(ILogger<CatalogStore> logger)
    {
        _logger = logger;

        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — product catalog is unavailable.");
            return;
        }

        _table = new TableClient(conn, TableName);
        try { _table.CreateIfNotExists(); }
        catch (Exception ex) { _logger.LogError(ex, "Could not ensure catalog table exists."); }
    }

    public bool IsConfigured => _table is not null;

    // --- Admin (all products, incl. inactive) ---

    public async Task<IReadOnlyList<AppProduct>> GetAllAsync(CancellationToken ct = default)
    {
        if (_table is null) return Array.Empty<AppProduct>();
        var list = new List<AppProduct>();
        await foreach (var e in _table.QueryAsync<CatalogProductEntity>(
            filter: $"PartitionKey eq '{CatalogProductEntity.Partition}'", cancellationToken: ct).ConfigureAwait(false))
        {
            list.Add(e.ToProduct());
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task UpsertAsync(AppProduct product, CancellationToken ct = default)
    {
        if (_table is null) throw new InvalidOperationException("Catalog storage is not configured.");
        if (!IsValidSku(product.Sku)) throw new ArgumentException("SKU must be 1–64 chars: letters, digits, dash or underscore.");

        await _table.UpsertEntityAsync(CatalogProductEntity.FromProduct(product), TableUpdateMode.Replace, ct)
            .ConfigureAwait(false);
        _logger.LogInformation("Catalog product upserted: {Sku}", product.Sku);
    }

    public async Task DeleteAsync(string sku, CancellationToken ct = default)
    {
        if (_table is null || string.IsNullOrWhiteSpace(sku)) return;
        await _table.DeleteEntityAsync(CatalogProductEntity.Partition, sku, ETag.All, ct).ConfigureAwait(false);
        _logger.LogInformation("Catalog product deleted: {Sku}", sku);
    }

    // --- IAppCatalog (checkout read path; active only) ---

    public async Task<IReadOnlyList<AppProduct>> GetActiveAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        return all.Where(p => p.Active).ToList();
    }

    public async Task<AppProduct?> FindAsync(string sku, CancellationToken ct = default)
    {
        if (_table is null || string.IsNullOrWhiteSpace(sku)) return null;
        var res = await _table.GetEntityIfExistsAsync<CatalogProductEntity>(
            CatalogProductEntity.Partition, sku, cancellationToken: ct).ConfigureAwait(false);
        var product = res.HasValue ? res.Value!.ToProduct() : null;
        return product is { Active: true } ? product : null;
    }

    public static bool IsValidSku(string sku) => !string.IsNullOrWhiteSpace(sku) && SkuRegex().IsMatch(sku);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SkuRegex();
}
