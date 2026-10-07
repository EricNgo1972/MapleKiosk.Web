using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>
/// The one catalog of everything the site sells: categories and items (product or service, price,
/// one-time/monthly/yearly), in table <c>appstorecatalog</c> via the site's <c>STORAGE_CONNECTION_STRING</c>.
/// Managed at /shop/admin (which also syncs each item to Stripe), read by the pricing pages, the shop,
/// both checkouts and the assistant. A fresh table is seeded with <see cref="CatalogDefaults"/>; without
/// storage the defaults are served read-only. Reads are cached briefly; every write clears the cache.
/// </summary>
public sealed partial class CatalogStore : IAppCatalog
{
    public const string TableName = "appstorecatalog";

    /// <summary>Every catalog price is in this currency (Stripe checkout and the pricing pages).</summary>
    public const string Currency = "CAD";

    // The catalog changes only from /shop/admin, which writes through this store (so its own changes show
    // at once). Past this age a read still answers from memory and refreshes in the background, so no
    // visitor ever waits on the table; that only picks up rows edited outside the admin.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly ILogger<CatalogStore> _logger;
    private readonly TableClient? _table;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _cache;
    private DateTime _cachedAtUtc;
    private int _refreshing;

    private sealed record Snapshot(IReadOnlyList<CatalogCategory> Categories, IReadOnlyList<AppProduct> Products);

    public CatalogStore(ILogger<CatalogStore> logger)
    {
        _logger = logger;

        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — serving the default catalog read-only.");
            return;
        }

        _table = new TableClient(conn, TableName);
        try { _table.CreateIfNotExists(); }
        catch (Exception ex) { _logger.LogError(ex, "Could not ensure catalog table exists."); }
    }

    public bool IsConfigured => _table is not null;

    // --- Reads ---

    public async Task<IReadOnlyList<CatalogCategory>> GetCategoriesAsync(CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).Categories;

    /// <summary>Every item, inactive too, in category order then their own.</summary>
    public async Task<IReadOnlyList<AppProduct>> GetAllAsync(CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).Products;

    /// <summary>The shop's catalog (IAppCatalog): every active item — the shop sells the whole catalog.</summary>
    public async Task<IReadOnlyList<AppProduct>> GetActiveAsync(CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).Products.Where(p => p.Active).ToList();

    /// <summary>Every category with its active items, in order: the pricing pages' quote builder and the shop
    /// both sell all of them.</summary>
    public async Task<IReadOnlyList<(CatalogCategory Category, IReadOnlyList<AppProduct> Items)>> GetGroupsAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync(ct).ConfigureAwait(false);
        return s.Categories
            .Select(c => (c, (IReadOnlyList<AppProduct>)s.Products.Where(p => p.Active && p.Category == c.Key).ToList()))
            .Where(g => g.Item2.Count > 0)
            .ToList();
    }

    /// <summary>An active item by SKU (checkout).</summary>
    public async Task<AppProduct?> FindAsync(string sku, CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).Products.FirstOrDefault(p => p.Active && p.Sku == sku);

    // --- Writes (admin) ---

    public async Task UpsertAsync(AppProduct product, CancellationToken ct = default)
    {
        var table = Writable();
        if (!IsValidSku(product.Sku)) throw new ArgumentException("SKU must be 1–64 chars: letters, digits, dash or underscore.");
        await table.UpsertEntityAsync(CatalogProductEntity.FromProduct(product), TableUpdateMode.Replace, ct).ConfigureAwait(false);
        _cache = null;
        _logger.LogInformation("Catalog item saved: {Sku}", product.Sku);
    }

    public async Task DeleteAsync(string sku, CancellationToken ct = default)
    {
        var table = Writable();
        await table.DeleteEntityAsync(CatalogProductEntity.Partition, sku, ETag.All, ct).ConfigureAwait(false);
        _cache = null;
        _logger.LogInformation("Catalog item deleted: {Sku}", sku);
    }

    public async Task UpsertCategoryAsync(CatalogCategory category, CancellationToken ct = default)
    {
        var table = Writable();
        if (!IsValidSku(category.Key)) throw new ArgumentException("Category key must be 1–64 chars: letters, digits, dash or underscore.");
        await table.UpsertEntityAsync(CatalogCategoryEntity.From(category), TableUpdateMode.Replace, ct).ConfigureAwait(false);
        _cache = null;
    }

    public async Task DeleteCategoryAsync(string key, CancellationToken ct = default)
    {
        var table = Writable();
        if ((await GetAllAsync(ct).ConfigureAwait(false)).Any(p => p.Category == key))
            throw new InvalidOperationException("Move or delete this category's items first.");
        await table.DeleteEntityAsync(CatalogCategoryEntity.Partition, key, ETag.All, ct).ConfigureAwait(false);
        _cache = null;
    }

    private TableClient Writable() => _table ?? throw new InvalidOperationException("Catalog storage is not configured (STORAGE_CONNECTION_STRING).");

    public static bool IsValidSku(string sku) => !string.IsNullOrWhiteSpace(sku) && SkuRegex().IsMatch(sku);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SkuRegex();

    // --- Loading ---

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } hit)
        {
            if (DateTime.UtcNow - _cachedAtUtc >= CacheTtl && Interlocked.Exchange(ref _refreshing, 1) == 0)
                _ = Task.Run(async () =>
                {
                    try { await LoadFreshAsync(CancellationToken.None).ConfigureAwait(false); }
                    finally { Volatile.Write(ref _refreshing, 0); }
                });
            return hit;
        }
        return await LoadFreshAsync(ct).ConfigureAwait(false);
    }

    // Reads the table (once at a time); the first read of the process, and the one after an admin write, wait for it.
    private async Task<Snapshot> LoadFreshAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return again;

            Snapshot snap;
            if (_table is null)
            {
                snap = Order(CatalogDefaults.Categories, CatalogDefaults.Products);
            }
            else
            {
                try { snap = await ReadAsync(_table, ct).ConfigureAwait(false); }
                catch (Exception ex) when (_cache is not null)
                {
                    _logger.LogWarning(ex, "Catalog read failed; serving the last one.");
                    return _cache;
                }
            }

            _cache = snap;
            _cachedAtUtc = DateTime.UtcNow;
            return snap;
        }
        finally { _lock.Release(); }
    }

    private async Task<Snapshot> ReadAsync(TableClient table, CancellationToken ct)
    {
        var categories = new List<CatalogCategory>();
        await foreach (var e in table.QueryAsync<CatalogCategoryEntity>(
            filter: $"PartitionKey eq '{CatalogCategoryEntity.Partition}'", cancellationToken: ct).ConfigureAwait(false))
            categories.Add(e.ToCategory());

        var products = new List<AppProduct>();
        await foreach (var e in table.QueryAsync<CatalogProductEntity>(
            filter: $"PartitionKey eq '{CatalogProductEntity.Partition}'", cancellationToken: ct).ConfigureAwait(false))
            products.Add(e.ToProduct());

        // First run on this table: seed the default categories and items.
        if (categories.Count == 0)
        {
            foreach (var c in CatalogDefaults.Categories)
            {
                await table.UpsertEntityAsync(CatalogCategoryEntity.From(c), TableUpdateMode.Replace, ct).ConfigureAwait(false);
                categories.Add(c);
            }
            foreach (var p in CatalogDefaults.Products.Where(d => products.All(p => p.Sku != d.Sku)))
            {
                await table.UpsertEntityAsync(CatalogProductEntity.FromProduct(p), TableUpdateMode.Replace, ct).ConfigureAwait(false);
                products.Add(p);
            }
            _logger.LogInformation("Catalog seeded with the default categories and items.");
        }
        else
        {
            // A category added to the defaults after the table was seeded (e.g. gift cards) arrives once, with
            // its items. To retire one later, set its items inactive at /shop/admin rather than deleting the
            // category, or it comes back on the next read.
            foreach (var c in CatalogDefaults.Categories.Where(d => categories.All(c => c.Key != d.Key)))
            {
                await table.UpsertEntityAsync(CatalogCategoryEntity.From(c), TableUpdateMode.Replace, ct).ConfigureAwait(false);
                categories.Add(c);
                foreach (var p in CatalogDefaults.Products.Where(d => d.Category == c.Key && products.All(p => p.Sku != d.Sku)))
                {
                    await table.UpsertEntityAsync(CatalogProductEntity.FromProduct(p), TableUpdateMode.Replace, ct).ConfigureAwait(false);
                    products.Add(p);
                }
                _logger.LogInformation("Catalog: added the new default category {Key} with its items.", c.Key);
            }
        }

        return Order(categories, products);
    }

    private static Snapshot Order(IEnumerable<CatalogCategory> categories, IEnumerable<AppProduct> products)
    {
        var cats = categories.OrderBy(c => c.Sort).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rank = cats.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var items = products
            .OrderBy(p => rank.TryGetValue(p.Category, out var r) ? r : int.MaxValue)
            .ThenBy(p => p.Sort).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Snapshot(cats, items);
    }
}
