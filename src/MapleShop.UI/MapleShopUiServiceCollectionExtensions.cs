using Microsoft.Extensions.DependencyInjection;
using MapleShop.UI.Services;

namespace MapleShop.UI;

public static class MapleShopUiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store UI: the per-circuit <see cref="CartState"/> and a typed
    /// <see cref="MapleShopClient"/> pointed at the backend checkout API with the
    /// shared API key attached to every request.
    /// </summary>
    public static IServiceCollection AddMapleShopUi(
        this IServiceCollection services, Action<MapleShopUiOptions> configure)
    {
        var options = new MapleShopUiOptions();
        configure(options);

        services.AddScoped<CartState>();

        services.AddHttpClient<MapleShopClient>(http =>
        {
            if (!string.IsNullOrWhiteSpace(options.BackendBaseUrl))
                http.BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-AppStore-Key", options.ApiKey);
        });

        return services;
    }
}
