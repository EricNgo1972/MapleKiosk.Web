using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Onboarding;

public static class OnboardingServiceCollectionExtensions
{
    /// <summary>Customer setup intake after deposit: store, workflow, and the
    /// signed-in file download endpoint. Needs AddAppStore (AppStoreConfig) and
    /// EmailService registered.</summary>
    public static IServiceCollection AddOnboarding(this IServiceCollection services)
    {
        services.AddSingleton<OnboardingStore>();
        services.AddSingleton<OnboardingService>();
        return services;
    }

    /// <summary>GET /onboarding/files/{token}/{fileId} — download of an uploaded file
    /// (setup form, logo or website brief). Staff can open any; a customer only
    /// their own record's files. The id must belong to that token.</summary>
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/onboarding/files/{token}/{fileId}", async (string token, string fileId, HttpContext http, OnboardingStore store, CancellationToken ct) =>
        {
            var record = await store.FindAsync(token, ct);
            if (record is null) return Results.NotFound();
            var user = http.User;
            if (!record.IsOpenTo(user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value, user.IsInRole(AppAuthValidator.AdminRole)))
                return Results.NotFound();
            var file = record.Files.FirstOrDefault(f => f.Id == fileId)
                       ?? (record.Form.Logo?.Id == fileId ? record.Form.Logo : null)
                       ?? (await store.GetBriefAsync(token, ct)).Files.FirstOrDefault(f => f.Id == fileId);
            if (file is null) return Results.NotFound();

            var stream = await store.OpenFileAsync(token, fileId, ct);
            return stream is null ? Results.NotFound() : Results.File(stream, file.ContentType, file.FileName);
        }).RequireAuthorization();

        return app;
    }
}
