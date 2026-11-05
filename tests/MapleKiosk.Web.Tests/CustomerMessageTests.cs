using MapleKiosk.Web.Assistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MapleKiosk.Web.Tests;

/// <summary>
/// Messages the assistant takes for the person in charge: they stay on the conversation after a restart, and
/// the customer is only told what really happened. Each test gets its own temporary data folder.
/// </summary>
public sealed class CustomerMessageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"chats-{Guid.NewGuid():N}");

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private ChatLog NewLog() => new(new Env(_root), NullLogger<ChatLog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_message_alone_is_kept_and_survives_a_restart()
    {
        var id = ChatLog.IdFor(null);
        var contact = new GuestContact("Lan", "+15145550199", null, "Nails Laval", null);
        var message = new GuestMessage("support", true, "Terminal is down", DateTimeOffset.UtcNow);

        Assert.True(await NewLog().SaveMessageAsync(id, contact, message));

        var saved = await NewLog().GetAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("Lan", saved.Contact?.Name);
        Assert.Equal(message, Assert.Single(saved.TakenMessages));
    }

    [Fact]
    public async Task A_message_keeps_the_interest_of_an_earlier_lead()
    {
        var id = ChatLog.IdFor(null);
        var log = NewLog();
        await log.SaveContactAsync(id, new GuestContact("Lan", null, "lan@example.com", null, "MapleSPA demo"));
        await log.SaveMessageAsync(id, new GuestContact("Lan", null, "lan@example.com", null, null),
            new GuestMessage("sales", false, "Call me", DateTimeOffset.UtcNow));

        Assert.Equal("MapleSPA demo", (await log.GetAsync(id))?.Contact?.Interest);
    }

    [Theory]
    [InlineData(true, true, "Passed on")]
    [InlineData(true, false, "email failed")]
    [InlineData(false, true, "Passed on")]
    [InlineData(false, false, "FAILED")]
    public void The_customer_is_told_what_happened(bool saved, bool emailed, string expected) =>
        Assert.Contains(expected, CustomerMessageTool.Outcome(saved, emailed, "Lan"));
}
