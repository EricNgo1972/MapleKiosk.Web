using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// Fallback <see cref="IChatClient"/> used when no real LLM is configured. Returns a plain reply, so the
/// visitor gets an answer instead of an error. (From the platform's chat gateway.)
/// </summary>
internal sealed class StubChatClient : IChatClient
{
    public const string Text =
        "The assistant isn't available right now. Please write to sales@maplekiosk.ca or use “Book a demo” at the top of the page.";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Text)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("StubChatClient does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
