namespace MapleKiosk.Web.Services;

/// <summary>One product with its own marketing page. The nav's Products menu, the footer and the
/// language switch all read this list, so a new product is one line here plus its page and copy.</summary>
/// <param name="Slug">Route under the culture prefix: "coffee" → /coffee, /fr/coffee, ...</param>
/// <param name="Name">Product name, never translated (MapleCoffee, ...).</param>
/// <param name="Key">Translation key stem: prod.{Key}.for is the "who it's for" line.</param>
/// <param name="Group">Trade = a system for one industry; AddOn = sold on its own or added to any of them.</param>
public record SiteProduct(string Slug, string Name, string Key, ProductGroup Group);

public enum ProductGroup { Trade, AddOn }

public static class SiteProducts
{
    public static readonly SiteProduct[] All =
    {
        new("coffee",      "MapleCoffee", "coffee", ProductGroup.Trade),
        new("nails",       "MapleSPA",    "nails",  ProductGroup.Trade),
        new("restaurants", "MaplePOS",    "resto",  ProductGroup.Trade),
        new("garage",      "MapleGarage", "garage", ProductGroup.Trade),
        new("ai-agent",    "AI Voice Agent", "agent", ProductGroup.AddOn),
    };

    public static IEnumerable<SiteProduct> In(ProductGroup g) => All.Where(p => p.Group == g);
}
