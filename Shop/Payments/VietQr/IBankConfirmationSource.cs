namespace MapleKiosk.Web.Shop.Payments.VietQr;

/// <summary>A normalized incoming bank credit, distilled from a provider webhook.</summary>
public sealed record BankCreditEvent(string Reference, decimal Amount, string Currency, string ProviderTxnId);

/// <summary>
/// Verifies and parses an inbound bank-aggregator webhook (Sepay/Casso) into a
/// <see cref="BankCreditEvent"/> — the seam that gives VietQR an automatic "paid"
/// signal. Returns null when the request can't be authenticated or isn't a
/// matchable inbound credit.
/// </summary>
public interface IBankConfirmationSource
{
    Task<BankCreditEvent?> ParseAsync(HttpRequest request, string rawBody, CancellationToken ct = default);
}
