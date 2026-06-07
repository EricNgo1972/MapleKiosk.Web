namespace MapleShop.UI.Services;

/// <summary>A product as returned by GET /api/checkout/catalog.</summary>
public sealed class CatalogProduct
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal PriceUsd { get; set; }
    public decimal PriceVnd { get; set; }
    public string? ImageUrl { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>OneTime / Monthly / Yearly.</summary>
    public string BillingInterval { get; set; } = "OneTime";
    public int TrialDays { get; set; }
    public List<string> Features { get; set; } = new();

    public bool IsRecurring =>
        BillingInterval is "Monthly" or "Yearly";

    public string PriceSuffix => BillingInterval switch
    {
        "Monthly" => "/mo",
        "Yearly" => "/yr",
        _ => ""
    };
}

/// <summary>One line sent to POST /api/checkout/orders (SKU + quantity only).</summary>
public sealed class CheckoutItem
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

public sealed class CreateOrderRequest
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

public sealed class OrderStatus
{
    public string OrderRef { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public string Currency { get; set; } = "";
}
