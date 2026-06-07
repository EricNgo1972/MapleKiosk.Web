using System.Security.Cryptography;

namespace MapleKiosk.Web.Shop.Orders;

public enum AppOrderStatus { Pending, Paid, Failed, Expired }

public static class AppPaymentMethods
{
    public const string Stripe = "Stripe";
    public const string VietQr = "VietQR";
}

public sealed class AppOrderLine
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>
/// One app-store purchase. <see cref="OrderRef"/> is the public handle — Stripe
/// ClientReferenceId, VietQR memo, and the id the website polls / lands on at the
/// success page — so every confirmation path converges on the same order.
/// </summary>
public sealed class AppOrder
{
    public string OrderRef { get; set; } = NewRef();
    public List<AppOrderLine> Lines { get; set; } = new();
    public string Method { get; set; } = AppPaymentMethods.Stripe;
    public string Currency { get; set; } = "USD";
    public decimal Total { get; set; }

    /// <summary>OneTime / Monthly / Yearly. Recurring orders check out as Stripe
    /// subscriptions (see <c>Catalog.BillingIntervals</c>).</summary>
    public string Interval { get; set; } = "OneTime";

    /// <summary>Trial days for a subscription order (0 = none).</summary>
    public int TrialDays { get; set; }
    public AppOrderStatus Status { get; set; } = AppOrderStatus.Pending;
    public string? CustomerEmail { get; set; }
    public string? ProviderTxnId { get; set; }
    public string? ProviderRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(30);

    // ASCII, ≤25 chars (VietQR EMV tag 62.01): "MK" + UTC yyMMddHHmmss + 4 hex = 18.
    public static string NewRef()
    {
        var stamp = DateTime.UtcNow.ToString("yyMMddHHmmss");
        var rand = Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        return $"MK{stamp}{rand}";
    }
}
