using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Services;

/// <summary>Demo app for one business type of the "Book a demo" form: the product name and its
/// demo URL, linked from the welcome email. Url "" = no demo yet (the email leaves the link out).</summary>
public sealed record DemoLink(string BusinessType, string Product, string Url);

/// <summary>
/// Per-product demo links, edited by staff on /onboarding/admin. Azure Table <c>demolinks</c>
/// (partition "demo", RowKey = the form's business-type value).
/// </summary>
public sealed class DemoLinkStore
{
    private const string TableName = "demolinks";
    private const string Partition = "demo";

    /// <summary>The form's business-type values (TrialForm option values) with the product each one gets by default.</summary>
    public static readonly (string Type, string Label, string Product)[] BusinessTypes =
    [
        ("Nail Salon", "Nail & beauty salon", "MapleSpa"),
        ("Restaurant", "Restaurant / fast food", "MaplePOS"),
        ("Garage", "Garage / auto service", "MapleGarage"),
        ("Retail", "Retail / shop", "MapleKiosk"),
        ("Startup", "Startup / office", "MapleKiosk"),
        ("Other", "Other", "MapleKiosk"),
    ];

    private readonly TableClient? _table;
    private readonly ILogger<DemoLinkStore> _logger;

    public DemoLinkStore(ILogger<DemoLinkStore> logger)
    {
        _logger = logger;
        var connection = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection)) return;
        try
        {
            _table = new TableClient(connection, TableName);
            _table.CreateIfNotExists();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Azure Table '{Table}'.", TableName);
            _table = null;
        }
    }

    public bool IsConfigured => _table is not null;

    /// <summary>One link per business type, in form order; types never saved get their default product and no URL.</summary>
    public async Task<IReadOnlyList<DemoLink>> GetAllAsync()
    {
        var saved = new Dictionary<string, TableEntity>(StringComparer.OrdinalIgnoreCase);
        if (_table is not null)
        {
            try
            {
                await foreach (var e in _table.QueryAsync<TableEntity>(x => x.PartitionKey == Partition))
                    saved[e.RowKey] = e;
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to read demo links."); }
        }

        return BusinessTypes.Select(t => saved.TryGetValue(t.Type, out var e)
                ? new DemoLink(t.Type, e.GetString("Product") is { Length: > 0 } p ? p : t.Product, e.GetString("Url") ?? "")
                : new DemoLink(t.Type, t.Product, ""))
            .ToList();
    }

    public async Task<DemoLink> GetAsync(string businessType)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(l => string.Equals(l.BusinessType, businessType, StringComparison.OrdinalIgnoreCase))
               ?? new DemoLink(businessType, "MapleKiosk", "");
    }

    public async Task SaveAsync(IEnumerable<DemoLink> links)
    {
        if (_table is null) throw new InvalidOperationException("Storage is not configured (STORAGE_CONNECTION_STRING).");
        foreach (var l in links)
        {
            await _table.UpsertEntityAsync(new TableEntity(Partition, l.BusinessType)
            {
                ["Product"] = l.Product.Trim(),
                ["Url"] = l.Url.Trim(),
            }, TableUpdateMode.Replace);
        }
    }

    /// <summary>"" for an empty value; otherwise the URL if it's an absolute http(s) link, else null (invalid).</summary>
    public static string? NormalizeUrl(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0) return "";
        if (!v.Contains("://")) v = "https://" + v;
        return Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.ToString().TrimEnd('/')
            : null;
    }
}
