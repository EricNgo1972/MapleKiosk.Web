// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Relay/VoiceSessionLimiter.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Caps how many conversations can be open at once.
///
/// <para>A cost guard, not a performance one. Every open session bills a vendor continuously for as long
/// as it stays open, so a kiosk stuck in a reconnect loop — a tab left on a wall display, a flaky tunnel —
/// would otherwise spend money all night with nobody in the building.</para>
///
/// <para>A static counter rather than a registered service: it holds one integer for the process and needs
/// no configuration of its own (the cap comes from the active profile), so making it injectable would add
/// a dependency to every caller and buy nothing.</para>
/// </summary>
internal static class VoiceSessionLimiter
{
    private static int _active;

    public static int Active => Volatile.Read(ref _active);

    /// <summary>Takes a slot, or returns false when the cap is reached. Release exactly once per success.</summary>
    public static bool TryAcquire(int maxConcurrent)
    {
        var max = Math.Max(1, maxConcurrent);

        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current >= max) return false;
            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current) return true;
        }
    }

    public static void Release()
    {
        // Never below zero: a double release would otherwise silently raise the effective cap.
        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current == 0) return;
            if (Interlocked.CompareExchange(ref _active, current - 1, current) == current) return;
        }
    }
}
