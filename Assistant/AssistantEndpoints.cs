using System.Collections.Concurrent;
using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// The website assistant: <c>POST /assistant/message</c> answers one message from the chat bubble, and
/// <c>GET /assistant/knowledge.md</c> serves the grounding, for the phone agent's prompt.
///
/// <para>The visitor's browser keeps the conversation and sends the recent turns with each message (capped
/// like the gateway's history window); every exchange is also recorded in <see cref="ChatLog"/> for the
/// team (/chats). Same-origin only, and rate-limited per visitor and per day, since every message costs a
/// model call. The one tool, save_guest_contact, keeps the name and contact a guest chooses to give.</para>
/// </summary>
public static class AssistantEndpoints
{
    // As the platform's ConversationOrchestrator: a sliding window of turns, each clipped.
    private const int MaxHistoryMessages = 20;
    private const int MaxTurnChars = 2000;
    private const int MaxReplyTokens = 700;

    // Per visitor: a burst limit and a daily one; and a ceiling for the whole site.
    private const int PerMinute = 8;
    private const int PerDay = 80;
    private const int SitePerDay = 3000;

    public sealed record Turn(string Role, string Text);
    public sealed record MessageRequest(string Text, List<Turn>? History, string? Lang, string? Page, string? ConversationId);
    public sealed record MessageReply(string Reply, string? ConversationId = null);

    public static IServiceCollection AddWebsiteAssistant(this IServiceCollection services)
    {
        services.AddSingleton<AssistantLlm>();
        services.AddSingleton<SiteKnowledge>();
        services.AddSingleton<IAssistantGrounding>(sp => sp.GetRequiredService<SiteKnowledge>());
        services.AddSingleton<VisitorLimiter>();
        services.AddSingleton<ChatLog>();
        services.AddSingleton<GuestContactTool>();
        return services;
    }

    public static IEndpointRouteBuilder MapWebsiteAssistant(this IEndpointRouteBuilder app)
    {
        app.MapPost("/assistant/message", MessageAsync).AllowAnonymous().DisableAntiforgery();

        app.MapGet("/assistant/knowledge.md", async (IAssistantGrounding grounding, CancellationToken ct) =>
            Results.Text(await grounding.ForCustomerAsync(ConversationChannel.Phone, ct) ?? "", "text/markdown; charset=utf-8"))
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> MessageAsync(
        MessageRequest req, HttpContext http, AssistantLlm llm, IAssistantGrounding grounding,
        VisitorLimiter limiter, ChatLog chats, GuestContactTool contactTool, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("Assistant");

        // The bubble on this site, not someone else's page spending our model calls.
        // (A body, so the site's status-code pages don't re-execute the POST against /not-found.)
        if (!SameOrigin(http.Request)) return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);

        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) return Results.Json(new { error = "empty" }, statusCode: StatusCodes.Status400BadRequest);
        if (text.Length > MaxTurnChars) text = text[..MaxTurnChars];

        var lang = req.Lang is "fr" or "vi" or "ru" ? req.Lang : "en";
        var id = ChatLog.IdFor(req.ConversationId);
        if (limiter.OverLimit(Visitor(http), PerMinute, PerDay, SitePerDay))
            return Results.Ok(new MessageReply(Busy(lang), id));

        var page = Path(req.Page);
        var saidAt = DateTimeOffset.UtcNow;
        var history = (req.History ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Text)).TakeLast(MaxHistoryMessages).ToList();
        var conversation = await chats.GetAsync(id, ct);

        string? knowledge = null;
        try { knowledge = await grounding.ForCustomerAsync(ConversationChannel.Chat, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Assistant: the grounding could not be read; answering without it."); }

        var messages = new List<ChatMessage> { new(ChatRole.System, AssistantPrompt.For(knowledge, ConversationChannel.Chat)) };
        foreach (var turn in history)
            messages.Add(new ChatMessage(turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, Clip(turn.Text)));
        // Server-supplied context sits right before the message it explains (as the gateway does): where the
        // visitor is, how far the conversation has gone, and whether they already left a contact — the server's
        // record, so asking for it never rests on the model's memory alone.
        // The demo button is named on screen in the visitor's language, so the reply should name it the same way.
        var demo = Translations.All[lang].TryGetValue("nav.cta", out var cta) ? cta : "Book a demo";
        var asked = history.Count(t => t.Role == "user") + 1;
        messages.Add(new ChatMessage(ChatRole.System,
            $"The visitor is reading the site in {Language(lang)} and is on the page {Path(req.Page)}. " +
            $"On their screen the “Book a demo” button reads “{demo}”. " +
            $"This is their message number {asked} in this conversation. " +
            (conversation?.Contact is { } c
                ? $"They already left their contact ({c.Name}): do not ask for it again."
                : "They have not left a contact yet.")));
        messages.Add(new ChatMessage(ChatRole.User, text));

        string reply;
        try
        {
            var client = await llm.GetAsync();
            var options = new ChatOptions { MaxOutputTokens = MaxReplyTokens };
            if (client is not StubChatClient)
            {
                options.Tools = [contactTool.For(id, lang, page)];
                client = new ChatClientBuilder(client).UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 2).Build();
            }
            var response = await client.GetResponseAsync(messages, options, ct);
            reply = response.Text?.Trim() is { Length: > 0 } r ? r : Unavailable(lang);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Assistant: the model call failed — returning the unavailable message.");
            reply = Unavailable(lang);
        }

        await chats.RecordAsync(id,
            new ChatLine("user", text, saidAt, page),
            new ChatLine("assistant", reply, DateTimeOffset.UtcNow, page),
            lang, http.Request.Headers["CF-IPCountry"].FirstOrDefault(), Device(http.Request), ct);

        return Results.Ok(new MessageReply(reply, id));
    }

    private static string Device(HttpRequest r)
    {
        var ua = r.Headers.UserAgent.ToString();
        return ua.Contains("Mobi", StringComparison.OrdinalIgnoreCase) ? "Phone"
             : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ? "Tablet"
             : "Computer";
    }

    private static string Clip(string text) => text.Length <= MaxTurnChars ? text : text[..MaxTurnChars] + "…";

    private static string Language(string lang) => lang switch { "fr" => "French", "vi" => "Vietnamese", "ru" => "Russian", _ => "English" };

    // Only a site path, never anything else the client might put there.
    private static string Path(string? page) =>
        page is { Length: > 0 and < 200 } p && p.StartsWith('/') && !p.StartsWith("//") ? p : "/";

    private static bool SameOrigin(HttpRequest r)
    {
        var site = r.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0) return site is "same-origin";
        var origin = r.Headers.Origin.ToString();
        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Authority == r.Host.Value);
    }

    // Behind Cloudflare the visitor is CF-Connecting-IP; otherwise the forwarded-headers address.
    private static string Visitor(HttpContext http) =>
        http.Request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } cf
            ? cf
            : http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string Busy(string lang) => lang switch
    {
        "fr" => "Beaucoup de questions en peu de temps ! Réessayez dans une minute, ou écrivez à sales@maplekiosk.ca.",
        "vi" => "Anh chị hỏi hơi nhanh! Vui lòng thử lại sau một phút, hoặc viết tới sales@maplekiosk.ca.",
        "ru" => "Слишком много вопросов подряд! Попробуйте через минуту или напишите на sales@maplekiosk.ca.",
        _ => "That's a lot of questions in a short time! Please try again in a minute, or write to sales@maplekiosk.ca.",
    };

    private static string Unavailable(string lang) => lang switch
    {
        "fr" => "L'assistant n'est pas disponible pour le moment. Écrivez à sales@maplekiosk.ca ou utilisez « Réserver une démo » en haut de la page.",
        "vi" => "Trợ lý tạm thời không hoạt động. Vui lòng viết tới sales@maplekiosk.ca hoặc bấm “Đặt lịch demo” ở đầu trang.",
        "ru" => "Ассистент сейчас недоступен. Напишите на sales@maplekiosk.ca или нажмите «Заказать демо» вверху страницы.",
        _ => StubChatClient.Text,
    };
}

/// <summary>Counts messages per visitor (a minute and a day) and for the whole site (a day), in memory.</summary>
public sealed class VisitorLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _seen = new();
    private readonly object _siteLock = new();
    private DateTime _siteDay = DateTime.UtcNow.Date;
    private int _siteCount;

    public bool OverLimit(string visitor, int perMinute, int perDay, int sitePerDay)
    {
        var now = DateTime.UtcNow;
        lock (_siteLock)
        {
            if (now.Date != _siteDay) { _siteDay = now.Date; _siteCount = 0; _seen.Clear(); }
            if (_siteCount >= sitePerDay) return true;
        }

        var q = _seen.GetOrAdd(visitor, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromDays(1)) q.Dequeue();
            if (q.Count >= perDay || q.Count(t => now - t < TimeSpan.FromMinutes(1)) >= perMinute) return true;
            q.Enqueue(now);
        }

        lock (_siteLock) _siteCount++;
        return false;
    }
}
