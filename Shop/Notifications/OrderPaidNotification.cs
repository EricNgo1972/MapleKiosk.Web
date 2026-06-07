namespace MapleKiosk.Web.Shop.Notifications;

/// <summary>The "payment finished" fact, emitted once per order from
/// <c>AppOrderService.MarkPaidAsync</c>; consumed by every signal sink and the
/// in-process OrderPaid event.</summary>
public sealed record OrderPaidNotification(
    string OrderRef,
    decimal Total,
    string Currency,
    string Method,
    string? CustomerEmail,
    string? ProviderTxnId,
    DateTimeOffset PaidAt);
