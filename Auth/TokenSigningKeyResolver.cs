using Microsoft.Extensions.Configuration;

namespace SPC.Infrastructure.Auth;

/// <summary>
/// Resolves <see cref="AuthOptions.TokenSigningKey"/> through a cascade that
/// mirrors the <c>SPC.BO.Security.IKeyVault</c> pattern, kept local here to
/// avoid the auth library taking a dependency on the BO layer.
///
/// Resolution order (first non-empty wins):
///   1. Process environment variable <c>AUTH_TOKEN_SIGNING_KEY</c> — operator
///      rotates the key via systemd / Windows Service env / Docker secrets
///      without touching appsettings or rebuilding.
///   2. <see cref="IConfiguration"/> — <c>Authentication:TokenSigningKey</c>
///      from appsettings.json, user secrets, command-line, or
///      <c>AUTHENTICATION__TOKENSIGNINGKEY</c> mapped through ASP.NET's env
///      provider.
///   3. Embedded <see cref="SecretVault"/> XOR fallback — operational backstop
///      for sealed deployments where neither of the above is set.
///
/// Returns empty string when nothing resolves; JWT verification then fails and
/// the user lands on <c>/access-denied</c>.
/// </summary>
internal static class TokenSigningKeyResolver
{
    public const string ConfigPath = "Authentication:TokenSigningKey";
    public const string EnvVar = "AUTH_TOKEN_SIGNING_KEY";

    public static string Resolve(IConfiguration config)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        var fromConfig = config[ConfigPath];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.Trim();

        if (SecretVault.TryResolve(ConfigPath, out var fromVault)) return fromVault;

        return "";
    }
}
