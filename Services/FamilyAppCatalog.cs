using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Services;

/// <summary>
/// Azure-Table-backed catalog for the "MapleKiosk family" apps section — one row
/// per product, one card each. The product list is defined once in
/// <see cref="DefaultApps"/>; it seeds the table on first run and also serves as
/// the in-memory fallback when storage isn't configured (so the marketing section
/// always renders). Uses the site's <c>STORAGE_CONNECTION_STRING</c>.
/// </summary>
public sealed class FamilyAppCatalog
{
    public const string TableName = "familyapps";
    private const string Partition = "app";

    private readonly ILogger<FamilyAppCatalog> _logger;
    private readonly TableClient? _table;
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private bool _synced;

    public FamilyAppCatalog(ILogger<FamilyAppCatalog> logger, IConfiguration config)
    {
        _logger = logger;

        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING")
                   ?? config["STORAGE_CONNECTION_STRING"];

        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — MapleKiosk family apps fall back to built-in defaults.");
            return;
        }

        try
        {
            _table = new TableClient(conn, TableName);
            _table.CreateIfNotExists();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not ensure '{Table}' table exists.", TableName);
            _table = null;
        }
    }

    public bool IsConfigured => _table is not null;

    // ───────────────────────────────────────────────────────────────────────
    //  👇  DEFINE YOUR PRODUCTS HERE  — edit this list to change the cards.
    //      Seeds Azure Storage on first run; also the offline fallback.
    // ───────────────────────────────────────────────────────────────────────
    public static IReadOnlyList<FamilyApp> DefaultApps() => new[]
    {
        // The MapleKiosk apps for small businesses, each built for an industry. There is
        // no standalone "POS" app — point of sale is built into the front-of-house
        // products (Coffee, RES, SPA). IndustryKey = translation key for the card's
        // industry label; PageSlug = dedicated page (null ⇒ card is not a link yet).
        // SaaS = fixed monthly price; on-premise = quoted (OnPremiseUsd null ⇒ "Call").
        new FamilyApp { SortOrder = 1, Number = "01", Icon = "☕", Title = "MapleCoffee", IndustryKey = "apps.ind.coffee", PageSlug = "/coffee",      Description = "Café and coffee-shop POS — fast counter checkout, tabs, modifiers, order-ahead and loyalty, sized for a small crew.", SaasMonthlyUsd = 39, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 2, Number = "02", Icon = "🍽️", Title = "MapleRES",    IndustryKey = "apps.ind.res",    PageSlug = "/restaurants", Description = "Restaurant POS and management for small dining rooms and takeout — floor plan, tableside orders, kitchen tickets and reservations.", SaasMonthlyUsd = 49, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 3, Number = "03", Icon = "💅", Title = "MapleSPA",    IndustryKey = "apps.ind.spa",    PageSlug = "/nails",       Description = "Salon POS and booking for nail and beauty shops — appointments, walk-in turns, and technician commissions and tips.", IsFeatured = true, SaasMonthlyUsd = 44, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 4, Number = "04", Icon = "🛠️", Title = "MapleEAM",    IndustryKey = "apps.ind.eam",    PageSlug = null,           Description = "Maintenance and asset tracking for small shops and garages — work orders, preventive schedules, repair history and uptime.", SaasMonthlyUsd = 39, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 5, Number = "05", Icon = "📒", Title = "MapleGL",     IndustryKey = "apps.ind.gl",     PageSlug = null,           Description = "The small-business books, sorted — general ledger, invoicing, expenses and tax-ready reports in one place.", SaasMonthlyUsd = 49, OnPremiseUsd = null },
    };

    /// <summary>Active products, in display order — one card each.</summary>
    public async Task<IReadOnlyList<FamilyApp>> GetAppsAsync(CancellationToken ct = default)
    {
        if (_table is null) return DefaultApps();

        await EnsureDefaultsSyncedAsync(ct).ConfigureAwait(false);

        var entities = new List<FamilyAppEntity>();
        try
        {
            await foreach (var e in _table.QueryAsync<FamilyAppEntity>(
                filter: $"PartitionKey eq '{Partition}'", cancellationToken: ct).ConfigureAwait(false))
            {
                entities.Add(e);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read family apps from Azure Table; using defaults.");
            return DefaultApps();
        }

        if (entities.Count == 0) return DefaultApps();

        return entities.Where(e => e.IsActive).Select(e => e.ToApp()).OrderBy(a => a.SortOrder).ToList();
    }

    /// <summary>
    /// Syncs <see cref="DefaultApps"/> into the table once per process. While there
    /// is no admin editor for the family apps, code is the source of truth, so this
    /// keeps storage current when the defaults (e.g. prices) change. When an admin
    /// editor is added, switch this to seed only when the table is empty.
    /// </summary>
    private async Task EnsureDefaultsSyncedAsync(CancellationToken ct)
    {
        if (_synced || _table is null) return;
        await _seedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_synced) return;
            await SeedDefaultsAsync(ct).ConfigureAwait(false);
            _synced = true;
        }
        finally { _seedGate.Release(); }
    }

    /// <summary>Idempotently writes <see cref="DefaultApps"/> into the table.</summary>
    public async Task SeedDefaultsAsync(CancellationToken ct = default)
    {
        if (_table is null) return;
        foreach (var app in DefaultApps())
        {
            try { await _table.UpsertEntityAsync(FamilyAppEntity.FromApp(app), TableUpdateMode.Replace, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to seed family app {Title}.", app.Title); }
        }
        _logger.LogInformation("Seeded {Count} MapleKiosk family apps into Azure Storage.", DefaultApps().Count);
    }
}

/// <summary>Azure Table row for one family app (partition <c>app</c>, RowKey = zero-padded sort order).</summary>
internal sealed class FamilyAppEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "app";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public int SortOrder { get; set; }
    public string Number { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string IndustryKey { get; set; } = "";
    public string PageSlug { get; set; } = "";
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; } = true;
    // Money stored as double in Table Storage; <= 0 means "Custom" (no fixed price).
    public double SaasMonthlyUsd { get; set; }
    public double OnPremiseUsd { get; set; }

    public FamilyApp ToApp() => new()
    {
        SortOrder = SortOrder,
        Number = Number,
        Icon = Icon,
        Title = Title,
        Description = Description,
        IndustryKey = IndustryKey,
        PageSlug = string.IsNullOrEmpty(PageSlug) ? null : PageSlug,
        IsFeatured = IsFeatured,
        SaasMonthlyUsd = SaasMonthlyUsd > 0 ? (decimal)SaasMonthlyUsd : null,
        OnPremiseUsd = OnPremiseUsd > 0 ? (decimal)OnPremiseUsd : null,
    };

    public static FamilyAppEntity FromApp(FamilyApp a) => new()
    {
        PartitionKey = "app",
        RowKey = a.SortOrder.ToString("D3"),
        SortOrder = a.SortOrder,
        Number = a.Number,
        Icon = a.Icon,
        Title = a.Title,
        Description = a.Description,
        IndustryKey = a.IndustryKey,
        PageSlug = a.PageSlug ?? "",
        IsFeatured = a.IsFeatured,
        IsActive = true,
        SaasMonthlyUsd = a.SaasMonthlyUsd.HasValue ? (double)a.SaasMonthlyUsd.Value : 0,
        OnPremiseUsd = a.OnPremiseUsd.HasValue ? (double)a.OnPremiseUsd.Value : 0,
    };
}
