using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Checkout;
using MapleKiosk.Web.Shop.Config;
using MapleKiosk.Web.Shop.Notifications;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments;
using MapleKiosk.Web.Shop.Payments.Qr;
using MapleKiosk.Web.Shop.Payments.VietQr;

namespace MapleKiosk.Web.Shop;

public static class AppStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-app checkout: config, catalog, orders, payment creators,
    /// the bank confirmation source, and the two "payment finished" signal sinks
    /// (outbound webhook + email). Reuses the site's EmailService for receipts and
    /// STORAGE_CONNECTION_STRING for persistence.
    /// </summary>
    public static IServiceCollection AddAppStore(this IServiceCollection services)
    {
        services.AddHttpClient();

        services.AddSingleton<AppStoreConfig>();
        services.AddSingleton<QrImageService>();
        // Azure-Table catalog: one instance serves both the checkout read path
        // (IAppCatalog) and the admin write path (CatalogStore).
        services.AddSingleton<CatalogStore>();
        services.AddSingleton<IAppCatalog>(sp => sp.GetRequiredService<CatalogStore>());
        services.AddSingleton<BlobImageUploader>();
        services.AddSingleton<AppOrderService>();
        services.AddSingleton<StripeCheckoutCreator>();
        services.AddSingleton<VietQrCreator>();
        services.AddSingleton<CheckoutService>();
        services.AddSingleton<IBankConfirmationSource, SepayConfirmationSource>();

        services.AddSingleton<IOrderPaidSink, OutboundWebhookNotifier>();
        services.AddSingleton<IOrderPaidSink, EmailReceiptSender>();

        return services;
    }
}
