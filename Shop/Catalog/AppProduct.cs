namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>Billing cadence of a plan. One-time = single charge (Stripe payment +
/// VietQR); Monthly/Yearly = recurring Stripe subscription.</summary>
public static class BillingIntervals
{
    public const string OneTime = "OneTime";
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";

    public static bool IsRecurring(string interval)
        => string.Equals(interval, Monthly, StringComparison.OrdinalIgnoreCase)
        || string.Equals(interval, Yearly, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stripe recurring interval token ("month"/"year"), or null for one-time.</summary>
    public static string? StripeInterval(string interval) => interval switch
    {
        Monthly => "month",
        Yearly => "year",
        _ => null
    };

    /// <summary>Short suffix for price display.</summary>
    public static string Suffix(string interval) => interval switch
    {
        Monthly => "/mo",
        Yearly => "/yr",
        _ => ""
    };
}

/// <summary>A group of catalog items, e.g. "Setup" or "Software". Every category is sold both on the trades'
/// pricing pages (the quote builder) and in the shop (/shop).</summary>
public sealed class CatalogCategory
{

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Buyers pick one item (a plan) rather than any number (add-ons).</summary>
    public bool PickOne { get; set; }
    public int Sort { get; set; }
}

/// <summary>
/// Something we sell: a product or a service, at one price (in <see cref="CatalogStore.Currency"/>),
/// charged once or every month/year. Managed at /shop/admin, synced to Stripe as a Product + Price,
/// and sold from the pricing pages or the shop. Checkout always re-resolves the price from here.
/// </summary>
public sealed class AppProduct
{
    public string Sku { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }

    /// <summary>VietQR price (shop only); 0 = not sold over VietQR.</summary>
    public decimal PriceVnd { get; set; }
    public string? ImageUrl { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>How often it's charged: OneTime / Monthly / Yearly — see <see cref="BillingIntervals"/>.</summary>
    public string BillingInterval { get; set; } = BillingIntervals.OneTime;

    /// <summary>Free-trial length in days (shop subscriptions only). 0 = no trial.</summary>
    public int TrialDays { get; set; }
    public int Sort { get; set; }
    public bool Recommended { get; set; }

    /// <summary>What's included, one point per line: the shop's product page (/shop/{sku}) only.</summary>
    public string? Details { get; set; }

    /// <summary>Selling points shown on the card when the site has no translated copy for it.</summary>
    public List<string> Features { get; set; } = new();

    /// <summary>The key of the pricing pages' translated copy: the SKU without its category prefix
    /// ("setup-server" → "server", read as pr.setup.server.*).</summary>
    public string CopyKey => Sku.StartsWith(Category + "-", StringComparison.Ordinal) ? Sku[(Category.Length + 1)..] : Sku;
}
