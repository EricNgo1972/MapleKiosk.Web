using System.Security.Claims;
using MapleKiosk.Web.Onboarding;

namespace MapleKiosk.Web.Services;

/// <summary>
/// Allowlist for the central-OAuth login (oauth.maplekiosk.ca). Two kinds of
/// account get in:
/// <list type="bullet">
/// <item>Team — <c>@spc-technology.com</c> addresses plus the owner account → role <see cref="AdminRole"/>.</item>
/// <item>Customers — any email on the onboarding list (added by staff after the
/// deposit) → role <see cref="CustomerRole"/>, which only reaches their own setup page.</item>
/// </list>
/// Returning claims = allow; null = deny (the auth callback then redirects to
/// /access-denied). This is the <c>AuthEmailValidator</c> the
/// SPC.Infrastructure.Auth callback invokes after verifying the host-signed JWT.
/// </summary>
public static class AppAuthValidator
{
    public const string AdminRole = "Admin";
    public const string CustomerRole = "Customer";

    private const string AllowedDomain = "@spc-technology.com";
    private static readonly string[] AllowedEmails = { "ericngo0305@gmail.com" };

    public static bool IsStaff(string canonicalEmail) =>
        canonicalEmail.EndsWith(AllowedDomain, StringComparison.Ordinal)
        || AllowedEmails.Contains(canonicalEmail, StringComparer.Ordinal);

    public static async Task<IReadOnlyList<Claim>?> ValidateAsync(string email, OnboardingStore onboarding)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var canonical = OnboardingRecord.CanonicalEmail(email);

        if (IsStaff(canonical))
            return new List<Claim> { new(ClaimTypes.Role, AdminRole) };

        var records = await onboarding.FindByEmailAsync(canonical);
        if (records.Count > 0)
            return new List<Claim> { new(ClaimTypes.Role, CustomerRole) };

        return null;
    }
}
