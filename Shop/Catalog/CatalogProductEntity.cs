using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>
/// Azure Table row for a catalog product. One entity per product so the catalog
/// can be maintained record-by-record from the admin UI. PartitionKey is a fixed
/// bucket; RowKey is the SKU.
/// </summary>
public sealed class CatalogProductEntity : ITableEntity
{
    public const string Partition = "Product";

    public string PartitionKey { get; set; } = Partition;
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public double PriceUsd { get; set; }
    public double PriceVnd { get; set; }
    public string? ImageUrl { get; set; }
    public bool Active { get; set; } = true;

    public string BillingInterval { get; set; } = BillingIntervals.OneTime;
    public int TrialDays { get; set; }
    public string FeaturesJson { get; set; } = "[]";

    public static CatalogProductEntity FromProduct(AppProduct p) => new()
    {
        PartitionKey = Partition,
        RowKey = p.Sku,
        Name = p.Name,
        Description = p.Description,
        PriceUsd = (double)p.PriceUsd,
        PriceVnd = (double)p.PriceVnd,
        ImageUrl = p.ImageUrl,
        Active = p.Active,
        BillingInterval = p.BillingInterval,
        TrialDays = p.TrialDays,
        FeaturesJson = JsonSerializer.Serialize(p.Features)
    };

    public AppProduct ToProduct() => new()
    {
        Sku = RowKey,
        Name = Name,
        Description = Description,
        PriceUsd = (decimal)PriceUsd,
        PriceVnd = (decimal)PriceVnd,
        ImageUrl = ImageUrl,
        Active = Active,
        BillingInterval = string.IsNullOrWhiteSpace(BillingInterval) ? BillingIntervals.OneTime : BillingInterval,
        TrialDays = TrialDays,
        Features = string.IsNullOrWhiteSpace(FeaturesJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(FeaturesJson) ?? new List<string>())
    };
}
