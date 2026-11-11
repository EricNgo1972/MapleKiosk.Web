using System.Collections.Concurrent;
using System.ComponentModel;
using MapleKiosk.Web.Assistant.Voice;
using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// <c>call_person</c>: the guest asks for a real person, and the staff phone (/admin/assistant, row
/// Assistant/StaffPhone) rings.
/// <list type="bullet">
///   <item>On a phone call: the call itself is put through to the staff phone (Twilio redirect → &lt;Dial&gt;),
///   a few seconds after the tool returns so the agent can say "one moment". No answer in 25 s: the line
///   apologises, says the team will call back, and hangs up.</item>
///   <item>On the chat, SMS or browser voice: the staff phone is rung from MapleKiosk's number with a spoken
///   note (who, why, their number) and texted the same, so the person can call the guest straight back.</item>
/// </list>
/// Either way the request is kept on the conversation (/chats) as an urgent "callback" message. Once per conversation.
/// </summary>
public sealed class PersonCallTool
{
    public const string Name = "call_person";

    /// <summary>How long the agent has to say "putting you through" before the call leaves it.</summary>
    internal static readonly TimeSpan TransferDelay = TimeSpan.FromSeconds(6);

    private readonly ChatLog _chats;
    private readonly ILogger<PersonCallTool> _logger;
    private readonly ConcurrentDictionary<string, bool> _done = new();

    public PersonCallTool(ChatLog chats, ILogger<PersonCallTool> logger)
    {
        _chats = chats;
        _logger = logger;
    }

    /// <param name="guestPhone">The number the guest is calling or texting from, when known.</param>
    /// <param name="lineNumber">MapleKiosk's number they called or texted; else the one learned from the webhooks.</param>
    /// <param name="callSid">The live call to put through — phone calls only.</param>
    public AIFunction For(string conversationId, OrderTool.Channel channel, string? guestPhone = null,
        string? lineNumber = null, string? callSid = null) =>
        AIFunctionFactory.Create(
            async ([Description("The guest's name, if they gave it.")] string? name,
                   [Description("The phone number where the person can call them back, if they gave one.")] string? phone,
                   [Description("Why they want a person, in a few words.")] string? reason,
                   CancellationToken ct) =>
                await CallAsync(conversationId, channel, Clean(name, 120), PhoneHookup.E164(phone) ?? PhoneHookup.E164(guestPhone),
                    Clean(reason, 300), lineNumber, callSid, ct),
            name: Name,
            description: channel == OrderTool.Channel.PhoneCall
                ? "Put this phone call through to a real person at MapleKiosk. Call it only when the caller asks to speak to a " +
                  "person, a human, someone on the team or the owner. Then say one short sentence (you're putting them through, " +
                  "please hold) and stop talking."
                : "Ring a real person at MapleKiosk so they call the guest back right away. Call it only when the guest asks to " +
                  "speak to a person, a human, someone on the team or the owner, and once you have the phone number to call " +
                  "them back on.");

    internal async Task<string> CallAsync(string id, OrderTool.Channel channel, string? name, string? guestPhone,
        string? reason, string? lineNumber, string? callSid, CancellationToken ct)
    {
        if (await AssistantSettings.StaffPhoneAsync() is not { } staff)
            return "Not available: nobody can be reached by phone right now. Offer to take a message for the team (take_message).";
        if (_done.ContainsKey(id))
            return "Already done in this conversation. Do not call this again.";
        // Nobody is rung outside the hours (8:00–18:00 every day): a message waits for the morning instead.
        if (!LocalClock.IsOpen())
            return "Closed: the team works every day from 8 AM to 6 PM (Quebec time), so nobody can take it now. Say so, and " +
                   "offer to take a message (take_message) so someone calls them back after 8 AM.";

        var line = PhoneHookup.E164(lineNumber) ?? await PhoneHookup.LineNumberAsync();
        var live = channel == OrderTool.Channel.PhoneCall && !string.IsNullOrEmpty(callSid);
        if (line is null)
            return "Not available: the phone line isn't set up. Offer to take a message for the team (take_message).";
        if (!live && guestPhone is null)
            return "Not yet: ask for the phone number where the person can call them back, repeat it to confirm, then call this again.";

        var who = name ?? "A guest";
        var where = channel switch
        {
            OrderTool.Channel.PhoneCall => "phone call",
            OrderTool.Channel.Sms => "SMS",
            OrderTool.Channel.BrowserVoice => "website voice",
            _ => "website chat",
        };
        var link = $"{SiteHost.Root.TrimEnd('/')}/chats/{id}";
        var text = $"MapleKiosk assistant: {who}{(guestPhone is null ? "" : $" ({guestPhone})")} on the {where} wants to "
                   + $"talk to a person{(reason is null ? "." : $": {reason}.")} {link}";

        await KeepAsync(id, name, guestPhone, reason, where, ct);
        _done[id] = true;

        // The details by text first: on a transfer they arrive before the phone rings.
        var texted = await TwilioApi.SendSmsAsync(line, staff, text, _logger, ct, Services.SmsKind.Alert);

        if (live)
        {
            var sid = callSid!;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TransferDelay);
                await TwilioApi.RedirectAsync(sid, TwilioApi.DialTwiml(staff, line), _logger, CancellationToken.None);
            });
            _logger.LogInformation("Assistant: putting call {CallSid} (conversation {Id}) through to the staff phone.", sid, id);
            return "Putting them through now. Say only, in one short sentence, that you're connecting them to someone and to " +
                   "please hold — then stop talking. If nobody answers, the line itself tells them the team will call back.";
        }

        var spoken = $"Hello, this is the MapleKiosk assistant. {who} on the {where} wants to talk to a person"
                     + (reason is null ? ". " : $", about {reason}. ")
                     + $"Please call them back at {Spoken(guestPhone!)}. The details are in your texts.";
        var rang = await TwilioApi.CallAsync(line, staff, TwilioApi.AnnounceTwiml(spoken), _logger, ct);
        if ((rang || texted) && channel != OrderTool.Channel.Sms)
            AutoTexts.Queue(guestPhone, AutoTexts.Callback(name, "qc"), _logger);
        _logger.LogInformation("Assistant: call_person for conversation {Id} — rang {Rang}, texted {Texted}.", id, rang, texted);

        return rang || texted
            ? $"Done: someone at MapleKiosk was just {(rang ? "called" : "texted")} with their number and will call them back " +
              $"at {guestPhone} shortly. Say exactly that — never promise a time — and do not call this again."
            : "FAILED: nobody could be reached. Apologise, say their request is saved for the team, and give them sales@maplekiosk.ca.";
    }

    /// <summary>On /chats: the contact (unless one is there already) and an urgent "callback" message.</summary>
    private async Task KeepAsync(string id, string? name, string? phone, string? reason, string where, CancellationToken ct)
    {
        try
        {
            var existing = (await _chats.GetAsync(id, ct))?.Contact;
            var contact = existing is not null && !ConversationSummarizer.IsPlaceholder(existing.Name)
                ? existing with { Phone = existing.Phone ?? phone }
                : new GuestContact(name ?? existing?.Name ?? "Guest", phone ?? existing?.Phone, existing?.Email,
                    existing?.Business, existing?.Interest);
            await _chats.SaveMessageAsync(id, contact,
                new GuestMessage("callback", true, $"Asked for a person ({where}){(reason is null ? "." : $": {reason}")}",
                    DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant: the call_person request for conversation {Id} could not be kept.", id);
        }
    }

    /// <summary>"+14187173979" → "4 1 8, 7 1 7, 3 9 7 9": a phone number the way a voice should read it.</summary>
    internal static string Spoken(string e164)
    {
        var d = PhoneHookup.Digits(e164);
        if (d.Length == 11 && d[0] == '1') d = d[1..];
        static string Each(string s) => string.Join(" ", s.ToCharArray());
        return d.Length == 10 ? $"{Each(d[..3])}, {Each(d[3..6])}, {Each(d[6..])}" : Each(d);
    }

    private static string? Clean(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : v[..max];
    }
}
