using System.Text.Json;
using Microsoft.JSInterop;

namespace MapleShop.UI.Services;

public sealed class CartLine
{
    public string Sku { get; init; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string BillingInterval { get; set; } = "OneTime";
    public int TrialDays { get; set; }
    public int Quantity { get; set; } = 1;

    /// <summary>The catalog category it came from; a pick-one category (one software plan, say) keeps one line.</summary>
    public string? Group { get; set; }

    public bool IsRecurring => BillingInterval is "Monthly" or "Yearly";
    public string PriceSuffix => BillingInterval switch { "Monthly" => "/mo", "Yearly" => "/yr", _ => "" };
    public decimal LineTotal => Price * Quantity;
}

/// <summary>
/// The store cart. Lines carry a quantity (e.g. one licence per location) and their own cadence, so a
/// cart can hold one-time items next to monthly plans — checkout turns that into one Stripe
/// subscription whose first invoice also charges the one-time items. Raises <see cref="OnChange"/> so
/// the cart button, drawer and add buttons re-render together. Scoped, so all interactive islands on a
/// page share one instance; saved to the browser's localStorage so it survives a reload or a cancelled
/// Stripe page. Prices here are for display only — checkout re-reads them from the catalog.
/// The site's /shop page keeps its cart in the browser (site.js) under the same key and format, so
/// /shop/success empties that one too.
/// </summary>
public sealed class CartState
{
    public const int MaxQuantity = 99;
    private const string StorageKey = "mapleshop.cart";

    private readonly IJSRuntime _js;
    private readonly List<CartLine> _lines = new();
    private Task? _restore;

    public CartState(IJSRuntime js) => _js = js;

    public IReadOnlyList<CartLine> Lines => _lines;
    public event Action? OnChange;

    /// <summary>Number of items (quantities summed), for the cart badge.</summary>
    public int Count => _lines.Sum(l => l.Quantity);

    public bool Contains(string sku) => _lines.Any(l => l.Sku == sku);

    /// <summary>True when the cart holds any plan — checkout is then card-only (Stripe subscription).</summary>
    public bool HasSubscription => _lines.Any(l => l.IsRecurring);

    /// <summary>Monthly and yearly plans can't share one subscription.</summary>
    public bool MixesCycles => _lines.Where(l => l.IsRecurring).Select(l => l.BillingInterval).Distinct().Count() > 1;

    /// <summary>The plans' free trial: only when every plan has one (the shortest), as checkout applies it.</summary>
    public int TrialDays
    {
        get
        {
            var plans = _lines.Where(l => l.IsRecurring).ToList();
            return plans.Count > 0 && plans.All(l => l.TrialDays > 0) ? plans.Min(l => l.TrialDays) : 0;
        }
    }

    /// <summary>What the first payment charges: one-time items, plus the plans unless they start on a trial.</summary>
    public decimal DueToday => _lines.Where(l => !l.IsRecurring || TrialDays == 0).Sum(l => l.LineTotal);

    /// <summary>What renews each cycle afterwards.</summary>
    public decimal Recurring => _lines.Where(l => l.IsRecurring).Sum(l => l.LineTotal);

    /// <summary>"/mo" or "/yr" for <see cref="Recurring"/> (the first plan's, when cycles are mixed).</summary>
    public string RecurringSuffix => _lines.FirstOrDefault(l => l.IsRecurring)?.PriceSuffix ?? "";

    /// <param name="group">The item's catalog category.</param>
    /// <param name="pickOne">The category sells one item at a time: this one replaces any other from it.</param>
    public void Add(string sku, string name, decimal price, string billingInterval = "OneTime", int trialDays = 0,
        string? group = null, bool pickOne = false)
    {
        if (string.IsNullOrWhiteSpace(sku)) return;
        if (pickOne && group is not null) _lines.RemoveAll(l => l.Group == group && l.Sku != sku);
        var line = _lines.FirstOrDefault(l => l.Sku == sku);
        if (line is not null) line.Quantity = Math.Min(MaxQuantity, line.Quantity + 1);
        else _lines.Add(new CartLine
        {
            Sku = sku, Name = name, Price = price, BillingInterval = billingInterval, TrialDays = trialDays, Group = group
        });
        Changed();
    }

    public void SetQuantity(string sku, int quantity)
    {
        var line = _lines.FirstOrDefault(l => l.Sku == sku);
        if (line is null) return;
        if (quantity <= 0) { Remove(sku); return; }
        line.Quantity = Math.Min(MaxQuantity, quantity);
        Changed();
    }

    public void Remove(string sku)
    {
        if (_lines.RemoveAll(l => l.Sku == sku) > 0) Changed();
    }

    public void Clear()
    {
        if (_lines.Count == 0) return;
        _lines.Clear();
        Changed();
    }

    /// <summary>Brings saved lines up to date with the live catalog: new names/prices/cadence, and
    /// anything no longer sold dropped.</summary>
    public void Reprice(IReadOnlyCollection<CatalogProduct> catalog)
    {
        var changed = false;
        foreach (var line in _lines.ToList())
        {
            var p = catalog.FirstOrDefault(c => c.Sku == line.Sku && c.Active);
            if (p is null) { _lines.Remove(line); changed = true; continue; }
            if (line.Name == p.Name && line.Price == p.Price && line.BillingInterval == p.BillingInterval && line.TrialDays == p.TrialDays) continue;
            (line.Name, line.Price, line.BillingInterval, line.TrialDays) = (p.Name, p.Price, p.BillingInterval, p.TrialDays);
            changed = true;
        }
        if (changed) Changed();
    }

    /// <summary>Loads the saved cart once per circuit; every island awaits the same load. Call from
    /// OnAfterRenderAsync (JS isn't available while prerendering).</summary>
    public Task RestoreAsync() => _restore ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            var saved = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<CartLine>>(json);
            if (saved is null || saved.Count == 0) return;
            foreach (var line in saved.Where(l => !string.IsNullOrWhiteSpace(l.Sku) && !Contains(l.Sku)))
            {
                line.Quantity = Math.Clamp(line.Quantity, 1, MaxQuantity);
                _lines.Add(line);
            }
            OnChange?.Invoke();
        }
        catch (Exception ex) when (ex is JSException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            // Storage blocked, unreadable or the circuit went away: start with an empty cart.
        }
    }

    private void Changed()
    {
        OnChange?.Invoke();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_lines.Count == 0) await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
            else await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_lines));
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            // Best effort: the cart still works for this visit.
        }
    }
}
