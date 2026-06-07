namespace MapleKiosk.Web.Shop.Notifications;

/// <summary>A delivery channel for the "payment finished" signal, invoked only on
/// the winning Pending→Paid transition. Implementations must log and swallow
/// their own failures.</summary>
public interface IOrderPaidSink
{
    Task OnOrderPaidAsync(OrderPaidNotification notification, CancellationToken ct = default);
}
