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

    /// <summary>GET /onboarding/files/{token}/{fileId} — staff-only (Admin role) download of a
    /// customer's uploaded file (setup form or website brief). The id must belong to that token.</summary>
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/onboarding/files/{token}/{fileId}", async (string token, string fileId, OnboardingStore store, CancellationToken ct) =>
        {
            var record = await store.FindAsync(token, ct);
            if (record is null) return Results.NotFound();
            var file = record.Files.FirstOrDefault(f => f.Id == fileId)
                       ?? (await store.GetBriefAsync(token, ct)).Files.FirstOrDefault(f => f.Id == fileId);
            if (file is null) return Results.NotFound();

            var stream = await store.OpenFileAsync(token, fileId, ct);
            return stream is null ? Results.NotFound() : Results.File(stream, file.ContentType, file.FileName);
        }).RequireAuthorization(p => p.RequireRole(AppAuthValidator.AdminRole));

        return app;
    }
}
