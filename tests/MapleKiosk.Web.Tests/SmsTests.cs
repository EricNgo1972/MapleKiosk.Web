using MapleKiosk.Web.Assistant;
using MapleKiosk.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MapleKiosk.Web.Tests;

/// <summary>The SMS log behind /admin/sms, and who the automatic texts go to.</summary>
public sealed class SmsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sms-{Guid.NewGuid():N}");

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task Texts_are_threaded_by_number_newest_first()
    {
        var store = new SmsStore(new Env(_root), NullLogger<SmsStore>.Instance);
        await store.AddAsync(true, "+15145550001", "+14509125508", "Bonjour", SmsKind.Guest);
        await store.AddAsync(false, "+15145550001", "+14509125508", "Bonjour! Ici Lisa.", SmsKind.Reply);
        await store.AddAsync(false, "+15145550002", "+14509125508", "MapleKiosk: thanks!", SmsKind.Auto, ok: false, error: "refused");

        var threads = await store.ThreadsAsync();
        Assert.Equal(["+15145550002", "+15145550001"], threads.Select(t => t.Number));
        Assert.Equal(1, threads[0].Failed);
        Assert.Equal(2, threads[1].Count);
        Assert.Equal("Bonjour! Ici Lisa.", threads[1].LastBody);
        Assert.False(threads[1].LastIncoming);

        var messages = await store.MessagesAsync("+15145550001");
        Assert.Equal(["Bonjour", "Bonjour! Ici Lisa."], messages.Select(m => m.Body));
        Assert.True(messages[0].Incoming);
    }

    [Theory]
    [InlineData("514 555-0123", "+15145550123")]
    [InlineData("+33 1 23 45 67 89", null)]      // outside Canada/US
    [InlineData("+14509125508", null)]           // the line itself
    [InlineData("418-717-3979", null)]           // the staff phone
    [InlineData("", null)]
    public void Automatic_texts_go_only_to_guests_in_canada_and_the_us(string phone, string? expected) =>
        Assert.Equal(expected, AutoTexts.Recipient(phone, "+14509125508", "+14187173979"));

    [Fact]
    public void Automatic_texts_need_a_line() => Assert.Null(AutoTexts.Recipient("514 555-0123", null, null));

    [Fact]
    public void Confirmations_speak_the_guests_language()
    {
        Assert.Equal("MapleKiosk: Thanks Marie! We got your demo request. Our team will call you to set the time.",
            AutoTexts.Demo("Marie Tremblay", "en"));
        Assert.StartsWith("MapleKiosk : Merci Marie! Votre demande de démo", AutoTexts.Demo("Marie", "fr"));
        Assert.Contains(" / Hi! Someone from MapleKiosk", AutoTexts.Callback(null, "qc"));
    }

    [Fact]
    public void Staff_alert_text_is_one_line_capped_and_keeps_the_link()
    {
        Assert.Equal("MapleKiosk: New demo request: Marie, Café X.", StaffAlert.Text("New demo request:\n Marie,  Café X.", ""));
        var link = "https://www.maplekiosk.ca/chats/abc";
        var t = StaffAlert.Text(new string('a', 1000), link);
        Assert.Equal(StaffAlert.MaxText, t.Length);
        Assert.EndsWith("… " + link, t);
    }
}
