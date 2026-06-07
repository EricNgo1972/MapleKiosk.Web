using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MapleKiosk.Web.Shop.Config;

namespace MapleKiosk.Web.Shop.Notifications;

/// <summary>
/// Signal (a): POSTs an <c>order.paid</c> event to the customer's configured
/// callback URL, signed with HMAC-SHA256 over "{t}.{body}" (Stripe-style).
/// No-op when no URL is configured; retries a few times on transient failures.
/// </summary>
public sealed class OutboundWebhookNotifier : IOrderPaidSink
{
    private const string SignatureHeader = "X-MapleShop-Signature";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AppStoreConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OutboundWebhookNotifier> _logger;

    public OutboundWebhookNotifier(AppStoreConfig config, IHttpClientFactory httpClientFactory, ILogger<OutboundWebhookNotifier> logger)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task OnOrderPaidAsync(OrderPaidNotification n, CancellationToken ct = default)
    {
        var url = await _config.GetOutboundWebhookUrlAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogDebug("Outbound webhook URL not configured; skipping for {OrderRef}.", n.OrderRef);
            return;
        }

        var body = JsonSerializer.Serialize(new
        {
            type = "order.paid",
            orderRef = n.OrderRef,
            total = n.Total,
            currency = n.Currency,
            method = n.Method,
            providerTxnId = n.ProviderTxnId,
            paidAt = n.PaidAt
        }, JsonOptions);

        var secret = await _config.GetOutboundWebhookSecretAsync().ConfigureAwait(false);
        var signature = Sign(body, secret);

        var client = _httpClientFactory.CreateClient();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                if (signature is not null)
                    content.Headers.TryAddWithoutValidation(SignatureHeader, signature);

                using var response = await client.PostAsync(url, content, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Outbound webhook delivered for {OrderRef} (attempt {Attempt}).", n.OrderRef, attempt);
                    return;
                }
                _logger.LogWarning("Outbound webhook for {OrderRef} got {Status} (attempt {Attempt}).",
                    n.OrderRef, (int)response.StatusCode, attempt);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Outbound webhook for {OrderRef} failed (attempt {Attempt}).", n.OrderRef, attempt);
            }

            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
        }

        _logger.LogError("Outbound webhook for {OrderRef} exhausted retries.", n.OrderRef);
    }

    private static string? Sign(string body, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
