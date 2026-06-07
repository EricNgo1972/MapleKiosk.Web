using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Config;
using MapleKiosk.Web.Shop.Orders;
using Stripe;
using Stripe.Checkout;

namespace MapleKiosk.Web.Shop.Payments;

/// <summary>
/// Creates a hosted Stripe Checkout Session for an app-store order. The OrderRef
/// is carried as ClientReferenceId + metadata so the webhook converges on the
/// order, and success/cancel redirect back to the website.
/// </summary>
public sealed class StripeCheckoutCreator
{
    private readonly AppStoreConfig _config;
    private readonly ILogger<StripeCheckoutCreator> _logger;

    public StripeCheckoutCreator(AppStoreConfig config, ILogger<StripeCheckoutCreator> logger)
    {
        _config = config;
        _logger = logger;
    }

    public sealed record Result(bool Success, string? Url, string? SessionId, string? Error);

    public async Task<Result> CreateAsync(AppOrder order, string successUrl, string cancelUrl, CancellationToken ct = default)
    {
        var secretKey = await _config.GetStripeSecretKeyAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(secretKey))
            return new Result(false, null, null, "Stripe is not configured.");

        // Recurring plans → subscription mode (each line needs a Recurring price);
        // one-time → payment mode.
        var stripeInterval = BillingIntervals.StripeInterval(order.Interval); // "month"/"year"/null
        var mode = stripeInterval is null ? "payment" : "subscription";

        var lineItems = order.Lines.Select(l =>
        {
            var priceData = new SessionLineItemPriceDataOptions
            {
                Currency = order.Currency.ToLowerInvariant(),
                UnitAmount = (long)(l.UnitPrice * 100),
                ProductData = new SessionLineItemPriceDataProductDataOptions { Name = l.Name }
            };
            if (stripeInterval is not null)
                priceData.Recurring = new SessionLineItemPriceDataRecurringOptions { Interval = stripeInterval };
            return new SessionLineItemOptions { Quantity = l.Quantity, PriceData = priceData };
        }).ToList();

        var options = new SessionCreateOptions
        {
            Mode = mode,
            SuccessUrl = AppendRef(successUrl, order.OrderRef),
            CancelUrl = AppendRef(cancelUrl, order.OrderRef),
            PaymentMethodTypes = ["card"],
            ClientReferenceId = order.OrderRef,
            Metadata = new Dictionary<string, string> { ["orderRef"] = order.OrderRef },
            LineItems = lineItems
        };

        if (mode == "subscription")
        {
            // Carry orderRef onto the subscription too, and apply any free trial.
            options.SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string> { ["orderRef"] = order.OrderRef }
            };
            if (order.TrialDays > 0)
                options.SubscriptionData.TrialPeriodDays = order.TrialDays;
        }

        if (!string.IsNullOrWhiteSpace(order.CustomerEmail))
            options.CustomerEmail = order.CustomerEmail;

        try
        {
            var client = new StripeClient(secretKey);
            var service = new SessionService(client);
            var session = await service.CreateAsync(options, new RequestOptions { IdempotencyKey = order.OrderRef }, ct)
                .ConfigureAwait(false);

            _logger.LogInformation("App Store Stripe session created: {OrderRef} -> {SessionId}", order.OrderRef, session.Id);
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
