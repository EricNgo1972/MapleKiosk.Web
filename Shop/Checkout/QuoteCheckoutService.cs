using MapleKiosk.Web.Services;
using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments;

namespace MapleKiosk.Web.Shop.Checkout;

/// <summary>What a guest picked on a /{product}/pricing quote, sent when they press "Buy online".
/// Only SKUs travel; prices come from the catalog.</summary>
public sealed class QuoteCheckoutRequest
{
    public string Product { get; set; } = "";
    public List<string> Items { get; set; } = new();
    public string? Company { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Culture { get; set; }
}

/// <summary>
/// Turns a pricing-page quote into a Pending order and a Stripe Checkout page. Each item is charged
/// as the catalog says: one-time items once, monthly (or yearly) ones as a subscription, so a quote
/// with any plan becomes one Stripe subscription whose first invoice is the quote's "first payment";
/// an all one-time quote is a plain payment. Payment confirmation is the shop's: the Stripe webhook →
/// <see cref="AppOrderService.MarkPaidAsync"/> → receipt emails.
/// </summary>
public sealed class QuoteCheckoutService
{
    private readonly CatalogStore _catalog;
    private readonly AppOrderService _orders;
    private readonly StripeCheckoutCreator _stripe;

    public QuoteCheckoutService(CatalogStore catalog, AppOrderService orders, StripeCheckoutCreator stripe)
    {
        _catalog = catalog;
        _orders = orders;
        _stripe = stripe;
    }

    public async Task<CheckoutResult> CreateAsync(QuoteCheckoutRequest req, string baseUri, CancellationToken ct = default)
    {
        var trade = SiteProducts.All.FirstOrDefault(p => p.Slug == req.Product && p.Group == ProductGroup.Trade);
        if (trade is null) return Fail("Unknown product.");

        var email = req.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 120 || !email.Contains('@') || email.Contains(' '))
            return Fail("A valid email is required.");

        var culture = req.Culture is "fr" or "vi" or "ru" ? req.Culture : "en";

        // Every SKU must be an active item of a quote category, and a pick-one category gives one item.
        var quote = await _catalog.GetGroupsAsync(ct).ConfigureAwait(false);
        var lines = new List<AppOrderLine>();
        var picked = new HashSet<string>(); // pick-one categories already used
        foreach (var sku in req.Items.Distinct())
        {
            var (category, item) = quote.SelectMany(g => g.Items.Select(i => (g.Category, Item: i))).FirstOrDefault(x => x.Item.Sku == sku);
            if (item is null) return Fail($"Unknown item: {sku}");
            if (category.PickOne && !picked.Add(category.Key)) return Fail($"Pick one item from {category.Name}.");

            lines.Add(new AppOrderLine
            {
                Sku = item.Sku,
                Name = $"{trade.Name} · {Text(culture, trade.Key, $"{item.Category}.{item.CopyKey}.t") ?? item.Name}",
                UnitPrice = item.Price,
                Quantity = 1,
                Interval = item.BillingInterval
            });
        }
        if (lines.Count == 0) return Fail("Nothing picked.");

        // One Stripe subscription bills on one cycle.
        var cycles = lines.Select(l => l.Interval!).Where(BillingIntervals.IsRecurring).Distinct().ToList();
        if (cycles.Count > 1) return Fail("Monthly and yearly items can't be bought together.");

        static string? Clip(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, max)];

        var order = new AppOrder
        {
            Lines = lines,
            Method = AppPaymentMethods.Stripe,
            Currency = CatalogStore.Currency,
            Total = lines.Sum(l => l.LineTotal), // the first payment
            Interval = cycles.FirstOrDefault() ?? BillingIntervals.OneTime,
            CustomerEmail = email,
            Company = Clip(req.Company, 80),
            CustomerName = Clip(req.Name, 80),
            CustomerPhone = Clip(req.Phone, 40),
            Source = $"quote:{trade.Slug}"
        };

        var prefix = culture == "en" ? "" : culture + "/";
        var root = baseUri.TrimEnd('/') + "/";
        var success = $"{root}{prefix}quote/paid";
        var cancel = $"{root}{prefix}{trade.Slug}/pricing?checkout=cancelled#quote";

        var session = await _stripe.CreateAsync(order, success, cancel, ct, culture).ConfigureAwait(false);
        if (!session.Success) return Fail(session.Error ?? "Could not start Stripe checkout.");

        order.ProviderRef = session.SessionId;
        await _orders.CreateAsync(order, ct).ConfigureAwait(false);

        return new CheckoutResult
        {
            Success = true, OrderRef = order.OrderRef, Method = order.Method,
            Total = order.Total, Currency = order.Currency, StripeUrl = session.Url
        };
    }

    /// <summary>The pricing pages' wording: pr.{trade}.{key} if the trade has its own, else pr.{key};
    /// English fallback; null when the site has no copy for it (then the catalog's own text is used).</summary>
    internal static string? Text(string culture, string tradeKey, string key)
    {
        foreach (var c in culture == "en" ? new[] { "en" } : new[] { culture, "en" })
        {
            if (!Translations.All.TryGetValue(c, out var d)) continue;
            if (d.TryGetValue($"pr.{tradeKey}.{key}", out var own)) return own;
            if (d.TryGetValue($"pr.{key}", out var v)) return v;
        }
        return null;
    }

    private static CheckoutResult Fail(string error)
        => new() { Success = false, Method = AppPaymentMethods.Stripe, Error = error };
}
