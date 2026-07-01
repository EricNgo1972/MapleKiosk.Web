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
        // SaaS = fixed monthly price (raised +50%); on-premise = quoted (OnPremiseUsd null ⇒ "Call").
        new FamilyApp { SortOrder = 1, Number = "01", Icon = "🛒", Title = "Point of Sale",            Description = "Fast, flexible checkout on any device — cash, card, tap or QR — with receipts and daily totals done for you.", IsFeatured = true, SaasMonthlyUsd = 44, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 2, Number = "02", Icon = "📅", Title = "Booking & Appointments",   Description = "Online scheduling, reminders and no-show protection that keep your calendar full.", SaasMonthlyUsd = 29, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 3, Number = "03", Icon = "📦", Title = "Inventory & Stock",         Description = "Track stock, suppliers and purchase orders in real time, with low-stock alerts before you run out.", SaasMonthlyUsd = 38, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 4, Number = "04", Icon = "🧾", Title = "Accounting & Invoicing",    Description = "Invoices, expenses and reports that keep the books tidy and make tax time painless.", SaasMonthlyUsd = 44, OnPremiseUsd = null },
        new FamilyApp { SortOrder = 5, Number = "05", Icon = "🔧", Title = "Asset Maintenance",         Description = "Schedule upkeep, log repairs and track equipment so nothing breaks down at the worst time.", SaasMonthlyUsd = 29, OnPremiseUsd = null },
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
        IsFeatured = a.IsFeatured,
        IsActive = true,
        SaasMonthlyUsd = a.SaasMonthlyUsd.HasValue ? (double)a.SaasMonthlyUsd.Value : 0,
        OnPremiseUsd = a.OnPremiseUsd.HasValue ? (double)a.OnPremiseUsd.Value : 0,
    };
}
