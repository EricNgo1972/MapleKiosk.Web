using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SPC.Infrastructure.Auth;

/// <summary>
/// Resolves whether the email returned by the auth host may sign in, and
/// supplies any tenant-specific claims (role, etc.) to attach to the
/// principal. Returns null when sign-in is denied; returns a (possibly empty)
/// list of extra claims when allowed. Registered as a scoped DI service by
/// the host (lambda in Program.cs) so the auth library doesn't need to know
/// how the allowlist is sourced — Staff table, config, AD, etc.
/// </summary>
public delegate Task<IReadOnlyList<Claim>?> AuthEmailValidator(string email);

/// <summary>
/// Validates an email + plaintext password pair and returns the principal's
/// claims. Mirrors <see cref="AuthEmailValidator"/> for the local
/// username/password sign-in path. Returns null when credentials are
/// invalid or the email isn't on the allowlist; returns a (possibly empty)
/// list of extra claims when allowed (role, owner, etc.).
/// </summary>
public delegate Task<AuthPasswordResult?> AuthPasswordValidator(string email, string password);

/// <summary>
/// Returns true when <paramref name="newPin"/> is not currently held by any
/// other active identity (Owner or Staff). The /signin page resolves the
/// user from the PIN alone; without a uniqueness check at PIN-set time,
/// two users sharing a PIN would silently lock the second one out.
/// </summary>
/// <param name="excludeStaffId">Staff being edited — its existing hash is
/// skipped so the operator can re-save the same PIN without the check
/// reporting a self-collision. Pass null when checking on behalf of a
/// non-Staff identity (e.g. the Owner at /activate).</param>
public delegate Task<bool> PinAvailabilityChecker(string newPin, string? excludeStaffId);

/// <summary>Successful password validation result, used to populate the
/// auth cookie. Email and Name come from the staff record (canonical).</summary>
public sealed record AuthPasswordResult(string Email, string Name, IReadOnlyList<Claim> ExtraClaims);

public static class AuthEndpoints
{
    /// <summary>
    /// Properties applied to every successful sign-in so the cookie survives
    /// browser restarts and the auth ticket slides forward on each request.
    /// <see cref="AuthenticationProperties.IsPersistent"/> is what tells the
    /// browser to write the cookie to disk (with a <c>Max-Age</c> header)
    /// instead of treating it as a session cookie that dies on window close.
    /// <see cref="AuthenticationProperties.AllowRefresh"/> opts in to the
    /// sliding-expiration refresh that <c>CookieAuthenticationOptions</c>
    /// already enables.
    /// </summary>
    private static AuthenticationProperties PersistentSessionProperties => new()
    {
        IsPersistent = true,
        AllowRefresh = true,
    };

    public static IEndpointRouteBuilder MapSPCAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant entry point for the central-auth-host flow.
        // Forwards the user to the auth host; the host bounces back to
        // /auth/callback with a signed JWT. Query-style (?provider=google|apple)
        // for legacy providers.
        endpoints.MapGet("/login", (HttpContext ctx, string? returnUrl, string? provider) =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;
            if (string.IsNullOrWhiteSpace(opts.AuthHostUrl))
                return Results.Problem("Authentication:AuthHostUrl is not configured.");

            var thisOrigin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var safeReturn = SafeReturn(returnUrl);
            var tenantCallback = $"{thisOrigin}/auth/callback?returnUrl={Uri.EscapeDataString(safeReturn)}";

            var authHost = opts.AuthHostUrl.TrimEnd('/');
            var providerQs = string.IsNullOrWhiteSpace(provider) ? "" : $"&provider={Uri.EscapeDataString(provider)}";
            return Results.Redirect($"{authHost}/login?tenantReturn={Uri.EscapeDataString(tenantCallback)}{providerQs}");
        }).AllowAnonymous();

        // Path-style entry point: /login/microsoft (and any future provider
        // the auth host exposes under /login/{provider}/). The auth host
        // mounts Microsoft at a dedicated path rather than the shared
        // ?provider= query, so the tenant forwarder mirrors that shape.
        // Provider segment is whitelisted to [a-z0-9-]{1,32} so it can't be
        // weaponized to redirect off-host.
        endpoints.MapGet("/login/{provider}", (HttpContext ctx, string provider, string? returnUrl) =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;
            if (string.IsNullOrWhiteSpace(opts.AuthHostUrl))
                return Results.Problem("Authentication:AuthHostUrl is not configured.");

            if (!IsAllowedProviderSlug(provider))
                return Results.BadRequest("Unsupported provider.");

            var thisOrigin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var safeReturn = SafeReturn(returnUrl);
            var tenantCallback = $"{thisOrigin}/auth/callback?returnUrl={Uri.EscapeDataString(safeReturn)}";

            var authHost = opts.AuthHostUrl.TrimEnd('/');
            return Results.Redirect($"{authHost}/login/{provider}/?tenantReturn={Uri.EscapeDataString(tenantCallback)}");
        }).AllowAnonymous();

        // Auth host bounces back here with a signed token. Verify, allowlist,
        // sign in.
        endpoints.MapGet("/auth/callback", async (HttpContext ctx, string? token, string? returnUrl) =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;

            var (ok, email, name, picture) = await TokenSigner.TryVerifyAsync(token ?? "", opts.TokenSigningKey);
            if (!ok) return Results.Redirect("/access-denied");

            var validate = ctx.RequestServices.GetRequiredService<AuthEmailValidator>();
            var extraClaims = await validate(email);
            // Pass the denied email as a hint so the access-denied page can
            // show the operator exactly which account was rejected — they
            // can then forward that to their admin to be allowlisted.
            // Apps that don't read the query string just ignore it.
            if (extraClaims is null) return Results.Redirect($"/access-denied?email={Uri.EscapeDataString(email ?? "")}");

            var claims = new List<Claim>
            {
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, name ?? email),
            };
            if (!string.IsNullOrWhiteSpace(picture))
                claims.Add(new Claim("picture", picture));
            claims.AddRange(extraClaims);

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                PersistentSessionProperties);

            return Results.Redirect(SafeReturn(returnUrl));
        }).AllowAnonymous();

        // Local PIN sign-in. The /signin Blazor page POSTs here.
        // Two shapes accepted:
        //   • email + password (legacy / Solo single-operator path)
        //   • password only    (Main head — validator resolves identity from PIN)
        // Lockout key: canonical email when supplied; client IP otherwise.
        endpoints.MapPost("/signin/password", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();
            var canonical = email.Trim().ToLowerInvariant();
            var pinOnly = string.IsNullOrEmpty(canonical);

            var tracker = ctx.RequestServices.GetRequiredService<LoginAttemptTracker>();

            // PIN-only flows have no identity until after a successful match,
            // so failures are attributed to the client IP. Falls back to a
            // shared key when the IP isn't resolvable so the kiosk still
            // gets some throttling rather than zero.
            var lockoutKey = pinOnly
                ? "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown")
                : canonical;

            string DeniedRedirect(string reason)
            {
                var url = $"/signin?returnUrl={Uri.EscapeDataString(SafeReturn(returnUrl))}&error={reason}";
                if (!pinOnly) url += $"&for={Uri.EscapeDataString(canonical)}";
                return url;
            }

            if (string.IsNullOrWhiteSpace(password))
                return Results.Redirect(DeniedRedirect("invalid"));

            if (tracker.IsLocked(lockoutKey))
                return Results.Redirect(DeniedRedirect("locked"));

            var validate = ctx.RequestServices.GetRequiredService<AuthPasswordValidator>();
            var result = await validate(email, password);
            if (result is null)
            {
                // PIN-only failures aren't attributed inside the validator
                // (no identity known) — record here so the IP-keyed counter
                // still fires the lockout. Email-keyed failures were already
                // recorded inside the validator.
                if (pinOnly) tracker.RecordFailure(lockoutKey);

                var reason = tracker.IsLocked(lockoutKey) ? "locked" : "invalid";
                return Results.Redirect(DeniedRedirect(reason));
            }

            // Successful PIN-only sign-in — clear the IP-keyed counter so a
            // few wrong tries before the right one don't compound.
            if (pinOnly) tracker.RecordSuccess(lockoutKey);

            var claims = new List<Claim>
            {
                new(ClaimTypes.Email, result.Email),
                new(ClaimTypes.Name, string.IsNullOrWhiteSpace(result.Name) ? result.Email : result.Name),
            };
            claims.AddRange(result.ExtraClaims);

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                PersistentSessionProperties);

            return Results.Redirect(SafeReturn(returnUrl));
        }).AllowAnonymous().DisableAntiforgery();

        endpoints.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();

        return endpoints;
    }

    private static string SafeReturn(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        return returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";
    }

    private static bool IsAllowedProviderSlug(string? provider)
    {
        if (string.IsNullOrEmpty(provider) || provider.Length > 32) return false;
        foreach (var c in provider)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok) return false;
        }
        return true;
    }
}
