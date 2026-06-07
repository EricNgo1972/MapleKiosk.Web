using System.Security.Claims;

namespace MapleKiosk.Web.Services;

/// <summary>
/// Allowlist for the central-OAuth login (oauth.maplekiosk.ca). Access is
/// restricted to <c>@spc-technology.com</c> addresses plus the single owner
/// account. Returning claims = allow; null = deny (the auth callback then
/// redirects to /access-denied). This is the <c>AuthEmailValidator</c> the
/// SPC.Infrastructure.Auth callback invokes after verifying the host-signed JWT.
/// </summary>
public static class AppAuthValidator
{
    private const string AllowedDomain = "@spc-technology.com";
    private static readonly string[] AllowedEmails = { "ericngo0305@gmail.com" };

    public static Task<IReadOnlyList<Claim>?> ValidateAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Task.FromResult<IReadOnlyList<Claim>?>(null);

        var canonical = email.Trim().ToLowerInvariant();

        var allowed = canonical.EndsWith(AllowedDomain, StringComparison.Ordinal)
                      || AllowedEmails.Contains(canonical, StringComparer.Ordinal);

        if (!allowed)
            return Task.FromResult<IReadOnlyList<Claim>?>(null);

        IReadOnlyList<Claim> claims = new List<Claim>
        {
            new(ClaimTypes.Role, "Admin")
        };
        return Task.FromResult<IReadOnlyList<Claim>?>(claims);
    }
}
