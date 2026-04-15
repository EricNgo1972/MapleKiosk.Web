namespace MapleKiosk.Web.Services;

public record CartItem(string Sku, string Name, decimal Price, int Quantity);

public class CartService
{
    public List<CartItem> Items { get; } = new();

    public event Action? OnChange;

    public decimal Total => Items.Sum(i => i.Price * i.Quantity);

    public void Add(CartItem item)
    {
        Items.Add(item);
        OnChange?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        OnChange?.Invoke();
    }
}
