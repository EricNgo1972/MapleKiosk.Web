using MapleKiosk.Web.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The website's voice agent: one raw WebSocket at <c>/assistant/voice/ws</c> carrying microphone audio up
/// and agent audio down, for the hidden admin test page (/voice-test, wwwroot/js/voice-test.js). Adapted from
/// the platform's Infrastructure/VoiceAgent/.../Relay/VoiceAgentEndpoints.cs.
///
/// <para><b>Vendored, not referenced.</b> Everything under Assistant/Voice is copied from the monorepo (each
/// file names its source) so the site stays standalone — a ProjectReference would drag in CSLA, SQLite and the
/// rest. What the copy leaves out, and why:</para>
/// <list type="bullet">
///   <item>The kiosk's single-use session ticket: the page is admin-only, so the socket is gated by the auth
///   cookie on the upgrade (same origin) and the Admin role instead.</item>
///   <item>The voice profile store (CSLA/IKeyValueStore) and CompanySettings: the profile is built per call
///   from <see cref="AssistantSettings.ResolveVoiceAsync"/> (/chats → Settings), the business is "MapleKiosk".</item>
///   <item>The MCP tool broker and the ChatAudit trail: the agent has no tools beyond the relay's own
///   end_call (<see cref="EmptyVoiceToolBroker"/>), so there is nothing to audit.</item>
///   <item>Deepgram and the stub provider: Gemini Live only; an unset key fails with a message naming the setting.</item>
/// </list>
/// <para>The relay and <see cref="IVoiceClientChannel"/> are untouched, which is the seam a Twilio media-stream
/// channel plugs into later without changing the relay.</para>
/// </summary>
public static class WebsiteVoiceEndpoints
{
    public const string Route = "/assistant/voice/ws";

    /// <summary>A cost guard: every open session bills Gemini for as long as it stays open.</summary>
    private const int MaxConcurrentSessions = 2;

    public const string BusinessName = "MapleKiosk";

    public static IServiceCollection AddWebsiteVoice(this IServiceCollection services)
    {
        services.AddSingleton<IVoiceToolBrokerFactory, EmptyVoiceToolBrokerFactory>();
        services.AddSingleton<IVoiceAgentServiceFactory, VoiceAgentServiceFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVoiceAgentProvider, GeminiLiveVoiceAgentProvider>());
        return services;
    }

    public static IEndpointRouteBuilder MapWebsiteVoice(this IEndpointRouteBuilder endpoints)
    {
        // Cast so it binds as a route handler (whose IResult is written), not as a bare RequestDelegate.
        endpoints.Map(Route, (Func<HttpContext, Task<IResult>>)RunAsync)
            .RequireAuthorization(p => p.RequireRole(AppAuthValidator.AdminRole));
        return endpoints;
    }

    private static async Task<IResult> RunAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MapleKiosk.Voice.Relay");

        if (!context.WebSockets.IsWebSocketRequest)
            return Results.BadRequest("This endpoint expects a WebSocket upgrade.");

        // The cookie rides on the upgrade, and browsers do not apply CORS to WebSockets — so a page on
        // another site could otherwise open this socket as the signed-in admin and spend Gemini minutes.
        if (!SameOrigin(context.Request))
        {
            logger.LogWarning("Voice: refused a socket from another origin ({Origin}).", context.Request.Headers.Origin.ToString());
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var requested = context.Request.Query["lang"].ToString();
        var lang = requested is "fr" or "vi" or "ru" ? requested : "en";
        var profile = ProfileFor(await AssistantSettings.ResolveVoiceAsync(), lang);

        if (!VoiceSessionLimiter.TryAcquire(profile.MaxConcurrentSessions))
        {
            logger.LogWarning("Voice: refused a conversation — {Active} already open.", VoiceSessionLimiter.Active);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("Voice: conversation open (website, {Language}).", lang);

            await new VoiceRelay(
                new BrowserVoiceChannel(socket, logger), logger,
                new ChatLogVoiceConversationStore(
                    services.GetRequiredService<ChatLog>(),
                    context.Request.Headers["CF-IPCountry"].FirstOrDefault(),
                    "/voice-test"),
                new MapleKioskVoicePersona(lang),
                new NullVoiceOutcomeReader()).RunAsync(
                services.GetRequiredService<IVoiceAgentServiceFactory>(),
                services.GetRequiredService<IVoiceToolBrokerFactory>(),
                profile,
                VoiceToolAudience.Customer,
                // Nothing known up front; an empty context is desktop voice with kiosk acoustics.
                VoiceSessionContext.Empty,
                lang,
                BusinessName,
                await GroundingAsync(services, logger, context.RequestAborted),
                context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voice: conversation ended with an error.");
        }
        finally
        {
            VoiceSessionLimiter.Release();
            logger.LogInformation("Voice: conversation closed.");
        }

        // The response has already been taken over by the upgrade; nothing further to write.
        return Results.Empty;
    }

    /// <summary>
    /// The profile the vendored provider reads, from what an admin set on /chats → Settings. A blank key is
    /// passed through on purpose: the provider refuses it with a message naming the setting, and the relay
    /// sends that to the page instead of a silently closed socket.
    /// </summary>
    internal static VoiceProfileInfo ProfileFor(VoiceSetup setup, string lang) => new()
    {
        Name = "Website",
        Kind = VoiceKinds.GeminiLive,
        IsActive = true,
        ApiKey = setup.GeminiApiKey,
        Model = setup.Model,
        VoiceName = setup.Voice,
        Language = lang,
        // Blank: the persona greets, in the visitor's language.
        Greeting = "",
        PromptAddendum = MapleKioskVoicePersona.LanguageNote(lang),
        MaxConcurrentSessions = MaxConcurrentSessions,
    };

    /// <summary>
    /// What MapleKiosk sells, for the agent to be held to. Read once per conversation; a read that fails
    /// leaves the agent ungrounded rather than failing to open — the platform's rule.
    /// </summary>
    private static async Task<string?> GroundingAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await services.GetRequiredService<IAssistantGrounding>()
                .ForCustomerAsync(ConversationChannel.DesktopVoice, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Voice: the grounding could not be read; answering without it.");
            return null;
        }
    }

    private static bool SameOrigin(HttpRequest r)
    {
        var origin = r.Headers.Origin.ToString();
        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
               && string.Equals(o.Authority, r.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
