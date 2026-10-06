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

    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Details { get; set; }
    public double Price { get; set; }

    /// <summary>Rows written before the catalog had one currency; read as <see cref="Price"/> when it's unset.</summary>
    public double PriceUsd { get; set; }
    public double PriceVnd { get; set; }
    public string? ImageUrl { get; set; }
    public bool Active { get; set; } = true;

    public string BillingInterval { get; set; } = BillingIntervals.OneTime;
    public int TrialDays { get; set; }
    public int Sort { get; set; }
    public bool Recommended { get; set; }
    public string FeaturesJson { get; set; } = "[]";

    public static CatalogProductEntity FromProduct(AppProduct p) => new()
    {
        PartitionKey = Partition,
        RowKey = p.Sku,
        Category = p.Category,
        Name = p.Name,
        Description = p.Description,
        Details = p.Details,
        Price = (double)p.Price,
        PriceVnd = (double)p.PriceVnd,
        ImageUrl = p.ImageUrl,
        Active = p.Active,
        BillingInterval = p.BillingInterval,
        TrialDays = p.TrialDays,
        Sort = p.Sort,
        Recommended = p.Recommended,
        FeaturesJson = JsonSerializer.Serialize(p.Features)
    };

    public AppProduct ToProduct() => new()
    {
        Sku = RowKey,
        Category = Category ?? "",
        Name = Name,
        Description = Description,
        Details = Details,
        Price = (decimal)(Price > 0 ? Price : PriceUsd),
        PriceVnd = (decimal)PriceVnd,
        ImageUrl = ImageUrl,
        Active = Active,
        BillingInterval = string.IsNullOrWhiteSpace(BillingInterval) ? BillingIntervals.OneTime : BillingInterval,
        TrialDays = TrialDays,
        Sort = Sort,
        Recommended = Recommended,
        Features = string.IsNullOrWhiteSpace(FeaturesJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(FeaturesJson) ?? new List<string>())
    };
}

/// <summary>Azure Table row for a <see cref="CatalogCategory"/>, in the same table; RowKey is the key.</summary>
public sealed class CatalogCategoryEntity : ITableEntity
{
    public const string Partition = "Category";

    public string PartitionKey { get; set; } = Partition;
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Name { get; set; } = "";
    public bool PickOne { get; set; }
    public int Sort { get; set; }

    public static CatalogCategoryEntity From(CatalogCategory c) => new()
    {
        RowKey = c.Key, Name = c.Name, PickOne = c.PickOne, Sort = c.Sort
    };

    public CatalogCategory ToCategory() => new()
    {
        Key = RowKey, Name = Name, PickOne = PickOne, Sort = Sort
    };
}
