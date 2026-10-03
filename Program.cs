using MapleKiosk.Web.Components;
using MapleKiosk.Web.Onboarding;
using MapleKiosk.Web.Services;
using MapleKiosk.Web.Shop;
using MapleShop.UI;
using Microsoft.AspNetCore.HttpOverrides;
using SPC.Infrastructure.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();

// Behind cloudflared (TLS terminated at the edge, forwarded to localhost:5500).
// Honor X-Forwarded-* so the app sees the real scheme/host (https://web.maplekiosk.ca)
// — required for correct NavigationManager.BaseUri, Stripe redirect URLs and the
// OAuth callback tenant origin. The tunnel is the only thing that reaches the app,
// so trust the forwarded headers from any immediate peer.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                       | ForwardedHeaders.XForwardedProto
                       | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// /health: the readiness probe mk-provisioning polls when it runs this site as a container.
builder.Services.AddHealthChecks();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddSingleton<DemoLinkStore>();
builder.Services.AddSingleton<TrialSignupService>();
builder.Services.AddSingleton<EmailService>();

// Azure-Table-backed "MapleKiosk family" apps showcase (one card per product).
// Uses STORAGE_CONNECTION_STRING; seeds/falls back to FamilyAppCatalog.DefaultApps().
builder.Services.AddSingleton<FamilyAppCatalog>();

// Central OAuth login (oauth.maplekiosk.ca), exactly like MapleTKT. AuthHostUrl
// + signing key resolve config-free (env → Azure Table Authentication/*) and are
// injected into IConfiguration so AddSPCAuth binds them.
builder.Configuration.AddInMemoryCollection(await AuthConfig.ResolveAsync());
builder.Services.AddSPCAuth(builder.Configuration);
// Access allowlist: team (@spc-technology.com + owner) as Admin, plus customer
// emails on the onboarding list as Customer (their setup page only).
builder.Services.AddScoped<AuthEmailValidator>(sp =>
{
    var onboarding = sp.GetRequiredService<OnboardingStore>();
    return email => AppAuthValidator.ValidateAsync(email, onboarding);
});
// OAuth-only — no-op password validator so /signin/password can't throw.
builder.Services.AddScoped<AuthPasswordValidator>(_ => (_, _) => Task.FromResult<AuthPasswordResult?>(null));

// Checkout API hosted in-app under /api/checkout (extractable to a dedicated
// service later). Config-free via AppStore/* in the keyvalue table / env vars.
builder.Services.AddHttpContextAccessor();
builder.Services.AddAppStore();

// Post-deposit setup intake: /onboarding/{token} (customer) + /onboarding/admin.
builder.Services.AddOnboarding();

// Store UI (cart + checkout widget). It calls /api/checkout — same origin by
// default, so BackendBaseUrl is left empty; the API key (if configured) is still
// sent so the in-app endpoints accept it.
var (shopBackendUrl, shopApiKey) = await ShopConfig.ResolveAsync();
builder.Services.AddMapleShopUi(o =>
{
    o.BackendBaseUrl = shopBackendUrl;
    o.ApiKey = shopApiKey;
});

var app = builder.Build();

// Must run before anything that reads scheme/host (auth, redirects, BaseUri).
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found");

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Marketing pages: let a signed-out visitor's browser keep them for 2 minutes, so site.js's
// hover prefetch makes the next click (language switch, nav) instant. Private (never shared
// caches) and Vary: Cookie (signing in bypasses it); app pages are never cached.
string[] uncachedPrefixes = ["/onboarding", "/shop", "/signin", "/access-denied", "/api", "/auth", "/login", "/logout", "/media", "/health", "/_blazor", "/_framework"];
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsGet(ctx.Request.Method)
        && ctx.User.Identity?.IsAuthenticated != true
        && !uncachedPrefixes.Any(p => ctx.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
    {
        ctx.Response.OnStarting(() =>
        {
            var r = ctx.Response;
            // Overrides antiforgery's no-store (the demo form on every page): the cached page goes
            // back to the same browser, whose antiforgery cookie its token was issued for.
            if (r.StatusCode == 200 && r.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                r.Headers.Pragma = default;
                r.Headers.CacheControl = "private, max-age=120";
                r.Headers.Vary = "Cookie";
            }
            return Task.CompletedTask;
        });
    }
    await next();
});
app.UseAntiforgery();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapSiteMedia();
app.MapAppStoreEndpoints();
app.MapOnboardingEndpoints();
app.MapSPCAuthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(MapleShop.UI.Components.ShopWidget).Assembly);

app.Run();
