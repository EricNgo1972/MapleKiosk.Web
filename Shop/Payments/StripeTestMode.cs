using System.Collections.Concurrent;
using System.Security.Claims;
using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Shop.Payments;

/// <summary>
/// Which signed-in admins check out against the Stripe test account (switched on /shop/admin). Only their own
/// checkouts change — every other visitor keeps paying live — and the orders they place are marked
/// <c>AppOrder.IsTest</c>. Held in memory on purpose: a restart or deploy puts everyone back on live.
/// </summary>
public sealed class StripeTestMode
{
    private readonly ConcurrentDictionary<string, bool> _on = new(StringComparer.Ordinal);

    /// <summary>True when <paramref name="user"/> is an admin who has test mode on.</summary>
    public bool IsOn(ClaimsPrincipal? user) => Key(user) is { } key && _on.ContainsKey(key);

    public void Set(ClaimsPrincipal user, bool on)
    {
        if (Key(user) is not { } key) return;
        if (on) _on[key] = true;
        else _on.TryRemove(key, out _);
    }

    private static string? Key(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true || !user.IsInRole(AppAuthValidator.AdminRole)) return null;
        var email = user.FindFirst(ClaimTypes.Email)?.Value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(email) ? null : email;
    }
}
