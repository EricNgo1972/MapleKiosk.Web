using Azure.Data.Tables;

namespace MapleKiosk.Web.Services;

/// <summary>
/// Config-free resolver for the central-auth settings, mirroring the monorepo
/// convention: env var → Azure Table key-value store (partition
/// <c>Authentication</c>, column <c>Value</c>), read via the site's
/// <c>STORAGE_CONNECTION_STRING</c>. <c>AuthHostUrl</c> defaults to
/// oauth.maplekiosk.ca. The values are injected into IConfiguration so
/// <c>AddSPCAuth</c> binds them — nothing in appsettings.
/// </summary>
public static class AuthConfig
{
    public const string DefaultAuthHostUrl = "https://oauth.maplekiosk.ca";

    public static async Task<IReadOnlyDictionary<string, string?>> ResolveAsync()
    {
        var authHostUrl = await ResolveAsync("AUTHENTICATION__AUTHHOSTURL", "AuthHostUrl");
        if (string.IsNullOrWhiteSpace(authHostUrl)) authHostUrl = DefaultAuthHostUrl;

        var signingKey = await ResolveAsync("AUTH_TOKEN_SIGNING_KEY", "TokenSigningKey");

        var map = new Dictionary<string, string?> { ["Authentication:AuthHostUrl"] = authHostUrl };
        if (!string.IsNullOrWhiteSpace(signingKey))
            map["Authentication:TokenSigningKey"] = signingKey;
        return map;
    }

    private static async Task<string> ResolveAsync(string envVar, string rowKey)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var connection = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection)) return "";

        try
        {
            var tableName = string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Production", StringComparison.OrdinalIgnoreCase)
                ? "keyvalueProduction" : "keyvalue";

            var table = new TableClient(connection, tableName);
            var resp = await table.GetEntityIfExistsAsync<TableEntity>("Authentication", rowKey);
            if (resp.HasValue && resp.Value!.TryGetValue("Value", out var v))
                return v?.ToString()?.Trim() ?? "";
        }
        catch
        {
            // Degrade — AuthHostUrl default still applies; signing key falls back
            // to the embedded vault inside SPC.Infrastructure.Auth.
        }

        return "";
    }
}
