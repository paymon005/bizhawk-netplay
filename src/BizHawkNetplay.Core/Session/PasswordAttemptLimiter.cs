using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Meters how often one address may make the host derive a password key.
///
/// <b>The problem, which is not mainly about CPU.</b> Verifying a joiner's proof costs the host one
/// PBKDF2 derivation — about a second on the .NET Framework build that ships, and it is paid for
/// every connection that reaches the password step, including from someone who was never going to
/// pass it. The host's accept loop is serial, so those seconds are not merely CPU: they are a lobby
/// nobody can join. A stranger who can reach the port can hold the door shut without ever knowing
/// the password.
///
/// A refusal here costs microseconds, so the attempts that matter get through.
///
/// <b>Why a bucket rather than a count.</b> Legitimate use is bursty and then quiet: a player
/// mistypes twice and gets it right, or four people behind one household address join within a few
/// seconds of each other. A flat cap either bites on that or never bites at all. A bucket lets the
/// burst through and then meters the sustained rate, which is exactly the shape of the difference
/// between a lobby filling up and a lobby being hammered.
///
/// <b>Not a security control.</b> Nothing here protects the password — the stretch does that. This
/// bounds what an unauthenticated party can make the host spend, and nothing more.
/// </summary>
public sealed class PasswordAttemptLimiter
{
    /// <summary>
    /// Attempts one address may make back to back before the rate applies.
    ///
    /// Sized for the largest honest burst: a full lobby's worth of players arriving from one public
    /// address at once, plus a couple of mistyped passwords among them.
    /// </summary>
    public const int BurstAllowance = 10;

    /// <summary>
    /// How fast the allowance comes back — one attempt every five seconds.
    ///
    /// At the shipping KDF cost that caps a hostile address at roughly a fifth of one core, and
    /// leaves the accept loop free the rest of the time. A player who has genuinely locked
    /// themselves out is waiting seconds, not minutes.
    /// </summary>
    public const double RefillPerSecond = 0.2;

    /// <summary>
    /// How many addresses are tracked at once.
    ///
    /// The table is keyed by something a remote party chooses, so it needs a bound or it is its own
    /// slow memory leak. What goes when it is full is <see cref="EvictIfFull"/>, and the choice
    /// there is load-bearing rather than housekeeping.
    /// </summary>
    public const int MaxTrackedSources = 256;

    private sealed class Bucket
    {
        public double Tokens;
        public double LastSeenSeconds;
    }

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly int _burst;
    private readonly double _refillPerSecond;
    private readonly int _maxSources;

    public PasswordAttemptLimiter(
        int burst = BurstAllowance,
        double refillPerSecond = RefillPerSecond,
        int maxTrackedSources = MaxTrackedSources)
    {
        _burst = burst < 1 ? 1 : burst;
        _refillPerSecond = refillPerSecond <= 0 ? RefillPerSecond : refillPerSecond;
        _maxSources = maxTrackedSources < 1 ? 1 : maxTrackedSources;
    }

    /// <summary>Addresses currently being tracked. For the diagnostics line, and to prove the bound.</summary>
    public int TrackedSources => _buckets.Count;

    /// <summary>Attempts refused since this host started listening.</summary>
    public long Refused { get; private set; }

    /// <summary>
    /// Whether this address may make the host derive a key right now, spending one attempt if so.
    ///
    /// <paramref name="nowSeconds"/> is a monotonic reading supplied by the caller, like every other
    /// clock in this codebase — a limiter that read the wall clock could be widened by the system
    /// time moving.
    /// </summary>
    public bool TryBeginAttempt(string source, double nowSeconds)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        if (!_buckets.TryGetValue(source, out var bucket))
        {
            EvictIfFull(nowSeconds);
            bucket = new Bucket { Tokens = _burst, LastSeenSeconds = nowSeconds };
            _buckets[source] = bucket;
        }
        else
        {
            // Refill for the time that passed, capped at the burst. Clamped at zero elapsed so a
            // clock that goes backwards cannot mint tokens.
            double elapsed = nowSeconds - bucket.LastSeenSeconds;
            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(_burst, bucket.Tokens + elapsed * _refillPerSecond);
                bucket.LastSeenSeconds = nowSeconds;
            }
            else if (elapsed < 0) bucket.LastSeenSeconds = nowSeconds;
        }

        if (bucket.Tokens < 1)
        {
            Refused++;
            return false;
        }
        bucket.Tokens -= 1;
        return true;
    }

    /// <summary>
    /// This address completed a handshake, so forgive what it spent getting here.
    ///
    /// Someone who mistyped their password four times and then got it right is a player, not a
    /// threat, and should not arrive at their next session already half-metered.
    /// </summary>
    public void RecordSuccess(string source)
    {
        if (source != null && _buckets.TryGetValue(source, out var bucket)) bucket.Tokens = _burst;
    }

    /// <summary>How many attempts this address has left right now. For tests and the log.</summary>
    public int RemainingFor(string source, double nowSeconds)
    {
        if (!_buckets.TryGetValue(source, out var bucket)) return _burst;
        double elapsed = Math.Max(0, nowSeconds - bucket.LastSeenSeconds);
        return (int)Math.Min(_burst, bucket.Tokens + elapsed * _refillPerSecond);
    }

    /// <summary>A new lobby starts everyone even.</summary>
    public void Clear()
    {
        _buckets.Clear();
        Refused = 0;
    }

    /// <summary>
    /// Make room by dropping the address with the MOST allowance left, oldest first among equals.
    ///
    /// Not "least recently seen", which is the obvious rule and the wrong one. Forgetting an
    /// address restores it to a full burst, so evicting a spent entry hands back exactly the
    /// allowance it just used up — and an attacker with a handful of addresses could refill its
    /// meter by filling the table. Evicting a FULL entry costs nothing at all, because a fresh
    /// bucket starts full anyway.
    ///
    /// A test caught this: with several entries seen in the same instant, "oldest" is whatever the
    /// dictionary happened to enumerate first, which was sometimes the one being metered.
    /// </summary>
    private void EvictIfFull(double nowSeconds)
    {
        if (_buckets.Count < _maxSources) return;
        string? victim = null;
        double mostTokens = double.MinValue;
        double victimSeen = double.MaxValue;
        foreach (var entry in _buckets)
        {
            // Value each entry as it would stand NOW, so one that has quietly refilled is a
            // cheaper loss than one that spent recently.
            double elapsed = Math.Max(0, nowSeconds - entry.Value.LastSeenSeconds);
            double tokens = Math.Min(_burst, entry.Value.Tokens + elapsed * _refillPerSecond);
            if (tokens > mostTokens
                || (tokens == mostTokens && entry.Value.LastSeenSeconds < victimSeen))
            {
                mostTokens = tokens;
                victimSeen = entry.Value.LastSeenSeconds;
                victim = entry.Key;
            }
        }
        if (victim != null) _buckets.Remove(victim);
    }
}
