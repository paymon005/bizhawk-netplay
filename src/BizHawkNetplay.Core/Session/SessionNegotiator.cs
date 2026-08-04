using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Session;

/// <summary>How a peer wants to play, independent of what the other peer can do.</summary>
public sealed class SessionPreferences
{
    public SessionPreferences(int inputDelay, bool wantRollback, string password = "")
    {
        if (inputDelay < 1) throw new ArgumentOutOfRangeException(nameof(inputDelay), "Delay must be >= 1");
        InputDelay = inputDelay;
        WantRollback = wantRollback;
        Password = password ?? "";
    }

    /// <summary>Requested input delay D (frames). The session uses the larger of the two peers' asks.</summary>
    public int InputDelay { get; }

    /// <summary>Whether this peer opted into rollback (only honored if both peers qualify).</summary>
    public bool WantRollback { get; }

    /// <summary>The session password in the clear (empty = no password). NEVER transmitted: it's used
    /// locally to compute the handshake's nonce challenge-response proof (see <see cref="SessionAuth"/>).</summary>
    public string Password { get; }
}

/// <summary>The agreed session parameters, or a rejection with a human-readable reason.</summary>
public sealed class NegotiationResult
{
    private NegotiationResult(bool accepted, string? reason, SyncMode mode, int inputDelay,
        string? warning = null)
    {
        Accepted = accepted;
        RejectReason = reason;
        Mode = mode;
        InputDelay = inputDelay;
        Warning = warning;
    }

    public bool Accepted { get; }
    public string? RejectReason { get; }
    public SyncMode Mode { get; }
    public int InputDelay { get; }

    /// <summary>
    /// Something worth saying about an ACCEPTED session — a difference real enough to name but not
    /// to refuse over. Null when there is nothing to say.
    ///
    /// Separate from <see cref="RejectReason"/> because the two are opposite in kind, and folding a
    /// warning into a reason is how a warning ends up either unprinted or mistaken for a refusal.
    /// </summary>
    public string? Warning { get; }

    public static NegotiationResult Reject(string reason) =>
        new(false, reason, SyncMode.Lockstep, 0);

    public static NegotiationResult Accept(SyncMode mode, int inputDelay, string? warning = null) =>
        new(true, null, mode, inputDelay, warning);
}

/// <summary>
/// Pure decision logic for the handshake (§3.4). Given both peers' identities and preferences,
/// it verifies they match on everything determinism depends on and negotiates the sync mode and
/// input delay. No sockets here — the control channel feeds it the exchanged values and applies
/// the verdict, so all the rules are unit-tested.
/// </summary>
public static class SessionNegotiator
{
    public static NegotiationResult Negotiate(
        PeerIdentity local, PeerIdentity remote,
        SessionPreferences localPrefs, SessionPreferences remotePrefs)
    {
        if (local == null) throw new ArgumentNullException(nameof(local));
        if (remote == null) throw new ArgumentNullException(nameof(remote));

        if (local.ProtocolVersion != remote.ProtocolVersion)
            return NegotiationResult.Reject(
                $"protocol mismatch (local v{local.ProtocolVersion}, remote v{remote.ProtocolVersion})");

        if (!string.Equals(local.RomHash, remote.RomHash, StringComparison.OrdinalIgnoreCase))
            return NegotiationResult.Reject("ROM mismatch — both players must load the same ROM");

        if (!string.Equals(local.CoreName, remote.CoreName, StringComparison.Ordinal))
            return NegotiationResult.Reject(
                $"core mismatch ({local.CoreName} vs {remote.CoreName}) — select the same core");

        if (!string.Equals(local.CoreVersion, remote.CoreVersion, StringComparison.Ordinal))
            return NegotiationResult.Reject(
                $"core version mismatch ({local.CoreVersion} vs {remote.CoreVersion}) — use the same BizHawk build");

        // Before comparing the digests, establish that they mean anything.
        //
        // A settings read that threw produced an empty blob, and an empty blob hashes to a constant.
        // So two peers who both failed to read their own settings produced the SAME digest and this
        // comparison passed — the check silently inverted itself in exactly the circumstance it
        // existed for. It has to fail closed, because the digest is the only thing standing between
        // a plugin or region difference and a desync twenty minutes in.
        if (!local.SyncSettingsReadable)
            return NegotiationResult.Reject(
                "this core's sync settings could not be read, so there is nothing to compare against " +
                "the other player — and a difference there (video plugin, region, CPU core) desyncs " +
                "without warning. Reload the ROM and try again.");
        if (!remote.SyncSettingsReadable)
            return NegotiationResult.Reject(
                "the other player's core could not report its sync settings, so the two configurations " +
                "cannot be compared. Ask them to reload the ROM and try again.");

        if (!string.Equals(local.SyncSettingsDigest, remote.SyncSettingsDigest, StringComparison.Ordinal))
            return NegotiationResult.Reject(DescribeSyncSettingsMismatch(local, remote));

        // Point at the exact difference rather than a bare "controller layout mismatch" — the usual
        // cause is a per-port controller-type difference (3- vs 6-button pad, analog vs digital, a
        // multitap/peripheral) that's fixable in seconds once you know which port to look at.
        if (local.PortLayoutDigests.Count != remote.PortLayoutDigests.Count)
            return NegotiationResult.Reject(
                $"controller count differs — you expose {local.PortLayoutDigests.Count} port(s), the peer exposes " +
                $"{remote.PortLayoutDigests.Count}. Match the number of controllers / the multitap setting on both machines.");
        for (int i = 0; i < local.PortLayoutDigests.Count; i++)
            if (!string.Equals(local.PortLayoutDigests[i], remote.PortLayoutDigests[i], StringComparison.Ordinal))
                return NegotiationResult.Reject(
                    $"controller layout differs on port P{i + 1} — check that P{i + 1}'s controller type matches on " +
                    "both machines (e.g. 3- vs 6-button pad, analog vs digital, or an attached peripheral).");

        if (!local.Deterministic)
            return NegotiationResult.Reject("this core is not running deterministically here");
        if (!remote.Deterministic)
            return NegotiationResult.Reject("the remote core is not running deterministically");

        // (The session password is verified separately, via the nonce challenge-response in
        // Handshake/SessionAuth — it can't be a stateless equality check here without sending a
        // replayable hash over the wire.)

        // Input delay: honor the larger ask so both peers are comfortable (§8).
        int inputDelay = Math.Max(localPrefs.InputDelay, remotePrefs.InputDelay);

        // Rollback only when both peers asked for it AND both qualify on the probe (§5),
        // with the worst peer's depth deciding.
        bool bothWant = localPrefs.WantRollback && remotePrefs.WantRollback;
        int worstDepth = Math.Min(local.MaxRollbackDepth, remote.MaxRollbackDepth);
        bool rollbackViable = worstDepth >= ProbeResult.RollbackDepthThreshold;

        var mode = (bothWant && rollbackViable) ? SyncMode.Rollback : SyncMode.Lockstep;
        return NegotiationResult.Accept(mode, inputDelay, DescribeVideoDifference(local, remote));
    }

    /// <summary>How many differing settings to name before summarising the rest. Six fits a log line
    /// and, past that, the two machines are configured differently enough that listing every field
    /// is not the useful message.</summary>
    private const int MaxNamedSyncDifferences = 6;

    /// <summary>
    /// Name the sync settings that differ, rather than reporting that some do.
    ///
    /// Both peers run this and both run it symmetrically, so each side's "yours" is genuinely its
    /// own — the two players read complementary halves of the same answer off their own screens
    /// without having to compare logs.
    ///
    /// The digest has already decided; this only explains. Where it cannot explain — a peer too old
    /// to send its fields, a core that exposes none, or a difference that survives flattening — it
    /// says so instead of implying the settings match, because "no difference found" from a
    /// comparison that could not see one is the same sentence as a genuine match and must not read
    /// like it.
    /// </summary>
    /// <summary>
    /// Name a video-settings difference on a session that is otherwise going ahead.
    ///
    /// These settings are not part of any core's sync settings, so nothing compared them and a
    /// difference first announced itself as a desync. On N64 they are the render resolution and
    /// plugin, and above native resolution the plugin resolves its framebuffer back into RDRAM —
    /// GPU-produced bytes that two machines need not agree on.
    ///
    /// A warning rather than a refusal, deliberately. Whether a difference matters depends on
    /// whether the game reads its own framebuffer, which is a property of the game rather than of
    /// the numbers, and plenty of sessions run fine mismatched. What was unacceptable was silence.
    /// </summary>
    private static string? DescribeVideoDifference(PeerIdentity local, PeerIdentity remote)
    {
        if (local.VideoSettings.Length == 0 || remote.VideoSettings.Length == 0) return null;
        if (string.Equals(local.VideoSettings, remote.VideoSettings, StringComparison.Ordinal)) return null;
        return $"video settings differ — you: {local.VideoSettings}; them: {remote.VideoSettings}. " +
               "These are not part of the core's sync settings, so they were not required to match. " +
               "On a game that reads its own framebuffer they can desync you; matching them on both " +
               "machines is the safe choice.";
    }

    private static string DescribeSyncSettingsMismatch(PeerIdentity local, PeerIdentity remote)
    {
        const string headline = "core sync-settings mismatch";
        const string generic = headline + " — align sync settings on both ends";

        if (local.SyncSettingsFields.Count == 0 || remote.SyncSettingsFields.Count == 0)
            return generic;

        var theirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in remote.SyncSettingsFields) theirs[field.Key] = field.Value;

        var named = new List<string>();
        int extra = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in local.SyncSettingsFields)
        {
            seen.Add(field.Key);
            string mine = field.Value;
            string other = theirs.TryGetValue(field.Key, out var v) ? v : null!;
            if (other != null && string.Equals(mine, other, StringComparison.Ordinal)) continue;
            if (named.Count < MaxNamedSyncDifferences)
                named.Add(other == null
                    ? $"{field.Key} (yours {Show(mine)}, theirs absent)"
                    : $"{field.Key} (yours {Show(mine)}, theirs {Show(other)})");
            else extra++;
        }
        foreach (var field in remote.SyncSettingsFields)
        {
            if (seen.Contains(field.Key)) continue;
            if (named.Count < MaxNamedSyncDifferences)
                named.Add($"{field.Key} (yours absent, theirs {Show(field.Value)})");
            else extra++;
        }

        if (named.Count == 0)
            return headline + " — the settings this can compare all match, so the difference is in " +
                   "something it cannot see. Reload the ROM on both ends with identical core settings.";

        return headline + ": " + string.Join(", ", named)
             + (extra > 0 ? $", and {extra} more" : "")
             + ". Change these to match on both machines, then reload the ROM.";
    }

    private static string Show(string value) => value.Length == 0 ? "(empty)" : value;
}
