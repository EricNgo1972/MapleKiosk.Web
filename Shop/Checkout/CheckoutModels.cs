namespace MapleKiosk.Web.Shop.Checkout;

public sealed class CheckoutItem
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

public sealed class CreateCheckoutRequest
{
    public List<CheckoutItem> Items { get; set; } = new();
    public string Method { get; set; } = "Stripe";
    public string? Email { get; set; }
    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
    public string? Currency { get; set; }
}

public sealed class CheckoutResult
{
    public bool Success { get; set; }
    public string? OrderRef { get; set; }
    public string Method { get; set; } = "";
    public decimal Total { get; set; }
    public string Currency { get; set; } = "";
    public string? StripeUrl { get; set; }
    public string? QrPayload { get; set; }
    public string? QrImageDataUri { get; set; }
    public string? Error { get; set; }
}

public sealed class OrderStatusResult
{
    public string OrderRef { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public string Currency { get; set; } = "";
}
