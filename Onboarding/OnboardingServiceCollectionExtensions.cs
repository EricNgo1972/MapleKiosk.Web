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
        services.AddSingleton<DesignerPackage>();
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

        // GET /onboarding/admin/{token}/designer-package — staff only: the customer's answers and
        // files as one zip for the web designer (read-only; see DesignerPackage).
        app.MapGet("/onboarding/admin/{token}/designer-package", async (string token, OnboardingStore store,
            DesignerPackage package, CancellationToken ct) =>
        {
            var record = await store.FindAsync(token, ct);
            if (record is null) return Results.NotFound();

            // Built in a temp file (ZipArchive writes synchronously), deleted once sent.
            var temp = new FileStream(Path.Combine(Path.GetTempPath(), $"mk-designer-{Guid.NewGuid():N}.zip"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await package.WriteAsync(record, temp, ct);
                temp.Position = 0;
                return Results.File(temp, "application/zip", DesignerPackage.ZipName(record));
            }
            catch
            {
                await temp.DisposeAsync();
                throw;
            }
        }).RequireAuthorization(p => p.RequireRole(AppAuthValidator.AdminRole));

        return app;
    }
}
