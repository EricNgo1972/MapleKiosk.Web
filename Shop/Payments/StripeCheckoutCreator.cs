using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Config;
using MapleKiosk.Web.Shop.Orders;
using Stripe;
using Stripe.Checkout;

namespace MapleKiosk.Web.Shop.Payments;

/// <summary>Opens Stripe Checkout for an order (<see cref="StripeCheckoutCreator"/>); a seam for the phone-line tests.</summary>
public interface IStripeCheckout
{
    Task<StripeCheckoutCreator.Result> CreateAsync(AppOrder order, string successUrl, string cancelUrl, CancellationToken ct = default,
        string? locale = null);
}

/// <summary>
/// Creates a hosted Stripe Checkout Session for an app-store order. The OrderRef
/// is carried as ClientReferenceId + metadata so the webhook converges on the
/// order, and success/cancel redirect back to the website.
/// </summary>
public sealed class StripeCheckoutCreator : IStripeCheckout
{
    private readonly AppStoreConfig _config;
    private readonly StripeProductSync _products;
    private readonly ILogger<StripeCheckoutCreator> _logger;

    public StripeCheckoutCreator(AppStoreConfig config, StripeProductSync products, ILogger<StripeCheckoutCreator> logger)
    {
        _config = config;
        _products = products;
        _logger = logger;
    }

    public sealed record Result(bool Success, string? Url, string? SessionId, string? Error);

    public async Task<Result> CreateAsync(AppOrder order, string successUrl, string cancelUrl, CancellationToken ct = default, string? locale = null)
    {
        // A test order (StripeTestMode) checks out on the Stripe test account: same flow, nothing charged.
        var secretKey = await _config.GetStripeSecretKeyAsync(order.IsTest).ConfigureAwait(false);
        if (string.IsNullOrEmpty(secretKey))
            return new Result(false, null, null, order.IsTest ? "Stripe test mode is not configured." : "Stripe is not configured.");
        // Never charge a card for an order marked test: the test slot must hold a test key.
        if (order.IsTest && !secretKey.Contains("_test_", StringComparison.Ordinal))
        {
            _logger.LogError("Test order {OrderRef} refused: AppStore/StripeTestSecretKey is not a test key.", order.OrderRef);
            return new Result(false, null, null, "Stripe test mode is misconfigured.");
        }

        // Any recurring line → subscription mode (each recurring line gets a Recurring price;
        // one-time lines, like a quote's setup, are charged once on the first invoice).
        // All one-time → payment mode.
        string? LineInterval(AppOrderLine l) => BillingIntervals.StripeInterval(l.Interval ?? order.Interval); // "month"/"year"/null
        var mode = order.Lines.Any(l => LineInterval(l) is not null) ? "subscription" : "payment";

        // A line whose Product/Price was synced to Stripe (/shop/admin) and still matches bills that
        // Price, so Stripe reports it by product; anything else is priced inline as before.
        var priceIds = await _products.ResolvePriceIdsAsync(order.Lines.Select(l => new StripeItem(
            l.Sku, l.Name, null, l.UnitPrice, order.Currency, l.Interval ?? order.Interval, true)), ct, order.IsTest).ConfigureAwait(false);

        var lineItems = order.Lines.Select(l =>
        {
            if (priceIds.TryGetValue(l.Sku, out var priceId))
                return new SessionLineItemOptions { Quantity = l.Quantity, Price = priceId };

            var priceData = new SessionLineItemPriceDataOptions
            {
                Currency = order.Currency.ToLowerInvariant(),
                UnitAmount = StripeProductSync.Cents(l.UnitPrice),
                ProductData = new SessionLineItemPriceDataProductDataOptions { Name = l.Name }
            };
            if (LineInterval(l) is { } interval)
                priceData.Recurring = new SessionLineItemPriceDataRecurringOptions { Interval = interval };
            return new SessionLineItemOptions { Quantity = l.Quantity, PriceData = priceData };
        }).ToList();

        // Everything sales needs to recognise the order in the Stripe dashboard.
        var metadata = new Dictionary<string, string> { ["orderRef"] = order.OrderRef };
        void Meta(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) metadata[key] = value; }
        Meta("source", order.Source);
        Meta("company", order.Company);
        Meta("contact", order.CustomerName);
        Meta("phone", order.CustomerPhone);
        Meta("lang", order.Culture);

        var options = new SessionCreateOptions
        {
            Mode = mode,
            SuccessUrl = AppendRef(successUrl, order.OrderRef),
            CancelUrl = AppendRef(cancelUrl, order.OrderRef),
            PaymentMethodTypes = ["card"],
            ClientReferenceId = order.OrderRef,
            Metadata = metadata,
            LineItems = lineItems,
            Locale = locale is "fr" or "vi" or "ru" ? locale : "auto"
        };

        if (mode == "subscription")
        {
            // Carry the same metadata onto the subscription, and apply any free trial.
            options.SubscriptionData = new SessionSubscriptionDataOptions { Metadata = new(metadata) };
            if (order.TrialDays > 0)
                options.SubscriptionData.TrialPeriodDays = order.TrialDays;
        }
        else
        {
            // Subscriptions always make a Stripe customer; make one for one-time payments too.
            options.CustomerCreation = "always";
        }

        // Stripe Tax (GST/QST/HST...) once it's set up in the Stripe dashboard: AppStore/StripeAutomaticTax = true.
        if (string.Equals(await _config.GetStripeAutomaticTaxAsync(order.IsTest).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase))
        {
            options.AutomaticTax = new SessionAutomaticTaxOptions { Enabled = true };
            options.BillingAddressCollection = "required";
        }

        if (!string.IsNullOrWhiteSpace(order.CustomerEmail))
            options.CustomerEmail = order.CustomerEmail;

        try
        {
            var client = new StripeClient(secretKey);
            var service = new SessionService(client);
            var session = await service.CreateAsync(options, new RequestOptions { IdempotencyKey = order.OrderRef }, ct)
                .ConfigureAwait(false);

            _logger.LogInformation("App Store Stripe {Account} session created: {OrderRef} -> {SessionId}",
                order.IsTest ? "TEST" : "live", order.OrderRef, session.Id);
            return new Result(true, session.Url, session.Id, null);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "App Store Stripe session failed: {OrderRef}", order.OrderRef);
            return new Result(false, null, null, ex.StripeError?.Message ?? ex.Message);
        }
    }

    private static string AppendRef(string url, string orderRef)
    {
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}ref={Uri.EscapeDataString(orderRef)}";
    }
}
