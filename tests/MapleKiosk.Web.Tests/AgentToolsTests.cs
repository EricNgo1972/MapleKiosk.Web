using MapleKiosk.Web.Assistant;
using MapleKiosk.Web.Assistant.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MapleKiosk.Web.Tests;

/// <summary>book_demo's checks and the voice agent's tool broker.</summary>
public sealed class AgentToolsTests
{
    // The checks run before anything is saved, so no store is needed.
    private static readonly DemoRequestTool Tool = new(null!, null!, NullLogger<DemoRequestTool>.Instance);

    private static DemoRequestTool.Request Req(string name = "Marie", string business = "Café Soleil",
        string? phone = "+15145550123", string? email = null) =>
        new(name, business, "Restaurant", phone, email, null, null, null);

    [Fact]
    public async Task Demo_needs_a_name() =>
        Assert.StartsWith("Not booked: their name", await Tool.BookAsync("c1", "fr", "SMS", Req(name: ""), default));

    [Fact]
    public async Task Demo_needs_the_business() =>
        Assert.StartsWith("Not booked: the business", await Tool.BookAsync("c1", "fr", "SMS", Req(business: ""), default));

    [Fact]
    public async Task Demo_needs_a_way_to_reach_them() =>
        Assert.StartsWith("Not booked: there is no phone", await Tool.BookAsync("c1", "fr", "SMS", Req(phone: null), default));

    [Fact]
    public async Task Demo_refuses_a_broken_email() =>
        Assert.StartsWith("Not booked: that email", await Tool.BookAsync("c1", "fr", "SMS", Req(email: "marie.gmail.com"), default));

    [Fact]
    public void Demo_outcome_never_promises_a_time_and_says_failure_plainly()
    {
        Assert.Contains("never promise a specific date or time", DemoRequestTool.Outcome(true, true, "Marie", false));
        Assert.Contains("confirmation email", DemoRequestTool.Outcome(true, true, "Marie", true));
        Assert.StartsWith("FAILED", DemoRequestTool.Outcome(false, false, "Marie", true));
    }

    [Fact]
    public async Task Voice_broker_advertises_its_tools_runs_them_and_refuses_others()
    {
        var echo = AIFunctionFactory.Create((string name) => $"Hello {name}", "greet", "Says hello.");
        var broker = new WebsiteVoiceToolBroker([echo], NullLogger.Instance);

        var tools = await broker.ListToolsAsync(VoiceToolAudience.Customer, default);
        Assert.Equal("greet", Assert.Single(tools).Name);
        Assert.Contains("\"name\"", tools[0].ParametersJson);

        var ok = await broker.InvokeAsync(new VoiceToolCall("1", "greet", """{"name":"Marie"}"""), default);
        Assert.False(ok.IsError);
        Assert.Equal("Hello Marie", ok.Content);

        var no = await broker.InvokeAsync(new VoiceToolCall("2", "place_order", "{}"), default);
        Assert.True(no.IsError);
    }

    [Fact]
    public void Greetings_ask_for_the_name()
    {
        foreach (var lang in new[] { "qc", "fr", "en" })
            foreach (var phone in new[] { true, false })
                Assert.EndsWith("?", new MapleKioskVoicePersona(lang, phone).GreetingFor(VoiceToolAudience.Customer, "MapleKiosk"));
        Assert.Contains("name", new MapleKioskVoicePersona("en", true).GreetingFor(VoiceToolAudience.Customer, "MapleKiosk"));
    }

    // place_order's checks run before the catalog or Stripe is touched.
    private static readonly OrderTool Orders = new(null!, null!, null!, null!, null!, NullLogger<OrderTool>.Instance);

    private static OrderTool.Request Order(string product = "coffee", string[]? items = null, string name = "Marie",
        string? email = "marie@cafe.ca", string? phone = null, bool payNow = true) =>
        new(product, items ?? ["coffee-pro"], name, "Café Soleil", email, phone, payNow, null);

    private static Task<string> Place(OrderTool.Request r) =>
        Orders.PlaceAsync("c1", "qc", OrderTool.Channel.Chat, null, null, r, default);

    [Fact]
    public async Task Order_needs_a_known_trade() =>
        Assert.StartsWith("Not ordered: the trade", await Place(Order(product: "bakery")));

    [Fact]
    public async Task Order_needs_items() =>
        Assert.StartsWith("Not ordered: no items", await Place(Order(items: [])));

    [Fact]
    public async Task Paying_now_needs_an_email() =>
        Assert.StartsWith("Not ordered: paying now needs their email", await Place(Order(email: null, phone: "+15145550123")));

    [Fact]
    public async Task An_order_request_needs_a_way_to_reach_them() =>
        Assert.StartsWith("Not ordered: there is no phone", await Place(Order(email: null, payNow: false)));

    [Fact]
    public void Order_summary_says_each_price_and_how_often()
    {
        var items = new[]
        {
            new MapleKiosk.Web.Shop.Catalog.AppProduct { Name = "Pro", Price = 49m, BillingInterval = MapleKiosk.Web.Shop.Catalog.BillingIntervals.Monthly },
            new MapleKiosk.Web.Shop.Catalog.AppProduct { Name = "Setup", Price = 199.5m, BillingInterval = MapleKiosk.Web.Shop.Catalog.BillingIntervals.OneTime },
        };
        Assert.Equal("Pro 49 CAD/month, Setup 199.5 CAD one-time", OrderTool.Summary(items));
    }
}

public sealed class ChatChannelTests
{
    private static ChatConversation Conv(string? device) =>
        new("id", default, default, "en", "/", "/", 1, "", null, device, null, []);

    [Theory]
    [InlineData("Phone call", "phone")]
    [InlineData("SMS", "sms")]
    [InlineData("Voice (browser)", "voice")]
    // A chat records the visitor's device: a mobile chat is still a chat, not a phone call.
    [InlineData("Phone", "chat")]
    [InlineData("Computer", "chat")]
    [InlineData(null, "chat")]
    public void Channel_comes_from_how_it_was_recorded(string? device, string channel) =>
        Assert.Equal(channel, ChatChannel.Of(Conv(device)));

    [Fact]
    public void Team_emails_are_tagged() =>
        Assert.Equal("[SMS] ", ChatChannel.SubjectTag("sms"));
}

/// <summary>call_person's helpers, the call summary's parsing, Lisa's phone greeting.</summary>
public sealed class PersonAndSummaryTests
{
    [Theory]
    [InlineData("4187173979", "+14187173979")]
    [InlineData("418 717-3979", "+14187173979")]
    [InlineData("+1 (418) 717-3979", "+14187173979")]
    [InlineData("1-418-717-3979", "+14187173979")]
    [InlineData("+33 1 23 45 67 89", "+33123456789")]
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void Phone_numbers_normalise_to_e164(string input, string? expected) =>
        Assert.Equal(expected, PhoneHookup.E164(input));

    [Fact]
    public void A_number_is_read_digit_by_digit() =>
        Assert.Equal("4 1 8, 7 1 7, 3 9 7 9", PersonCallTool.Spoken("+14187173979"));

    [Fact]
    public void Numbers_show_north_american_style() =>
        Assert.Equal("(418) 717-3979", ChatChannel.PhoneLabel("+14187173979"));

    [Fact]
    public void The_transfer_dials_the_staff_phone_from_the_line()
    {
        var twiml = TwilioApi.DialTwiml("+14187173979", "+15145550000");
        Assert.Contains("<Dial callerId=\"+15145550000\" timeout=\"25\" ringTone=\"us\">+14187173979</Dial>", twiml);
        Assert.EndsWith("<Hangup/></Response>", twiml);
    }

    [Theory]
    [InlineData("vi", "vi-VN")]
    [InlineData("ru", "ru-RU")]
    public void The_hold_message_follows_the_caller(string lang, string tag) =>
        Assert.Contains($"language=\"{tag}\"", TwilioApi.DialTwiml("+14187173979", "+15145550000", lang));

    [Theory]
    [InlineData("Vietnamese", "vi")]
    [InlineData(" VI ", "vi")]
    [InlineData("fr", "fr")]
    [InlineData("klingon", null)]
    [InlineData(null, null)]
    public void The_callers_language_is_read_from_the_tool(string? said, string? expected) =>
        Assert.Equal(expected, PersonCallTool.LanguageOf(said));

    [Theory]
    [InlineData("en", false, true)]
    [InlineData("fr", true, false)]
    [InlineData(null, true, true)]
    public void The_transfer_tells_the_caller_to_hold_first(string? lang, bool french, bool english)
    {
        var twiml = TwilioApi.DialTwiml("+14187173979", "+15145550000", lang);
        var dial = twiml.IndexOf("<Dial", StringComparison.Ordinal);
        Assert.Equal(french, twiml.IndexOf("Je transfère", StringComparison.Ordinal) is >= 0 and var f && f < dial);
        Assert.Equal(english, twiml.IndexOf("transferring your call", StringComparison.Ordinal) is >= 0 and var e && e < dial);
    }

    [Fact]
    public void Summary_json_is_read_through_a_code_fence()
    {
        var found = ConversationSummarizer.Parse("```json\n{\"name\":\"Eric\",\"business\":null,\"email\":\"nope\",\"summary\":\"Asked about prices.\"}\n```");
        Assert.NotNull(found);
        Assert.Equal("Eric", found!.Name);
        Assert.Null(found.Business);
        Assert.Null(found.Email);
        Assert.Equal("Asked about prices.", found.Summary);
    }

    [Fact]
    public void No_json_no_summary() => Assert.Null(ConversationSummarizer.Parse("Sorry, I can't."));

    [Theory]
    [InlineData("Phone caller", true)]
    [InlineData("SMS sender", true)]
    [InlineData(null, true)]
    [InlineData("Eric", false)]
    public void Placeholder_names_are_replaced(string? name, bool placeholder) =>
        Assert.Equal(placeholder, ConversationSummarizer.IsPlaceholder(name));

    [Fact]
    public void The_greeting_from_setup_wins()
    {
        var persona = new MapleKioskVoicePersona(MapleKioskVoicePersona.Bilingual, phone: true, greeting: PhoneHookup.DefaultGreeting);
        Assert.Equal("Bonjour, Hi. This is Lisa from MapleKiosk, how can I help you today?",
            persona.GreetingFor(VoiceToolAudience.Customer, "MapleKiosk"));
    }

    [Fact]
    public void Lisa_is_her_name() =>
        Assert.Contains("Lisa", new MapleKioskVoicePersona("en", phone: true).ForAudience(VoiceToolAudience.Customer));
}

public sealed class LocalClockTests
{
    [Fact]
    public void The_note_gives_today_and_tomorrow_in_quebec_time()
    {
        // 2026-11-12 03:30 UTC is still Wednesday Nov 11 evening in Quebec (UTC-5).
        var note = LocalClock.Note(new DateTimeOffset(2026, 11, 12, 3, 30, 0, TimeSpan.Zero));
        Assert.Contains("Wednesday, November 11, 2026, 10:30 PM", note);
        Assert.Contains("tomorrow is Thursday, November 12", note);
    }

    [Fact]
    public void The_voice_prompt_knows_the_date() =>
        Assert.Contains("in Quebec (Eastern time", new MapleKioskVoicePersona("qc", phone: true).ForAudience(VoiceToolAudience.Customer));
}

public sealed class HoldingFunctionTests
{
    private static AIFunction Slow(int ms) =>
        AIFunctionFactory.Create(async () => { await Task.Delay(ms); return "done"; }, name: "slow");

    [Fact]
    public async Task A_slow_tool_says_hold_on_once()
    {
        var held = 0;
        var tool = new HoldingFunction(Slow(300), () => { held++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(50));
        Assert.Equal("done", WebsiteVoiceToolBroker.Text(await tool.InvokeAsync(new AIFunctionArguments())));
        await tool.InvokeAsync(new AIFunctionArguments());
        Assert.Equal(1, held);
    }

    [Fact]
    public async Task A_quick_answer_sends_nothing()
    {
        var held = 0;
        var tool = new HoldingFunction(Slow(0), () => { held++; return Task.CompletedTask; }, TimeSpan.FromSeconds(2));
        await tool.InvokeAsync(new AIFunctionArguments());
        Assert.Equal(0, held);
    }
}

public sealed class OpeningHoursTests
{
    [Theory]
    [InlineData(12, 59, false)]  // 7:59 AM Quebec (UTC-5 in November)
    [InlineData(13, 0, true)]    // 8:00 AM
    [InlineData(22, 59, true)]   // 5:59 PM
    [InlineData(23, 0, false)]   // 6:00 PM
    public void Open_every_day_8_to_6(int utcHour, int minute, bool open) =>
        Assert.Equal(open, LocalClock.IsOpen(new DateTimeOffset(2026, 11, 15, utcHour, minute, 0, TimeSpan.Zero))); // a Sunday

    [Fact]
    public void Lisa_knows_the_hours() =>
        Assert.Contains("Monday to Sunday, 8 AM to 6 PM", LocalClock.Note());
}

public sealed class TransferLoopTests
{
    [Theory]
    [InlineData("+14509125508", "+14509125508", null, true)]           // caller ID is the line itself
    [InlineData("+14509125508", "+14509125508", "+14509125508", true)]
    [InlineData("+14509125508", "+15550001111", "+14509125508", true)] // another number, but from the learned line
    [InlineData("+14187173979", "+14509125508", "+14509125508", false)] // Eric calling the line: answered
    [InlineData("anonymous", "+14509125508", "+14509125508", false)]
    public void Own_calls_forwarded_back_are_spotted(string from, string to, string? line, bool own) =>
        Assert.Equal(own, WebsitePhoneEndpoints.IsOwnCall(from, to, line));

    [Fact]
    public void They_are_refused_as_busy() => Assert.Contains("<Reject reason=\"busy\"/>", TwilioTwiml.Reject);
}

public sealed class AgentNameTests
{
    [Fact]
    public void The_default_name_leaves_text_alone() =>
        Assert.Equal("Я Лиза, this is Lisa", AssistantSettings.Named("Я Лиза, this is Lisa", "Lisa"));

    [Fact]
    public void Another_name_replaces_lisa_everywhere() =>
        Assert.Equal("Я Emma, this is Emma", AssistantSettings.Named("Я Лиза, this is Lisa", "Emma"));

    [Fact]
    public void The_default_phone_greeting_follows_the_name() =>
        Assert.Equal("Bonjour, Hi. This is Emma from MapleKiosk, how can I help you today?", PhoneHookup.DefaultGreetingFor("Emma"));

    [Fact]
    public void The_voice_agent_uses_the_name()
    {
        var persona = new MapleKioskVoicePersona("en", phone: true, agentName: "Emma");
        Assert.Contains("You are Emma", persona.ForAudience(VoiceToolAudience.Customer));
        Assert.DoesNotContain("Lisa", persona.ForAudience(VoiceToolAudience.Customer));
        Assert.Contains("Emma", persona.GreetingFor(VoiceToolAudience.Customer, "MapleKiosk"));
    }

    [Fact]
    public void The_chat_prompt_uses_the_name() =>
        Assert.Contains("you are Emma", AssistantPrompt.For(null, ConversationChannel.Chat, "Emma"));
}
