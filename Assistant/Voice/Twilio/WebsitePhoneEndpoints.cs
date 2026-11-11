using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// MapleKiosk's own phone line: a Twilio number whose voice webhook points here rings the website's voice
/// agent, over the same relay as /voice-test, through the platform's Twilio media-stream channel. Adapted
/// from the platform's Infrastructure/VoiceAgent/.../Twilio/TwilioVoiceEndpoints.cs, keeping only the inbound
/// call (no transfer to staff, outbound calls, recordings, WhatsApp or SIP gateway).
/// <list type="bullet">
///   <item><c>POST /twilio/voice/incoming</c> — the number's voice webhook. Signature-checked; answers with
///   TwiML that parks the call on the media socket, or an apology when there is no Gemini key or the line is full.</item>
///   <item><c>GET /twilio/voice/stream/{ticket}</c> — the media socket Twilio dials next, gated by a
///   single-use ticket the webhook minted two minutes before at most.</item>
///   <item><c>POST /twilio/voice/status</c> — call-status callback, logged. Signature-checked.</item>
///   <item><c>POST /twilio/voice/fallback</c> — the backup webhook: a fixed apology, no dependencies.</item>
/// </list>
/// The number is wired by hand in the Twilio console (steps on /admin/assistant); see <see cref="PhoneHookup"/>.
/// </summary>
public static class WebsitePhoneEndpoints
{
    private static readonly (string Text, string? LanguageTag)[] Unavailable =
    [
        // French first: MapleKiosk is in Quebec.
        ("Désolé, nous ne pouvons pas prendre votre appel pour le moment. "
         + "Écrivez-nous à sales arobase maplekiosk point C A.", "fr-CA"),
        ("Sorry, we can't take your call right now. Please write to sales at maplekiosk dot C A, "
         + "or book a demo on maplekiosk dot C A.", "en-US"),
    ];

    /// <summary>Tickets the webhook minted, redeemable once by the media socket.</summary>
    private static readonly ConcurrentDictionary<string, PhoneTicket> Tickets = new();
    private static readonly TimeSpan TicketLife = TimeSpan.FromMinutes(2);

    private sealed record PhoneTicket(string? Caller, string CallSid, DateTime IssuedUtc, string? Line = null);

    public static IEndpointRouteBuilder MapWebsitePhone(this IEndpointRouteBuilder endpoints)
    {
        // Cast on purpose: a (HttpContext) → Task<IResult> lambda otherwise binds as a RequestDelegate, which
        // throws the IResult away — Twilio would get an empty 200 and dead air.
        endpoints.MapPost(PhoneHookup.IncomingRoute, (Delegate)IncomingAsync).AllowAnonymous().DisableAntiforgery();
        endpoints.MapPost(PhoneHookup.StatusRoute, (Delegate)StatusAsync).AllowAnonymous().DisableAntiforgery();
        endpoints.MapPost(PhoneHookup.FallbackRoute, () => Twiml(TwilioTwiml.SayAndHangUp(Unavailable)))
            .AllowAnonymous().DisableAntiforgery();
        endpoints.Map($"{PhoneHookup.StreamRoute}/{{ticket}}", (Func<HttpContext, string, Task<IResult>>)StreamAsync)
            .AllowAnonymous();
        // Texts to the same number: answered by the chat assistant (WebsiteSmsEndpoints.cs).
        endpoints.MapWebsiteSms();
        return endpoints;
    }

    // ── The webhook ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> IncomingAsync(HttpContext context)
    {
        var logger = Logger(context.RequestServices);

        var verdict = await VerifySignatureAsync(context, logger);
        if (verdict is not null) return verdict;

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var callSid = form["CallSid"].ToString();
        var to = form["To"].ToString();

        if (string.IsNullOrWhiteSpace((await AssistantSettings.ResolveVoiceAsync()).GeminiApiKey))
        {
            logger.LogError("Voice/phone: call {CallSid} refused — no Gemini API key (/admin/assistant).", callSid);
            return Twiml(TwilioTwiml.SayAndHangUp(Unavailable));
        }
        // Checked here as well as at the socket so a full house is told so in words, not connected then dropped.
        if (VoiceSessionLimiter.Active >= WebsiteVoiceEndpoints.MaxConcurrentSessions)
        {
            logger.LogWarning("Voice/phone: call {CallSid} refused — {Active} conversation(s) already open.",
                callSid, VoiceSessionLimiter.Active);
            return Twiml(TwilioTwiml.SayAndHangUp(Unavailable));
        }

        try { await PhoneHookup.LearnLineAsync(to); }
        catch (Exception ex) { logger.LogWarning(ex, "Voice/phone: could not remember the line number."); }

        // Lisa's own call coming back: she put a caller through (or rang about a chat guest) from this line, and the
        // staff phone forwards to this line. Answering would put the caller back on Lisa; refusing makes the transfer
        // fail at once, so the caller hears "nobody is available, we'll call you back".
        if (IsOwnCall(form["From"].ToString(), to, await PhoneHookup.LineNumberAsync()))
        {
            logger.LogInformation("Voice/phone: call {CallSid} is Lisa's own call forwarded back (the staff phone forwards here) — refused.", callSid);
            return Twiml(TwilioTwiml.Reject);
        }

        var caller = GuestNumber(form["From"].ToString(), to);
        var ticket = Mint(caller, callSid, to.StartsWith('+') ? to.Trim() : null);
        var request = context.Request;
        var streamUrl = $"{(request.IsHttps ? "wss" : "ws")}://{request.Host}{request.PathBase}{PhoneHookup.StreamRoute}/{ticket}";

        logger.LogInformation("Voice/phone: call {CallSid} from {From} → the website assistant.", callSid, caller ?? "withheld");
        return Twiml(TwilioTwiml.ConnectStream(streamUrl));
    }

    private static async Task<IResult> StatusAsync(HttpContext context)
    {
        var logger = Logger(context.RequestServices);

        var verdict = await VerifySignatureAsync(context, logger);
        if (verdict is not null) return verdict;

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        logger.LogInformation("Voice/phone: call {CallSid} is {Status}{Duration}.",
            form["CallSid"].ToString(), form["CallStatus"].ToString(),
            form.TryGetValue("CallDuration", out var d) && !string.IsNullOrEmpty(d) ? $" after {d}s" : "");
        return Twiml(TwilioTwiml.Empty);
    }

    // ── The socket ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> StreamAsync(HttpContext context, string ticket)
    {
        var services = context.RequestServices;
        var logger = Logger(services);

        if (!context.WebSockets.IsWebSocketRequest)
            return Results.BadRequest("This endpoint expects a WebSocket upgrade from Twilio Media Streams.");

        if (Redeem(ticket) is not { } call)
        {
            logger.LogWarning("Voice/phone: rejected a stream with an invalid, expired or already-used ticket.");
            return Results.Unauthorized();
        }

        var language = await PhoneHookup.LanguageAsync();
        var profile = PhoneProfile(await AssistantSettings.ResolveVoiceAsync(), language);

        if (!VoiceSessionLimiter.TryAcquire(profile.MaxConcurrentSessions))
        {
            logger.LogWarning("Voice/phone: refused a call — {Active} already open.", VoiceSessionLimiter.Active);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            var channel = await TwilioMediaStreamChannel.OpenAsync(socket, logger, context.RequestAborted);
            if (channel is null) return Results.Empty;

            logger.LogInformation("Voice/phone: call {CallSid} connected ({Language}).", call.CallSid, language);

            var conversationId = Guid.NewGuid().ToString("N");
            var sessionContext = VoiceSessionContext.From(new Dictionary<string, string?>
            {
                [VoiceSessionContext.SessionIdKey] = conversationId,
                [WebsiteVoiceToolBroker.LangKey] = language,
                [WebsiteVoiceToolBroker.LineKey] = call.Line,
                [VoiceSessionContext.ChannelKey] = VoiceSessionContext.PhoneChannel,
                [VoiceSessionContext.PhoneKey] = call.Caller,
                [VoiceSessionContext.CallSidKey] = call.CallSid,
            });

            await new VoiceRelay(
                channel, logger,
                new ChatLogVoiceConversationStore(
                    services.GetRequiredService<ChatLog>(), null, "phone",
                    ChatLogVoiceConversationStore.PhoneDevice, call.Caller, conversationId,
                    services.GetRequiredService<ConversationSummarizer>()),
                new MapleKioskVoicePersona(language, phone: true, greeting: await PhoneHookup.GreetingAsync()),
                new NullVoiceOutcomeReader()).RunAsync(
                services.GetRequiredService<IVoiceAgentServiceFactory>(),
                services.GetRequiredService<IVoiceToolBrokerFactory>(),
                profile,
                VoiceToolAudience.Customer,
                sessionContext,
                language,
                WebsiteVoiceEndpoints.BusinessName,
                await WebsiteVoiceEndpoints.GroundingAsync(services, logger, ConversationChannel.Phone, context.RequestAborted),
                context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voice/phone: call ended with an error.");
        }
        finally
        {
            VoiceSessionLimiter.Release();
            logger.LogInformation("Voice/phone: call closed.");
        }

        return Results.Empty;
    }

    internal static VoiceProfileInfo PhoneProfile(VoiceSetup setup, string lang) => new()
    {
        Name = "Phone",
        Kind = VoiceKinds.GeminiLive,
        IsActive = true,
        ApiKey = setup.GeminiApiKey,
        Model = setup.Model,
        VoiceName = setup.Voice,
        Language = lang,
        Greeting = "",
        PromptAddendum = MapleKioskVoicePersona.PhoneLanguageNote(lang),
        MaxConcurrentSessions = WebsiteVoiceEndpoints.MaxConcurrentSessions,
    };

    // ── Tickets ─────────────────────────────────────────────────────────────────────────────────────

    private static string Mint(string? caller, string callSid, string? line)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, old) in Tickets)
            if (now - old.IssuedUtc > TicketLife) Tickets.TryRemove(key, out _);

        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Tickets[ticket] = new PhoneTicket(caller, callSid, now, line);
        return ticket;
    }

    private static PhoneTicket? Redeem(string ticket) =>
        Tickets.TryRemove(ticket ?? "", out var t) && DateTime.UtcNow - t.IssuedUtc <= TicketLife ? t : null;

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Null when the request is genuinely Twilio's; otherwise the response to send instead. Refuses closed
    /// when no auth token is configured.
    /// </summary>
    internal static async Task<IResult?> VerifySignatureAsync(HttpContext context, ILogger logger)
    {
        if (await PhoneHookup.CredentialsAsync() is not { } creds)
        {
            logger.LogError("Voice/phone: refused a Twilio webhook — no Twilio/AuthToken in the keyvalue table.");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(context.RequestAborted)
            : null;
        var fields = form is null
            ? Enumerable.Empty<KeyValuePair<string, string>>()
            : form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString()));

        var r = context.Request;
        var url = $"{r.Scheme}://{r.Host}{r.PathBase}{r.Path}{r.QueryString}";
        if (TwilioSignature.IsValid(creds.Token, url, fields, r.Headers[TwilioSignature.Header].ToString()))
            return null;

        // The URL is the usual culprit — a proxy that rewrote the host or scheme — so say which one was checked.
        logger.LogWarning("Voice/phone: bad or missing Twilio signature for {Url}.", url);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>The caller's number in E.164, or null when withheld — or when it is the line's own number
    /// (a forward that dropped the original caller id says nothing about who is on it).</summary>
    /// <summary>A call whose caller ID is this line itself: one of Lisa's own outgoing calls, forwarded back.</summary>
    internal static bool IsOwnCall(string? from, string? to, string? line)
    {
        var caller = PhoneHookup.Digits(from);
        return caller.Length >= 8 && (caller == PhoneHookup.Digits(to) || caller == PhoneHookup.Digits(line));
    }

    internal static string? GuestNumber(string? from, string? to)
    {
        var caller = !string.IsNullOrWhiteSpace(from) && from.StartsWith('+') && from.Length > 3 ? from.Trim() : null;
        if (caller is null) return null;
        return PhoneHookup.Digits(caller) == PhoneHookup.Digits(to) ? null : caller;
    }

    private static IResult Twiml(string body) => Results.Content(body, TwilioTwiml.ContentType);

    private static ILogger Logger(IServiceProvider services) =>
        services.GetRequiredService<ILoggerFactory>().CreateLogger("MapleKiosk.Voice.Phone");
}
