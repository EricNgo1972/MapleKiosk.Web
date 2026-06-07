using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MapleKiosk.Web.Shop.Config;

namespace MapleKiosk.Web.Shop.Payments.VietQr;

/// <summary>
/// Confirmation source for Sepay (https://sepay.vn). Sepay POSTs a webhook per
/// transaction with <c>Authorization: Apikey &lt;secret&gt;</c>. We accept only
/// inbound ("in") credits and recover the OrderRef from the transfer content,
/// which the QR placed in the memo.
/// </summary>
public sealed partial class SepayConfirmationSource : IBankConfirmationSource
{
    private readonly AppStoreConfig _config;
    private readonly ILogger<SepayConfirmationSource> _logger;

    public SepayConfirmationSource(AppStoreConfig config, ILogger<SepayConfirmationSource> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<BankCreditEvent?> ParseAsync(HttpRequest request, string rawBody, CancellationToken ct = default)
    {
        var secret = await _config.GetBankAggregatorSecretAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Bank aggregator secret not configured; rejecting webhook.");
            return null;
        }

        var auth = request.Headers.Authorization.ToString();
        if (!FixedTimeEquals(auth, $"Apikey {secret}"))
        {
            _logger.LogWarning("Bank webhook rejected: bad Authorization header.");
            return null;
        }

        JsonElement root;
        try { root = JsonDocument.Parse(rawBody).RootElement; }
        catch (JsonException ex) { _logger.LogWarning(ex, "Bank webhook body invalid JSON."); return null; }

        if (!string.Equals(GetString(root, "transferType"), "in", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Bank webhook ignored: not an inbound credit.");
            return null;
        }

        var amount = GetDecimal(root, "transferAmount");
        var content = GetString(root, "content") ?? "";
        var txnId = GetString(root, "referenceCode") ?? GetString(root, "id") ?? Guid.NewGuid().ToString("N");

        var orderRef = ExtractOrderRef(content);
        if (orderRef is null)
        {
            _logger.LogWarning("Bank webhook: no OrderRef in content '{Content}'.", content);
            return null;
        }

        return new BankCreditEvent(orderRef, amount, "VND", txnId);
    }

    private static string? ExtractOrderRef(string content)
    {
        var m = OrderRefRegex().Match(content);
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex("MK[0-9]{12}[0-9A-Fa-f]{4}")]
    private static partial Regex OrderRefRegex();

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    private static decimal GetDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(v.GetString(), out var d) ? d : 0m,
            _ => 0m
        };
    }
}
