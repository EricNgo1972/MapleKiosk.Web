using System.Security.Claims;
using MapleKiosk.Web.Onboarding;

namespace MapleKiosk.Web.Services;

/// <summary>
/// Allowlist for the central-OAuth login (oauth.maplekiosk.ca). Two kinds of
/// account get in:
/// <list type="bullet">
/// <item>Team — <c>@spc-technology.com</c> addresses, the built-in accounts in <c>TeamEmails</c>
/// (the owner, MagSoft; can't be removed, so nobody locks the team out) and the team members
/// added on /admin/users (<see cref="UserDirectory"/>) → role <see cref="AdminRole"/>.</item>
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

    /// <summary>Every address on this domain is team (shown on /admin/users).</summary>
    public const string TeamDomain = "@spc-technology.com";

    /// <summary>Built-in team accounts outside the domain; always admin, can't be removed on /admin/users.</summary>
    public static readonly IReadOnlyList<string> TeamEmails = ["ericngo0305@gmail.com", "magsoft@magsoft.us"];

    public static bool IsBuiltInStaff(string canonicalEmail) =>
        canonicalEmail.EndsWith(TeamDomain, StringComparison.Ordinal)
        || TeamEmails.Contains(canonicalEmail, StringComparer.Ordinal);

    public static async Task<bool> IsStaffAsync(string canonicalEmail, UserDirectory users) =>
        IsBuiltInStaff(canonicalEmail) || await users.IsTeamAsync(canonicalEmail);

    public static async Task<IReadOnlyList<Claim>?> ValidateAsync(string email, OnboardingStore onboarding, UserDirectory users)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var canonical = OnboardingRecord.CanonicalEmail(email);

        if (await IsStaffAsync(canonical, users))
            return new List<Claim> { new(ClaimTypes.Role, AdminRole) };

        // Archived-only customers no longer sign in; suspended ones do, and see the "paused" page.
        var records = await onboarding.FindByEmailAsync(canonical);
        if (records.Any(r => r.Access != OnboardingAccess.Archived))
            return new List<Claim> { new(ClaimTypes.Role, CustomerRole) };

        return null;
    }
}
