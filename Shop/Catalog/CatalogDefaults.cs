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
        new() { Key = "gift",  Name = "Printed gift cards", PickOne = true,  Sort = 5,
                Names = new() { ["fr"] = "Cartes-cadeaux imprimées", ["vi"] = "Thẻ quà tặng in sẵn", ["ru"] = "Печатные подарочные карты" } },
    };

    private static AppProduct Item(string category, string key, string name, decimal price, string interval, int sort,
        bool recommended = false, string? description = null, string? details = null) => new()
    {
        Sku = $"{category}-{key}", Category = category, Name = name, Price = price, BillingInterval = interval,
        Sort = sort, Recommended = recommended, Description = description, Details = details, Active = true
    };

    // Printed gift cards: the SKU's suffix is the number of cards (gift-3000 → 3,000), which /gift-cards
    // reads for the price per card. From 3,000 cards a barcode & QR scanner is included. Worded in every
    // language here, so the shop and /gift-cards read right before anyone translates them at /shop/admin.
    private static AppProduct Cards(int count, decimal price, int designs, int sort, bool recommended = false)
    {
        CatalogText Text(string culture, string name, string desc, string oneDesign, string manyDesigns, string numbered, string scanner, string shipping)
        {
            var n = count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo(culture switch
            {
                "fr" => "fr-CA", "vi" => "vi-VN", "ru" => "ru-RU", _ => "en-CA",
            }));
            return new CatalogText
            {
                Name = string.Format(name, n),
                Description = string.Format(desc, n),
                Details = string.Join('\n', new[]
                {
                    designs == 1 ? oneDesign : string.Format(manyDesigns, designs),
                    numbered,
                    count >= 3000 ? scanner : null,
                    shipping,
                }.Where(l => l is not null)),
            };
        }

        var en = Text("en", "{0} gift cards", "{0} printed plastic gift cards with your design, each with its own barcode and QR code.",
            "1 card design", "Up to {0} card designs", "Every card numbered with a barcode and QR code, ready to sell at your MapleKiosk counter",
            "Barcode & QR code scanner included", "Shipping included");
        var item = Item("gift", count.ToString(System.Globalization.CultureInfo.InvariantCulture), en.Name!, price,
            BillingIntervals.OneTime, sort, recommended, en.Description, en.Details);
        item.Texts = new()
        {
            ["fr"] = Text("fr", "{0} cartes-cadeaux", "{0} cartes-cadeaux en plastique imprimées à votre image, chacune avec son code-barres et son code QR.",
                "1 design de carte", "Jusqu’à {0} designs de carte", "Chaque carte numérotée, avec code-barres et code QR, prête à vendre à votre comptoir MapleKiosk",
                "Lecteur de codes-barres et QR inclus", "Livraison incluse"),
            ["vi"] = Text("vi", "{0} thẻ quà tặng", "{0} thẻ quà tặng nhựa in theo thiết kế của bạn, mỗi thẻ có mã vạch và mã QR riêng.",
                "1 mẫu thiết kế", "Tối đa {0} mẫu thiết kế", "Mỗi thẻ có số riêng, mã vạch và mã QR, bán ngay tại quầy MapleKiosk",
                "Tặng máy quét mã vạch & QR", "Đã bao gồm phí vận chuyển"),
            ["ru"] = Text("ru", "{0} подарочных карт", "{0} пластиковых подарочных карт с вашим дизайном, у каждой свой штрихкод и QR-код.",
                "1 дизайн карты", "До {0} дизайнов карт", "Каждая карта с номером, штрихкодом и QR-кодом — готова к продаже на кассе MapleKiosk",
                "Сканер штрихкодов и QR-кодов в подарок", "Доставка включена"),
        };
        foreach (var t in item.Texts.Values) t.Source = CatalogText.SourceOf(item);
        return item;
    }

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
        Item("mkt", "vip", "Enterprise VIP", 749m, BillingIntervals.Monthly, 3, description: "Lead your area and become the name people know."),

        Cards(1000, 1250m, designs: 1, sort: 1),
        Cards(2000, 2500m, designs: 1, sort: 2),
        Cards(3000, 3000m, designs: 1, sort: 3, recommended: true),
        Cards(4000, 3950m, designs: 2, sort: 4),
    };
}
