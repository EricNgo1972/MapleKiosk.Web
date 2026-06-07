using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;       // CookieSecurePolicy, SameSiteMode
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SPC.Infrastructure.Auth;

public static class DependencyInjection
{
    public static IServiceCollection AddSPCAuth(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AuthOptions>(opts =>
        {
            config.GetSection(AuthOptions.SectionName).Bind(opts);
            // TokenSigningKey resolves through an IKeyVault-style cascade:
            // Env -> IConfiguration -> embedded SecretVault. See resolver doc.
            opts.TokenSigningKey = TokenSigningKeyResolver.Resolve(config);
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.LoginPath = "/signin";
                o.AccessDeniedPath = "/access-denied";
                // 30-day sliding window matches the "open and you're in" feel
                // of Gmail / Google Workspace / Microsoft 365. Each authenticated
                // request slides the expiry 30 days forward, so an active user
                // is effectively never signed out; an idle user past 30 days
                // is. Pairs with IsPersistent=true at SignInAsync sites (see
                // AuthEndpoints.PersistentSessionProperties) so the browser
                // writes a real cookie instead of a session-only cookie that
                // dies on window close.
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;

                // ── Cookie settings (the bits that decide whether the browser
                //    actually keeps the cookie across visits) ───────────────
                //
                // Name pinned — without an explicit name the framework uses
                // a name derived from the scheme + assembly, which can shift
                // between runs (it includes ".AspNetCore." plus a hash) and
                // leaves orphan cookies in the browser jar that quietly fail
                // to authenticate.
                o.Cookie.Name = "MapleTKT.Auth";
                // Max-Age is what tells the browser "store me to disk until
                // this date" — IsPersistent on the auth properties asks for
                // it, but pinning MaxAge here makes it deterministic and
                // explicit even on the local PIN-signin path.
                o.Cookie.MaxAge = TimeSpan.FromDays(30);
                // SameAsRequest (default) sets Secure=true only when the
                // current request was HTTPS — and Kestrel sees HTTP in dev,
                // which makes the Secure attribute flip per environment and
                // the browser drop the cookie when the scheme changes (e.g.
                // dev http://localhost → prod https:// over Cloudflare). We
                // could pin Secure=Always, but then dev over plain HTTP
                // (http://localhost:5125) wouldn't get the cookie at all.
                // Sticking with SameAsRequest gives the simplest mental model:
                // cookie matches the scheme it was issued on.
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                // Lax is required, not Strict — the OAuth callback is a
                // top-level GET coming from a different origin
                // (oauth.maplekiosk.ca). Strict would block the cookie from
                // being set on that redirect.
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.HttpOnly = true;
                o.Cookie.IsEssential = true;   // bypass cookie-consent gates
            });

        // Page-level authorization is enforced by AuthorizeRouteView reading
        // [Authorize] attributes — applied at the project _Imports.razor level
        // so all routable pages default to authed unless they opt out with
        // [AllowAnonymous]. We deliberately do NOT set a FallbackPolicy here:
        // doing so would gate Blazor's own SignalR hub at the endpoint level
        // and prevent anonymous pages (e.g. /signin) from establishing their
        // interactive circuit.
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, OwnerAuthorizationHandler>();
        services.AddSingleton<LoginAttemptTracker>();
        services.AddCascadingAuthenticationState();

        return services;
    }
}
