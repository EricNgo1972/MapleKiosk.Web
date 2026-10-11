using MapleKiosk.Web.Shop.Notifications;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>
/// Buys a phone-line order's number once it is paid. <see cref="Orders.AppOrderService.MarkPaidAsync"/> calls its
/// sinks only on the single winning Pending→Paid change, which Stripe's webhook (the only writer of payment state)
/// drives; <see cref="PhoneLineService.FulfilAsync"/> then claims the order, so even a second call buys nothing.
/// The service is resolved per call: it needs the order store, which itself depends on the sinks.
/// </summary>
public sealed class PhoneLineFulfilment : IOrderPaidSink
{
    private readonly IServiceProvider _services;

    public PhoneLineFulfilment(IServiceProvider services) => _services = services;

    public async Task OnOrderPaidAsync(OrderPaidNotification notification, CancellationToken ct = default)
    {
        if (notification.Source != PhoneLineService.Source) return;
        // Never throws (the service turns every problem into a retryable status on the order).
        await _services.GetRequiredService<PhoneLineService>().FulfilAsync(notification.OrderRef, ct: ct).ConfigureAwait(false);
    }
}
