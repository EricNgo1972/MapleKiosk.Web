using System.Collections.Concurrent;

namespace SPC.Infrastructure.Auth;

/// <summary>
/// Per-identity sign-in attempt counter with timed lockout. Used by the
/// staff sign-in path to defeat brute-force PIN guessing — at threshold
/// failures the identity is locked out for a fixed duration. State is
/// in-memory and resets on app restart, which is acceptable for the
/// small-business deployment model: the kiosk is rarely restarted mid
/// attack, and persistent lockout state would only add operational
/// complexity (where to clear it, etc.).
/// </summary>
public sealed class LoginAttemptTracker
{
    /// <summary>Failures within the same identity before the lockout
    /// trips. After lockout the counter is reset; further failures during
    /// the lockout window keep extending it via the same threshold logic
    /// (each one increments again until threshold).</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long an identity stays locked after hitting the
    /// threshold. Cleared early by a successful sign-in.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private sealed class State
    {
        public int FailedAttempts;
        public DateTime LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, State> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey)) return false;
        if (!_states.TryGetValue(identityKey, out var state)) return false;
        return state.LockedUntilUtc > DateTime.UtcNow;
    }

    public DateTime? LockedUntilUtc(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey)) return null;
        if (!_states.TryGetValue(identityKey, out var state)) return null;
        return state.LockedUntilUtc > DateTime.UtcNow ? state.LockedUntilUtc : null;
    }

    /// <summary>
    /// Returns the current failed-attempt count for <paramref name="identityKey"/>
    /// (0 if unknown). Resets to 0 once a lockout fires — callers should treat a
    /// non-null <see cref="LockedUntilUtc"/> as the authoritative "no more
    /// attempts allowed" signal. UI surfaces this as "N attempts remaining"
    /// (= <see cref="MaxAttempts"/> − count) on the PIN entry view.
    /// </summary>
    public int FailedAttempts(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey)) return 0;
        return _states.TryGetValue(identityKey, out var state) ? state.FailedAttempts : 0;
    }

    public void RecordFailure(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey)) return;
        var state = _states.GetOrAdd(identityKey, _ => new State());
        lock (state)
        {
            state.FailedAttempts++;
            if (state.FailedAttempts >= MaxAttempts)
            {
                state.LockedUntilUtc = DateTime.UtcNow + LockoutDuration;
                state.FailedAttempts = 0;
            }
        }
    }

    public void RecordSuccess(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey)) return;
        _states.TryRemove(identityKey, out _);
    }
}
