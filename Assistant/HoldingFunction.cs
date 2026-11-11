using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// A tool that tells the guest to hold on when it takes a moment. If the tool is still working after
/// <paramref name="after"/>, <paramref name="hold"/> runs once (on SMS: a "one moment, I'm sending your request"
/// text). A tool that answers straight away — a missing name, a bad email — sends nothing, so the guest never
/// hears "sending" for a request that wasn't sent.
/// </summary>
internal sealed class HoldingFunction(AIFunction inner, Func<Task> hold, TimeSpan after) : DelegatingAIFunction(inner)
{
    private int _held;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var call = base.InvokeCoreAsync(arguments, cancellationToken).AsTask();
        if (await Task.WhenAny(call, Task.Delay(after, cancellationToken)) != call && Interlocked.Exchange(ref _held, 1) == 0)
        {
            try { await hold(); } catch { /* best-effort: the request itself matters, not the notice */ }
        }
        return await call;
    }
}
