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
