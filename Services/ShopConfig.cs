namespace MapleKiosk.Web.Services;

/// <summary>
/// Config-free resolver for the two values the store UI needs: the backend
/// checkout API base URL and the shared API key. Mirrors the monorepo IKeyVault
/// cascade for the two tiers this thin site can reach without dragging in the
/// CSLA/DAL stack: environment variable first, then the central Azure Table
/// key-value store (partition <c>AppStore</c>, column <c>Value</c>) read via the
/// site's existing <c>STORAGE_CONNECTION_STRING</c>. No secrets in committed config.
///
/// Operators set either the env vars (APPSTORE_BACKEND_URL / APPSTORE_API_KEY) or
/// rows AppStore/BackendBaseUrl and AppStore/ApiKey in the keyvalue table.
/// </summary>
public static class ShopConfig
{
    public static async Task<(string BackendUrl, string ApiKey)> ResolveAsync()
    {
        var backendUrl = await ResolveAsync("APPSTORE_BACKEND_URL", "BackendBaseUrl");
        var apiKey = await ResolveAsync("APPSTORE_API_KEY", "ApiKey");
        return (backendUrl, apiKey);
    }

    private static Task<string> ResolveAsync(string envVar, string rowKey) =>
        KeyValueTable.ResolveAsync(envVar, "AppStore", rowKey);
}
