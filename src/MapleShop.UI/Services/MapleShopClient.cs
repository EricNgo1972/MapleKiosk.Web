using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace MapleShop.UI.Services;

/// <summary>
/// Server-side HTTP client to the checkout API. Runs inside the host's Blazor
/// Server circuit (never the browser), so the API key stays secret. When
/// <c>BackendBaseUrl</c> is configured it calls that remote API; when it's empty
/// the API is assumed to be hosted by the SAME app, so requests resolve against
/// the app's own base URI (from <see cref="NavigationManager"/>, which is valid
/// in an interactive circuit — unlike HttpContext) — zero-config for the in-app case.
/// </summary>
public sealed class MapleShopClient
{
    private readonly HttpClient _http;
    private readonly NavigationManager _nav;
    private readonly ILogger<MapleShopClient> _logger;

    public MapleShopClient(HttpClient http, NavigationManager nav, ILogger<MapleShopClient> logger)
    {
        _http = http;
        _nav = nav;
        _logger = logger;
    }

    public async Task<List<CatalogProduct>> GetCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<CatalogProduct>>(Resolve("api/checkout/catalog"), ct)
                   ?? new List<CatalogProduct>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load App Store catalog.");
            return new List<CatalogProduct>();
        }
    }

    public async Task<CheckoutResult> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default)
    {
        Uri uri;
        try { uri = Resolve("api/checkout/orders"); }
        catch (InvalidOperationException)
        {
            return new CheckoutResult { Success = false, Error = "Store backend is not configured." };
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(uri, request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Checkout call failed: {Status} {Body}", (int)response.StatusCode, body);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return new CheckoutResult { Success = false, Error = "Checkout rejected the request (API key mismatch)." };

                var failed = TryRead(body);
                return failed ?? new CheckoutResult { Success = false, Error = $"Checkout failed ({(int)response.StatusCode})." };
            }

            var result = await response.Content.ReadFromJsonAsync<CheckoutResult>(cancellationToken: ct);
            return result ?? new CheckoutResult { Success = false, Error = "Empty response from checkout service." };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Checkout backend unreachable at {Uri}.", uri);
            return new CheckoutResult { Success = false, Error = "Checkout service unreachable." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected checkout error.");
            return new CheckoutResult { Success = false, Error = "Unexpected checkout error." };
        }
    }

    public async Task<OrderStatus?> GetStatusAsync(string orderRef, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<OrderStatus>(
                Resolve($"api/checkout/orders/{Uri.EscapeDataString(orderRef)}/status"), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status check failed for {OrderRef}.", orderRef);
            return null;
        }
    }

    // Absolute URI from the configured base, or — when unset — the app's own
    // base URI (same-origin in-app hosting). NavigationManager.BaseUri is valid
    // during interactive circuit callbacks, where there is no HttpContext.
    private Uri Resolve(string path)
        => _http.BaseAddress is not null
            ? new Uri(_http.BaseAddress, path)
            : new Uri(new Uri(_nav.BaseUri), path);

    private static CheckoutResult? TryRead(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CheckoutResult>(
                body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch { return null; }
    }
}
