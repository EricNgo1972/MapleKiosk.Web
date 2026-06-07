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

/// <summary>
/// A purchasable SaaS plan. Supplied from the Azure-Table catalog and managed in
/// the admin UI. Prices are per settlement currency (USD via Stripe, VND via
/// VietQR) and per billing interval. Checkout always re-resolves price from here.
/// </summary>
public sealed class AppProduct
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal PriceUsd { get; set; }
    public decimal PriceVnd { get; set; }
    public string? ImageUrl { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>OneTime / Monthly / Yearly — see <see cref="BillingIntervals"/>.</summary>
    public string BillingInterval { get; set; } = BillingIntervals.OneTime;

    /// <summary>Free-trial length in days (subscriptions only). 0 = no trial.</summary>
    public int TrialDays { get; set; }

    /// <summary>Selling points shown on the plan card.</summary>
    public List<string> Features { get; set; } = new();
}
