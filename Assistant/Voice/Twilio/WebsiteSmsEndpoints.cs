using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// SMS on the same Twilio number as the phone line: a text to the number is answered by the website's chat
/// assistant (same model, knowledge and tools as the chat bubble), and the reply goes back by SMS.
/// <list type="bullet">
///   <item><c>POST /twilio/sms/incoming</c> — the number's "A message comes in" webhook. Signature-checked.
///   Answers Twilio at once with empty TwiML, then works out the reply in the background and sends it with
///   Twilio's Messages API — a model call (with tools) can outlast Twilio's 15-second webhook timeout.</item>
/// </list>
/// One conversation per sender's number on /chats ("SMS"), so the assistant remembers the thread. Twilio
/// itself handles STOP / START / HELP on the number; those words are not answered here.
/// </summary>
public static class WebsiteSmsEndpoints
{
    public const string IncomingRoute = "/twilio/sms/incoming";
    public static string IncomingUrl => SiteHost.Root.TrimEnd('/') + IncomingRoute;

    public const string Device = "SMS";
    private const string Page = "sms";

    private const int MaxHistoryMessages = 20;
    private const int MaxInChars = 1600;
    private const int MaxReplyChars = 900;   // ~6 SMS segments; the prompt asks for far less.
    private const int MaxReplyTokens = 400;

    // Per sender, like the chat bubble's visitor limits.
    private const int PerMinute = 6;
    private const int PerDay = 60;
    private const int SitePerDay = 3000;

    private static readonly HashSet<string> CarrierKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT", "ARRET", "ARRÊT",
        "START", "UNSTOP", "YES", "HELP", "INFO", "AIDE",
    };

    /// <summary>One reply at a time per sender, so two quick texts are answered in order with the thread intact.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Senders = new();

    public static IEndpointRouteBuilder MapWebsiteSms(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(IncomingRoute, (Delegate)IncomingAsync).AllowAnonymous().DisableAntiforgery();
        return endpoints;
    }

    private static async Task<IResult> IncomingAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MapleKiosk.Sms");

        var verdict = await WebsitePhoneEndpoints.VerifySignatureAsync(context, logger);
        if (verdict is not null) return verdict;

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var from = form["From"].ToString().Trim();
        var to = form["To"].ToString().Trim();
        var body = form["Body"].ToString().Trim();
        var sid = form["MessageSid"].ToString();

        // Every text in goes to the SMS inbox (/admin/sms), keywords included.
        if (from.StartsWith('+') && to.StartsWith('+') && body.Length > 0 && SmsStore.Current is { } log)
            await log.AddAsync(true, from, to, body, SmsKind.Guest);

        if (!from.StartsWith('+') || !to.StartsWith('+') || body.Length == 0 || CarrierKeywords.Contains(body))
        {
            logger.LogInformation("Sms: {Sid} from {From} not answered (empty, a keyword, or no number).", sid, from);
            return Empty();
        }

        // Singletons, resolved now: the request scope is gone by the time the background work runs.
        try { await PhoneHookup.LearnLineAsync(to); }
        catch (Exception ex) { logger.LogWarning(ex, "Sms: could not remember the line number."); }

        var work = new SmsWork(
            services.GetRequiredService<AssistantLlm>(),
            services.GetRequiredService<IAssistantGrounding>(),
            services.GetRequiredService<VisitorLimiter>(),
            services.GetRequiredService<ChatLog>(),
            services.GetRequiredService<GuestContactTool>(),
            services.GetRequiredService<CustomerMessageTool>(),
            services.GetRequiredService<DemoRequestTool>(),
            services.GetRequiredService<OrderTool>(),
            services.GetRequiredService<PersonCallTool>(),
            services.GetRequiredService<ConversationSummarizer>(),
            logger);
        var stopping = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        logger.LogInformation("Sms: {Sid} from {From} → the website assistant.", sid, from);
        _ = Task.Run(() => work.AnswerAsync(from, to, body.Length > MaxInChars ? body[..MaxInChars] : body, stopping));
        return Empty();
    }

    private sealed record SmsWork(
        AssistantLlm Llm, IAssistantGrounding Grounding, VisitorLimiter Limiter, ChatLog Chats,
        GuestContactTool ContactTool, CustomerMessageTool MessageTool, DemoRequestTool DemoTool,
        OrderTool OrderTool, PersonCallTool PersonTool, ConversationSummarizer Summarizer, ILogger Logger)
    {
        public async Task AnswerAsync(string from, string to, string text, CancellationToken ct)
        {
            var gate = Senders.GetOrAdd(from, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (Limiter.OverLimit("sms:" + from, PerMinute, PerDay, SitePerDay))
                {
                    Logger.LogWarning("Sms: {From} is over the message limit; not answered.", from);
                    return;
                }

                var id = ConversationId(from);
                var saidAt = DateTimeOffset.UtcNow;
                var reply = await ReplyAsync(id, from, to, text, ct);

                await Chats.RecordAsync(id,
                    new ChatLine("user", text, saidAt, Page),
                    new ChatLine("assistant", reply, DateTimeOffset.UtcNow, Page),
                    MapleKioskVoicePersona.Bilingual, null, Device, ct);
                if ((await Chats.GetAsync(id, ct))?.Contact is null)
                    await Chats.SaveContactAsync(id, new GuestContact("SMS sender", from, null, null, null), ct);

                await SendAsync(to, from, reply, Logger, ct);
                Summarizer.Queue(id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Logger.LogError(ex, "Sms: answering {From} failed.", from);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<string> ReplyAsync(string id, string from, string to, string text, CancellationToken ct)
        {
            string? knowledge = null;
            try { knowledge = await Grounding.ForCustomerAsync(ConversationChannel.Phone, ct); }
            catch (Exception ex) { Logger.LogWarning(ex, "Sms: the grounding could not be read; answering without it."); }

            var history = (await Chats.MessagesAsync(id, ct)).TakeLast(MaxHistoryMessages).ToList();
            var conversation = await Chats.GetAsync(id, ct);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, AssistantPrompt.For(knowledge, ConversationChannel.Phone, await AssistantSettings.AgentNameAsync()) + "\n\n" + SmsNote),
            };
            foreach (var line in history)
                messages.Add(new ChatMessage(line.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, line.Text));
            messages.Add(new ChatMessage(ChatRole.System,
                $"They are texting from {from}: that is their phone number — for take_message, book_demo and place_order use it unless they give another. "
                + (history.Count == 0 ? "This is their first text. " : "")
                + (conversation?.Contact is { Name: not "SMS sender" } c
                    ? $"They already left their contact ({c.Name}): do not ask for it again."
                    : "They have not told you their name yet.") + " " + LocalClock.Note()));
            messages.Add(new ChatMessage(ChatRole.User, text));

            try
            {
                var client = await Llm.GetAsync();
                var options = new ChatOptions { MaxOutputTokens = MaxReplyTokens };
                if (client is not StubChatClient)
                {
                    // A tool still working after a second and a half: tell them to hold on, by text, once.
                    var held = new Func<Task>(() => SendAsync(to, from, HoldText, Logger, ct, SmsKind.Hold));
                    AIFunction Hold(AIFunction f) => new HoldingFunction(f, held, HoldAfter);
                    options.Tools = [ContactTool.For(id, "fr", Page), Hold(MessageTool.For(id, "fr", Page)), Hold(DemoTool.For(id, "qc", "SMS", from)),
                        Hold(OrderTool.For(id, "qc", OrderTool.Channel.Sms, from, to)), Hold(PersonTool.For(id, OrderTool.Channel.Sms, from, to))];
                    client = new ChatClientBuilder(client).UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 2).Build();
                }
                var response = await client.GetResponseAsync(messages, options, ct);
                return response.Text?.Trim() is { Length: > 0 } r ? Plain(r) : Unavailable;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Logger.LogWarning(ex, "Sms: the model call failed — sending the unavailable message.");
                return Unavailable;
            }
        }
    }

    internal const string SmsNote =
        "You are answering text messages (SMS) sent to MapleKiosk's phone number, not the website chat. "
        + "Plain text only: no Markdown, no bold, no lists with symbols, no [[buttons]]. Keep each reply short — "
        + "one to three sentences, under 300 characters when you can. For a link write the full address, e.g. "
        + "https://www.maplekiosk.ca/coffee/pricing (put /fr in front of the path for French). "
        + "MapleKiosk is in Quebec: reply in the language the person writes in; if you can't tell, use French. "
        + "Your French is Quebec (Canadian) French.";

    /// <summary>The hold text: bilingual, the sender's language isn't known before the model answers.</summary>
    internal const string HoldText = "Un instant s.v.p., j'envoie votre demande. One moment please, I'm sending your request now.";

    private static readonly TimeSpan HoldAfter = TimeSpan.FromSeconds(1.5);

    internal const string Unavailable =
        "Désolé, l'assistant n'est pas disponible pour le moment. Sorry, the assistant is unavailable right now. "
        + "sales@maplekiosk.ca";

    /// <summary>One conversation per sender: the same 32-hex shape as every other /chats id.</summary>
    internal static string ConversationId(string from) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sms:" + PhoneHookup.Digits(from))))[..32].ToLowerInvariant();

    /// <summary>Whatever chat formatting slipped through, flattened for a phone's SMS app.</summary>
    internal static string Plain(string reply)
    {
        var s = Regex.Replace(reply, @"\[\[[^\]]*\]\]", "");                         // quick-reply buttons
        s = Regex.Replace(s, @"\[([^\]]+)\]\((/[^)\s]*)\)", "$1: https://www.maplekiosk.ca$2"); // site links
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", "$1: $2");               // other links
        s = s.Replace("**", "").Replace("__", "");
        s = Regex.Replace(s, @"^\s*[-*•]\s+", "- ", RegexOptions.Multiline);
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s.Length <= MaxReplyChars ? s : s[..(MaxReplyChars - 1)].TrimEnd() + "…";
    }

    /// <summary>Sends one SMS from MapleKiosk's number over Twilio's Messages API; false (logged) when it didn't go.</summary>
    internal static Task<bool> SendAsync(string from, string to, string body, ILogger logger, CancellationToken ct,
        string kind = SmsKind.Reply) =>
        TwilioApi.SendSmsAsync(from, to, body, logger, ct, kind);

    private static IResult Empty() => Results.Content(TwilioTwiml.Empty, TwilioTwiml.ContentType);
}
