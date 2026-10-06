namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>
/// The catalog a fresh table starts with (CatalogStore seeds it once), and what the site sells when
/// storage isn't configured (local runs). From here on the catalog is managed at /shop/admin.
/// The categories' keys (setup, soft, sms, mkt) and the SKUs' suffixes match the pricing
/// pages' translated copy (pr.{category}.{key}.*), so the cards keep their wording in every language.
/// </summary>
public static class CatalogDefaults
{
    public static readonly CatalogCategory[] Categories =
    {
        new() { Key = "setup", Name = "One-time setup",   PickOne = false, Sort = 1 },
        new() { Key = "soft",  Name = "Software",         PickOne = true,  Sort = 2 },
        new() { Key = "sms",   Name = "Text messages",    PickOne = true,  Sort = 3 },
        new() { Key = "mkt",   Name = "Online marketing", PickOne = true,  Sort = 4 },
    };

    private static AppProduct Item(string category, string key, string name, decimal price, string interval, int sort,
        bool recommended = false, string? description = null) => new()
    {
        Sku = $"{category}-{key}", Category = category, Name = name, Price = price, BillingInterval = interval,
        Sort = sort, Recommended = recommended, Description = description, Active = true
    };

    public static readonly AppProduct[] Products =
    {
        Item("setup", "server", "On-site server", 500m, BillingIntervals.OneTime, 1, description: "A dedicated server installed at your business, with its own database."),
        Item("setup", "web", "Website", 300m, BillingIntervals.OneTime, 2, description: "A booking or ordering website under your own name."),
        Item("setup", "train", "Installation, training and local support", 350m, BillingIntervals.OneTime, 3),

        Item("soft", "booking", "AI Booking System", 49.99m, BillingIntervals.Monthly, 1, description: "Answers the phone and takes bookings for you."),
        Item("soft", "salon", "MapleKiosk full system", 120m, BillingIntervals.Monthly, 2, recommended: true, description: "Runs the whole business: checkout, staff, payroll and reports."),

        Item("sms", "1", "SMS 1", 39.99m, BillingIntervals.Monthly, 1, description: "1,000 appointments a month (about 2,000 texts)"),
        Item("sms", "2", "SMS 2", 89.99m, BillingIntervals.Monthly, 2, description: "3,000 appointments a month (about 6,000 texts)"),
        Item("sms", "3", "SMS 3 (VIP)", 199.99m, BillingIntervals.Monthly, 3, description: "Unlimited appointments and texts"),

        Item("mkt", "basic", "Basic Presence", 249m, BillingIntervals.Monthly, 1, description: "Keep your profile in shape and your regulars coming back."),
        Item("mkt", "pro", "Pro Growth", 549m, BillingIntervals.Monthly, 2, recommended: true, description: "Reach more people and bring in new customers."),
        Item("mkt", "vip", "Enterprise VIP", 749m, BillingIntervals.Monthly, 3, description: "Lead your area and become the name people know.")
    };
}
