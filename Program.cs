using MapleKiosk.Web.Components;
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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddSingleton<TrialSignupService>();
builder.Services.AddSingleton<EmailService>();

// Central OAuth login (oauth.maplekiosk.ca), exactly like MapleTKT. AuthHostUrl
// + signing key resolve config-free (env → Azure Table Authentication/*) and are
// injected into IConfiguration so AddSPCAuth binds them.
builder.Configuration.AddInMemoryCollection(await AuthConfig.ResolveAsync());
builder.Services.AddSPCAuth(builder.Configuration);
// Access allowlist: only @spc-technology.com + the owner account.
builder.Services.AddScoped<AuthEmailValidator>(_ => AppAuthValidator.ValidateAsync);
// OAuth-only — no-op password validator so /signin/password can't throw.
builder.Services.AddScoped<AuthPasswordValidator>(_ => (_, _) => Task.FromResult<AuthPasswordResult?>(null));

// Checkout API hosted in-app under /api/checkout (extractable to a dedicated
// service later). Config-free via AppStore/* in the keyvalue table / env vars.
builder.Services.AddHttpContextAccessor();
builder.Services.AddAppStore();

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

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAppStoreEndpoints();
app.MapSPCAuthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(MapleShop.UI.Components.ShopWidget).Assembly);

app.Run();
