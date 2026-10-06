using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Config;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments;

namespace MapleKiosk.Web.Shop.Checkout;

/// <summary>
/// Turns a cart (SKUs + chosen method) into a persisted Pending order plus a way
/// to pay: a Stripe hosted-checkout URL, or a VietQR image. Prices are
/// re-resolved from the catalog here — client prices are never trusted.
/// </summary>
public sealed class CheckoutService
{
    public const int MaxQuantity = 99;

    private readonly IAppCatalog _catalog;
    private readonly AppOrderService _orders;
    private readonly StripeCheckoutCreator _stripe;
    private readonly VietQrCreator _vietQr;
    private readonly AppStoreConfig _config;

    public CheckoutService(IAppCatalog catalog, AppOrderService orders, StripeCheckoutCreator stripe, VietQrCreator vietQr,
        AppStoreConfig config)
    {
        _config = config;
        _catalog = catalog;
        _orders = orders;
        _stripe = stripe;
        _vietQr = vietQr;
    }

    public async Task<CheckoutResult> CreateAsync(CreateCheckoutRequest request, CancellationToken ct = default)
    {
        var items = request.Items?.Where(i => !string.IsNullOrWhiteSpace(i.Sku)).ToList() ?? new List<CheckoutItem>();
        if (items.Count == 0) return Fail(request.Method, "Cart is empty.");

        var isVietQr = string.Equals(request.Method, AppPaymentMethods.VietQr, StringComparison.OrdinalIgnoreCase);
        var method = isVietQr ? AppPaymentMethods.VietQr : AppPaymentMethods.Stripe;
        var currency = isVietQr ? "VND" : CatalogStore.Currency;

        // Resolve every product (price + billing authority); a SKU listed twice is one line.
        var picks = new List<(Catalog.AppProduct Product, int Quantity)>();
        foreach (var group in items.GroupBy(i => i.Sku.Trim()))
        {
            var product = await _catalog.FindAsync(group.Key, ct).ConfigureAwait(false);
            if (product is null) return Fail(method, $"Unknown or inactive product: {group.Key}");
            var quantity = group.Sum(i => Math.Max(1, i.Quantity));
            if (quantity > MaxQuantity) return Fail(method, $"At most {MaxQuantity} of {product.Name}.");
            picks.Add((product, quantity));
        }

        // A cart can mix one-time items with monthly (or yearly) plans: Stripe Checkout makes one
        // subscription for the plans and charges the one-time items once, on its first invoice.
        // One subscription renews on one cycle, and VietQR can't renew at all.
        var recurring = picks.Where(p => Catalog.BillingIntervals.IsRecurring(p.Product.BillingInterval)).ToList();
        var cycles = recurring.Select(p => p.Product.BillingInterval).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cycles.Count > 1)
            return Fail(method, "Monthly and yearly plans can't be bought together — please check them out separately.");
        if (recurring.Count > 0 && isVietQr)
            return Fail(method, "VietQR isn't available for subscriptions — please pay by card.");

        var lines = new List<AppOrderLine>();
        foreach (var (product, quantity) in picks)
        {
            var unit = isVietQr ? product.PriceVnd : product.Price;
            if (unit <= 0) return Fail(method, $"Product {product.Sku} is not sold via {method}.");
            lines.Add(new AppOrderLine
            {
                Sku = product.Sku, Name = product.Name, UnitPrice = unit, Quantity = quantity,
                Interval = product.BillingInterval
            });
        }

        // A subscription has one trial, so only when every plan in the cart offers one (the shortest).
        // During the trial only the one-time items are charged.
        var trialDays = recurring.Count > 0 && recurring.All(p => p.Product.TrialDays > 0)
            ? recurring.Min(p => p.Product.TrialDays) : 0;
        var dueToday = lines.Where(l => trialDays == 0 || !Catalog.BillingIntervals.IsRecurring(l.Interval!)).Sum(l => l.LineTotal);

        var order = new AppOrder
        {
            Lines = lines,
            Method = method,
            Currency = currency,
            Total = dueToday,
            Interval = cycles.FirstOrDefault() ?? Catalog.BillingIntervals.OneTime,
            TrialDays = trialDays,
            CustomerEmail = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim()
        };

        if (isVietQr)
        {
            var qr = await _vietQr.CreateAsync(order, ct).ConfigureAwait(false);
            if (!qr.Success) return Fail(method, qr.Error ?? "Could not build VietQR.");

            await _orders.CreateAsync(order, ct).ConfigureAwait(false);
            return new CheckoutResult
            {
                Success = true, OrderRef = order.OrderRef, Method = method,
                Total = order.Total, Currency = order.Currency,
                QrPayload = qr.Payload, QrImageDataUri = qr.QrImageDataUri
            };
        }

        if (string.IsNullOrWhiteSpace(request.SuccessUrl) || string.IsNullOrWhiteSpace(request.CancelUrl))
            return Fail(method, "SuccessUrl and CancelUrl are required for Stripe checkout.");

        var session = await _stripe.CreateAsync(order, request.SuccessUrl!, request.CancelUrl!, ct, request.Culture).ConfigureAwait(false);
        if (!session.Success) return Fail(method, session.Error ?? "Could not start Stripe checkout.");

        order.ProviderRef = session.SessionId;
        await _orders.CreateAsync(order, ct).ConfigureAwait(false);

        return new CheckoutResult
        {
            Success = true, OrderRef = order.OrderRef, Method = method,
            Total = order.Total, Currency = order.Currency, StripeUrl = session.Url
        };
    }

    public async Task<OrderStatusResult?> GetStatusAsync(string orderRef, CancellationToken ct = default)
    {
        var order = await _orders.GetAsync(orderRef, ct).ConfigureAwait(false);
        if (order is null) return null;
        return new OrderStatusResult
        {
            OrderRef = order.OrderRef, Status = order.Status.ToString(),
            Total = order.Total, Currency = order.Currency,
            Interval = order.Interval, TrialDays = order.TrialDays,
            ManageUrl = BillingIntervals.IsRecurring(order.Interval) ? NullIfEmpty(await _config.GetStripePortalUrlAsync().ConfigureAwait(false)) : null
        };
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static CheckoutResult Fail(string method, string error)
        => new() { Success = false, Method = method, Error = error };
}
