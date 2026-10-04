namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The tool source for the website's voice agent: none. Stands in for the platform's MCP broker
/// (Infrastructure/VoiceAgent/.../Mcp/McpToolBroker.cs), which reaches a head's tools over MCP — this site has
/// no MCP host and the agent only answers questions. The relay still adds its own <c>end_call</c>, which it
/// answers itself and never routes here.
///
/// <para>The broker contract requires refusing anything that was not advertised, so every call is an
/// error result (never a throw — an unanswered call id stalls the conversation).</para>
/// </summary>
internal sealed class EmptyVoiceToolBroker : IVoiceToolBroker
{
    public Task<IReadOnlyList<VoiceToolDefinition>> ListToolsAsync(VoiceToolAudience audience, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VoiceToolDefinition>>(Array.Empty<VoiceToolDefinition>());

    public Task<VoiceToolResult> InvokeAsync(VoiceToolCall call, CancellationToken ct) =>
        Task.FromResult(new VoiceToolResult(call.CallId, call.Name, "That tool is not available.", IsError: true));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class EmptyVoiceToolBrokerFactory : IVoiceToolBrokerFactory
{
    public Task<IVoiceToolBroker> CreateAsync(VoiceSessionContext? context = null, CancellationToken ct = default) =>
        Task.FromResult<IVoiceToolBroker>(new EmptyVoiceToolBroker());
}
