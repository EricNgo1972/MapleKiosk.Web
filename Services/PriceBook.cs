namespace MapleKiosk.Web.Services;

/// <summary>
/// What every trade's quote is made of, in US dollars, from the salon quote: the one-time setup, the
/// monthly software plans, the SMS packages and the marketing plans. The pricing pages (PricingBuilder)
/// and the website assistant (SiteKnowledge) both read it, so a price changes in one place.
/// Names and descriptions are translation keys: pr.{group}.{key}.t, overridable per product as
/// pr.{product}.{group}.{key}.t.
/// </summary>
public static class PriceBook
{
    public record Setup(string Key, decimal Price, int Points);
    public record Plan(string Key, decimal Price, int Features = 0, bool Recommended = false);

    public static readonly Setup[] SetupItems =
    {
        new("server", 500m, 3),
        new("web",    300m, 3),
        new("train",  350m, 4),
    };

    // The software plans share one feature list (pr.soft.f1..f8): the AI plan has the first three,
    // the full system all of them.
    public const int SoftwareFeatures = 8;

    public static readonly Plan[] Software =
    {
        new("booking", 49.99m,  Features: 3),
        new("salon",   120.00m, Features: SoftwareFeatures, Recommended: true),
    };

    public static readonly Plan[] Sms =
    {
        new("1", 39.99m),
        new("2", 89.99m),
        new("3", 199.99m),
    };

    // Appointments (or orders) outside an SMS plan, two texts each.
    public const decimal SmsOverage = 0.05m;

    public static readonly Plan[] Marketing =
    {
        new("basic", 249m),
        new("pro",   549m, Recommended: true),
        new("vip",   749m),
    };

    public static readonly string[] MarketingFacts = { "freq", "format", "maps", "channels", "print", "report" };
}
