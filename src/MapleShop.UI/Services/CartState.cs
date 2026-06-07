namespace MapleShop.UI.Services;

public sealed class CartLine
{
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal PriceUsd { get; init; }
    public string BillingInterval { get; init; } = "OneTime";

    public bool IsRecurring => BillingInterval is "Monthly" or "Yearly";
    public string PriceSuffix => BillingInterval switch { "Monthly" => "/mo", "Yearly" => "/yr", _ => "" };
}

/// <summary>
/// Per-circuit cart for the store UI. This is a SaaS catalog — each product is a
/// plan/licence, so a line is simply present-or-not (no quantities). Adding a SKU
/// already in the cart is a no-op. Raises <see cref="OnChange"/> so the cart
/// button, drawer and add buttons re-render together. Scoped, so all interactive
/// islands on a page share one instance.
/// </summary>
public sealed class CartState
{
    private readonly List<CartLine> _lines = new();

    public IReadOnlyList<CartLine> Lines => _lines;
    public event Action? OnChange;

    public int Count => _lines.Count;
    public decimal TotalUsd => _lines.Sum(l => l.PriceUsd);

    public bool Contains(string sku) => _lines.Any(l => l.Sku == sku);

    /// <summary>True when the cart holds any subscription plan — checkout then
    /// restricts to a single plan via Stripe (no VietQR, no mixing).</summary>
    public bool HasSubscription => _lines.Any(l => l.IsRecurring);

    public void Add(string sku, string name, decimal priceUsd, string billingInterval = "OneTime")
    {
        if (string.IsNullOrWhiteSpace(sku) || Contains(sku)) return;
        _lines.Add(new CartLine { Sku = sku, Name = name, PriceUsd = priceUsd, BillingInterval = billingInterval });
        OnChange?.Invoke();
    }

    public void Remove(string sku)
    {
        if (_lines.RemoveAll(l => l.Sku == sku) > 0)
            OnChange?.Invoke();
    }

    public void Clear()
    {
        if (_lines.Count == 0) return;
        _lines.Clear();
        OnChange?.Invoke();
    }
}
