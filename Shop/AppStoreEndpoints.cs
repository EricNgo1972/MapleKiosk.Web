using System.Security.Cryptography;
using System.Text;
using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Checkout;
using MapleKiosk.Web.Shop.Config;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments.VietQr;
using Stripe;
// Stripe also defines a CheckoutService; alias ours so the unqualified name is unambiguous.
using CheckoutService = MapleKiosk.Web.Shop.Checkout.CheckoutService;

namespace MapleKiosk.Web.Shop;

public static class AppStoreEndpoints
{
    private const string ApiKeyHeader = "X-AppStore-Key";

    /// <summary>Maps the in-app checkout API. Call once after routing is set up.
    /// Designed to be lifted into a dedicated checkout service later unchanged.</summary>
    public static IEndpointRouteBuilder MapAppStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/checkout").DisableAntiforgery();

        var api = group.MapGroup("").AddEndpointFilter(ApiKeyFilter);
        api.MapGet("/catalog", async (IAppCatalog catalog, CancellationToken ct) => Results.Ok(await catalog.GetActiveAsync(ct)));
        api.MapPost("/orders", async (CreateCheckoutRequest req, CheckoutService checkout, CancellationToken ct) =>
        {
            var result = await checkout.CreateAsync(req, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });
        api.MapGet("/orders/{orderRef}/status", async (string orderRef, CheckoutService checkout, CancellationToken ct) =>
        {
            var status = await checkout.GetStatusAsync(orderRef, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        var hooks = group.MapGroup("/webhooks");
        hooks.MapPost("/stripe", HandleStripeWebhookAsync);
        hooks.MapPost("/bank", HandleBankWebhookAsync);

        return app;
    }

    // API key is optional: when AppStore/ApiKey is unset (e.g. same-app dev) all
    // callers are allowed; when set, it must match. The header is fixed-time compared.
    private static async ValueTask<object?> ApiKeyFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var config = ctx.HttpContext.RequestServices.GetRequiredService<AppStoreConfig>();
        var expected = await config.GetApiKeyAsync();
        if (!string.IsNullOrEmpty(expected))
        {
            var provided = ctx.HttpContext.Request.Headers[ApiKeyHeader].FirstOrDefault();
            if (!FixedTimeEquals(provided, expected)) return Results.Unauthorized();
        }
        return await next(ctx);
    }

    private static async Task<IResult> HandleStripeWebhookAsync(
        HttpRequest request, AppStoreConfig config, AppOrderService orders, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("AppStore.StripeWebhook");
        var secret = await config.GetStripeWebhookSecretAsync();
        if (string.IsNullOrEmpty(secret))
        {
            log.LogError("App Store Stripe webhook secret not configured.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        string json;
        using (var reader = new StreamReader(request.Body)) json = await reader.ReadToEndAsync();
        var signature = request.Headers["Stripe-Signature"].FirstOrDefault() ?? "";

        Event stripeEvent;
        try { stripeEvent = EventUtility.ConstructEvent(json, signature, secret); }
        catch (StripeException ex) { log.LogWarning(ex, "Stripe webhook signature verification failed."); return Results.BadRequest(); }

        if (stripeEvent.Type is EventTypes.CheckoutSessionCompleted or EventTypes.CheckoutSessionAsyncPaymentSucceeded)
        {
            if (stripeEvent.Data.Object is Stripe.Checkout.Session session
                && !string.IsNullOrEmpty(session.ClientReferenceId)
                && string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
            {
                await orders.MarkPaidAsync(session.ClientReferenceId, session.PaymentIntentId ?? session.Id,
                    request.HttpContext.RequestAborted);
            }
        }

        return Results.Ok();
    }

    private static async Task<IResult> HandleBankWebhookAsync(
        HttpRequest request, IBankConfirmationSource source, AppOrderService orders, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("AppStore.BankWebhook");
        var ct = request.HttpContext.RequestAborted;

        string raw;
        using (var reader = new StreamReader(request.Body)) raw = await reader.ReadToEndAsync();

        var credit = await source.ParseAsync(request, raw, ct);
        if (credit is null) return Results.Unauthorized();

        var order = await orders.GetAsync(credit.Reference, ct);
        if (order is null) { log.LogWarning("Bank webhook: no order for {Reference}; acking.", credit.Reference); return Results.Ok(); }

        if (credit.Amount + 0.5m < order.Total)
        {
            log.LogWarning("Bank webhook: {Reference} underpaid ({Paid} < {Due}); not confirming.",
                credit.Reference, credit.Amount, order.Total);
            return Results.Ok();
        }

        await orders.MarkPaidAsync(credit.Reference, credit.ProviderTxnId, ct);
        return Results.Ok();
    }

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
