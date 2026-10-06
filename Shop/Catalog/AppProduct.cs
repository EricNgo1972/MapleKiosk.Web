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
/// An item's wording in one of the site's other languages (fr / vi / ru). English is the master: it's
/// what syncs to Stripe and what any empty field falls back to; translations are shown on the website
/// only (shop, cart, receipts). <see cref="Source"/> is the English they were made from, so the admin
/// can tell when the English has changed since.
/// </summary>
public sealed class CatalogText
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Details { get; set; }
    public string? Source { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(Details);

    /// <summary>The English an item's translation is checked against (name, description, details).</summary>
    public static string SourceOf(AppProduct p) => $"{p.Name}\n{p.Description}\n{p.Details}".Trim();
}

/// <summary>The site's languages besides English, which translations are kept for.</summary>
public static class CatalogLanguages
{
    public static readonly string[] Translated = { "fr", "vi", "ru" };

    public static string NameOf(string culture) => culture switch
    {
        "fr" => "Français", "vi" => "Tiếng Việt", "ru" => "Русский", _ => "English",
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

    /// <summary>The name in the site's other languages (fr / vi / ru); website only.</summary>
    public Dictionary<string, string> Names { get; set; } = new();

    public string NameIn(string culture)
        => Names.TryGetValue(culture, out var n) && !string.IsNullOrWhiteSpace(n) ? n : Name;
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

    /// <summary>The wording in the site's other languages (fr / vi / ru); website only, never Stripe.</summary>
    public Dictionary<string, CatalogText> Texts { get; set; } = new();

    private CatalogText? In(string culture) => Texts.TryGetValue(culture, out var t) ? t : null;
    private static string? Filled(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    // Each field falls back to English on its own, so a half-done translation still reads.
    public string NameIn(string culture) => Filled(In(culture)?.Name) ?? Name;
    public string? DescriptionIn(string culture) => Filled(In(culture)?.Description) ?? Description;
    public string? DetailsIn(string culture) => Filled(In(culture)?.Details) ?? Details;

    /// <summary>Selling points shown on the card when the site has no translated copy for it.</summary>
    public List<string> Features { get; set; } = new();

    /// <summary>The key of the pricing pages' translated copy: the SKU without its category prefix
    /// ("setup-server" → "server", read as pr.setup.server.*).</summary>
    public string CopyKey => Sku.StartsWith(Category + "-", StringComparison.Ordinal) ? Sku[(Category.Length + 1)..] : Sku;
}
