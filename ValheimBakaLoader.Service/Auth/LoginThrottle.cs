using System.Collections.Concurrent;

namespace ValheimBakaLoader.Service.Auth;

/// <summary>
/// Five wrong passwords from one address and that address waits a minute. The numbers
/// are small because the service has one account and a person with the password gets it
/// right the first or second time; everything beyond that is a script.
/// </summary>
public sealed class LoginThrottle
{
    private readonly int _failuresAllowed;
    private readonly TimeSpan _lockout;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public LoginThrottle(int failuresAllowed, TimeSpan lockout, TimeProvider? clock = null)
    {
        _failuresAllowed = failuresAllowed;
        _lockout = lockout;
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsLocked(string address)
    {
        if (!_entries.TryGetValue(Key(address), out var entry)) return false;
        if (entry.LockedUntil == null) return false;
        if (entry.LockedUntil > _clock.GetUtcNow()) return true;

        // The minute is over; the slate is clean.
        _entries.TryRemove(Key(address), out _);
        return false;
    }

    /// <summary>Counts one failure and says whether the address is now locked.</summary>
    public bool RecordFailure(string address)
    {
        var now = _clock.GetUtcNow();
        var entry = _entries.AddOrUpdate(
            Key(address),
            _ => new Entry(1, null),
            (_, old) => old.LockedUntil != null && old.LockedUntil > now
                ? old
                : new Entry(old.Failures + 1, null));

        if (entry.LockedUntil != null) return true;
        if (entry.Failures < _failuresAllowed) return false;

        _entries[Key(address)] = new Entry(entry.Failures, now + _lockout);
        return true;
    }

    public void Reset(string address) => _entries.TryRemove(Key(address), out _);

    private static string Key(string address) => string.IsNullOrEmpty(address) ? "unknown" : address;

    private sealed record Entry(int Failures, DateTimeOffset? LockedUntil);
}
