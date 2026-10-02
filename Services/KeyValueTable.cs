using Azure.Data.Tables;

namespace MapleKiosk.Web.Services;

/// <summary>
/// The shared Azure Table key-value store (column <c>Value</c>), read via the
/// site's <c>STORAGE_CONNECTION_STRING</c>. One table everywhere — <c>keyvalue</c>,
/// unless <c>GATEWAY_SECRETS_TABLE</c> overrides it — matching the monorepo's
/// SecretVault rule (the old keyvalueProduction split is gone).
/// </summary>
public static class KeyValueTable
{
    public static string Name =>
        Environment.GetEnvironmentVariable("GATEWAY_SECRETS_TABLE")?.Trim() is { Length: > 0 } name
            ? name
            : "keyvalue";

    /// <summary>Env var first, then row <paramref name="partition"/>/<paramref name="row"/>; "" when neither is set.</summary>
    public static async Task<string> ResolveAsync(string envVar, string partition, string row)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var connection = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection)) return "";

        try
        {
            var table = new TableClient(connection, Name);
            var resp = await table.GetEntityIfExistsAsync<TableEntity>(partition, row);
            if (resp.HasValue && resp.Value!.TryGetValue("Value", out var v))
                return v?.ToString()?.Trim() ?? "";
        }
        catch
        {
            // Degrade — callers treat "" as not configured.
        }

        return "";
    }
}
