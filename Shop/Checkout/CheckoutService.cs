using MapleKiosk.Web.Shop.Catalog;
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
    private static readonly HashSet<string> StripeCurrencies = new(StringComparer.OrdinalIgnoreCase) { "USD", "CAD" };

    private readonly IAppCatalog _catalog;
    private readonly AppOrderService _orders;
    private readonly StripeCheckoutCreator _stripe;
    private readonly VietQrCreator _vietQr;

    public CheckoutService(IAppCatalog catalog, AppOrderService orders, StripeCheckoutCreator stripe, VietQrCreator vietQr)
    {
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
        var currency = isVietQr
            ? "VND"
            : (request.Currency is not null && StripeCurrencies.Contains(request.Currency)
                ? request.Currency.ToUpperInvariant() : "USD");

        // Resolve every product (price + billing authority).
        var products = new List<Catalog.AppProduct>();
        foreach (var item in items)
        {
            var product = await _catalog.FindAsync(item.Sku, ct).ConfigureAwait(false);
            if (product is null) return Fail(method, $"Unknown or inactive product: {item.Sku}");
            products.Add(product);
        }

        // Subscriptions are Stripe-only and one-plan-at-a-time (a Checkout Session
        // can't mix a recurring plan with other items or run over VietQR).
        var recurring = products.Where(p => Catalog.BillingIntervals.IsRecurring(p.BillingInterval)).ToList();
        if (recurring.Count > 0)
        {
            if (products.Count > 1)
                return Fail(method, "Subscriptions must be purchased one plan at a time.");
            if (isVietQr)
                return Fail(method, "VietQR isn't available for subscriptions — please pay by card.");
        }

        var lines = new List<AppOrderLine>();
        foreach (var product in products)
        {
            var unit = isVietQr ? product.PriceVnd : product.PriceUsd;
            if (unit <= 0) return Fail(method, $"Product {product.Sku} is not sold via {method}.");
            // SaaS catalog: one licence per plan — quantity is always 1.
            lines.Add(new AppOrderLine { Sku = product.Sku, Name = product.Name, UnitPrice = unit, Quantity = 1 });
        }

        var order = new AppOrder
        {
            Lines = lines,
            Method = method,
            Currency = currency,
            Total = lines.Sum(l => l.LineTotal),
            Interval = recurring.Count > 0 ? recurring[0].BillingInterval : Catalog.BillingIntervals.OneTime,
            TrialDays = recurring.Count > 0 ? recurring[0].TrialDays : 0,
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

        var session = await _stripe.CreateAsync(order, request.SuccessUrl!, request.CancelUrl!, ct).ConfigureAwait(false);
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
            Total = order.Total, Currency = order.Currency
        };
    }

    private static CheckoutResult Fail(string method, string error)
        => new() { Success = false, Method = method, Error = error };
}
