namespace MapleShop.UI;

/// <summary>
/// Host-supplied settings for the store UI. The host resolves these from its own
/// config-free secret source (IKeyVault) and passes them to <c>AddMapleShopUi</c>.
/// </summary>
public sealed class MapleShopUiOptions
{
    /// <summary>Base URL of the backend checkout API, e.g. "https://app.maplekiosk.ca/".</summary>
    public string BackendBaseUrl { get; set; } = "";

    /// <summary>Shared API key sent as the X-AppStore-Key header on every call.</summary>
    public string ApiKey { get; set; } = "";
}
