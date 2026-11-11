using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The voice agent's tools — the chat assistant's own (<see cref="DemoRequestTool"/> book_demo,
/// <see cref="CustomerMessageTool"/> take_message, <see cref="GuestContactTool"/> save_guest_contact), so
/// voice, the phone line, chat and SMS book and take messages the same way. Replaces the empty broker.
///
/// <para>The tools write to the conversation's /chats record, so the endpoints put the record's id on the
/// session context (<see cref="VoiceSessionContext.SessionIdKey"/>) and the transcript store saves under the
/// same id. Per the broker contract it refuses anything it didn't advertise and never throws.</para>
/// </summary>
internal sealed class WebsiteVoiceToolBroker : IVoiceToolBroker
{
    /// <summary>The visitor's language on the session context (<c>en</c>, <c>fr</c>, <c>qc</c>…).</summary>
    public const string LangKey = "lang";

    /// <summary>MapleKiosk's Twilio number a phone caller rang — place_order texts the payment link from it.</summary>
    public const string LineKey = "line";

    private readonly Dictionary<string, AIFunction> _tools;
    private readonly ILogger _logger;

    public WebsiteVoiceToolBroker(IEnumerable<AIFunction> tools, ILogger logger)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    public Task<IReadOnlyList<VoiceToolDefinition>> ListToolsAsync(VoiceToolAudience audience, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VoiceToolDefinition>>(_tools.Values
            .Select(t => new VoiceToolDefinition(t.Name, t.Description, t.JsonSchema.GetRawText()))
            .ToArray());

    public async Task<VoiceToolResult> InvokeAsync(VoiceToolCall call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return new VoiceToolResult(call.CallId, call.Name, "That tool is not available.", IsError: true);

        try
        {
            var args = new AIFunctionArguments();
            if (!string.IsNullOrWhiteSpace(call.ArgumentsJson))
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        args[p.Name] = p.Value.Clone();
            }

            var result = await tool.InvokeAsync(args, ct);
            return new VoiceToolResult(call.CallId, call.Name, Text(result));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: tool {Tool} failed.", call.Name);
            return new VoiceToolResult(call.CallId, call.Name,
                "FAILED: that didn't go through. Apologise and give them sales@maplekiosk.ca.", IsError: true);
        }
    }

    internal static string Text(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => result.ToString() ?? "",
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class WebsiteVoiceToolBrokerFactory(
    DemoRequestTool demo, OrderTool order, CustomerMessageTool message, GuestContactTool contact, PersonCallTool person,
    ILoggerFactory loggers)
    : IVoiceToolBrokerFactory
{
    public Task<IVoiceToolBroker> CreateAsync(VoiceSessionContext? context = null, CancellationToken ct = default)
    {
        var logger = loggers.CreateLogger("MapleKiosk.Voice.Tools");
        // No record id: nowhere to write what the tools collect, so offer none rather than lose it.
        if (context?.SessionId is not { Length: 32 } id)
            return Task.FromResult<IVoiceToolBroker>(new WebsiteVoiceToolBroker([], logger));

        var lang = context.Get(WebsiteVoiceToolBroker.LangKey) ?? "en";
        var phone = context.IsPhoneCall;
        var page = phone ? "phone" : "/voice-test";
        return Task.FromResult<IVoiceToolBroker>(new WebsiteVoiceToolBroker(
        [
            demo.For(id, lang, phone ? "phone call" : "voice (browser)", context.Phone),
            order.For(id, lang, phone ? OrderTool.Channel.PhoneCall : OrderTool.Channel.BrowserVoice,
                context.Phone, context.Get(WebsiteVoiceToolBroker.LineKey)),
            message.For(id, lang, page),
            contact.For(id, lang, page),
            person.For(id, phone ? OrderTool.Channel.PhoneCall : OrderTool.Channel.BrowserVoice, context.Phone,
                context.Get(WebsiteVoiceToolBroker.LineKey), phone ? context.Get(VoiceSessionContext.CallSidKey) : null, lang),
        ], logger));
    }
}
