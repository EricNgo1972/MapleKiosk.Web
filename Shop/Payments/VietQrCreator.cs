using MapleKiosk.Web.Shop.Config;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments.Qr;

namespace MapleKiosk.Web.Shop.Payments;

/// <summary>
/// Builds a VietQR (NAPAS EMVCo) payment QR for an app-store order, paying into
/// the platform receiving account (config-free). The OrderRef is the transfer
/// memo so the bank aggregator webhook can match the incoming credit.
/// </summary>
public sealed class VietQrCreator
{
    private readonly AppStoreConfig _config;
    private readonly QrImageService _qrImage;
    private readonly ILogger<VietQrCreator> _logger;

    public VietQrCreator(AppStoreConfig config, QrImageService qrImage, ILogger<VietQrCreator> logger)
    {
        _config = config;
        _qrImage = qrImage;
        _logger = logger;
    }

    public sealed record Result(bool Success, string? Payload, string? QrImageDataUri, string? Error);

    public async Task<Result> CreateAsync(AppOrder order, CancellationToken ct = default)
    {
        var bankCode = await _config.GetVietQrBankCodeAsync().ConfigureAwait(false);
        var account = await _config.GetVietQrAccountNumberAsync().ConfigureAwait(false);
        var accountName = await _config.GetVietQrAccountNameAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(bankCode) || string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(accountName))
            return new Result(false, null, null, "VietQR is not configured.");

        try
        {
            var payload = VietQrPayloadBuilder.Build(new VietQrBuildInput(
                BankCodeOrBin: bankCode,
                AccountNumber: account,
                AccountName: accountName,
                AmountVnd: order.Total,
                OrderReference: order.OrderRef,
                Note: order.OrderRef));

            var dataUri = _qrImage.ToDataUri(payload);
            _logger.LogInformation("App Store VietQR built: {OrderRef}, Amount={Amount} VND", order.OrderRef, order.Total);
            return new Result(true, payload, dataUri, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App Store VietQR build failed: {OrderRef}", order.OrderRef);
            return new Result(false, null, null, ex.Message);
        }
    }
}
