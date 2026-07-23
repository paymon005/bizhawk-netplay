using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>How a peer wants to play, independent of what the other peer can do.</summary>
    public sealed class SessionPreferences
    {
        public SessionPreferences(int inputDelay, bool wantRollback)
        {
            if (inputDelay < 1) throw new ArgumentOutOfRangeException(nameof(inputDelay), "Delay must be >= 1");
            InputDelay = inputDelay;
            WantRollback = wantRollback;
        }

        /// <summary>Requested input delay D (frames). The session uses the larger of the two peers' asks.</summary>
        public int InputDelay { get; }

        /// <summary>Whether this peer opted into rollback (only honored if both peers qualify).</summary>
        public bool WantRollback { get; }
    }

    /// <summary>The agreed session parameters, or a rejection with a human-readable reason.</summary>
    public sealed class NegotiationResult
    {
        private NegotiationResult(bool accepted, string? reason, SyncMode mode, int inputDelay)
        {
            Accepted = accepted;
            RejectReason = reason;
            Mode = mode;
            InputDelay = inputDelay;
        }

        public bool Accepted { get; }
        public string? RejectReason { get; }
        public SyncMode Mode { get; }
        public int InputDelay { get; }

        public static NegotiationResult Reject(string reason) =>
            new NegotiationResult(false, reason, SyncMode.Lockstep, 0);

        public static NegotiationResult Accept(SyncMode mode, int inputDelay) =>
            new NegotiationResult(true, null, mode, inputDelay);
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

            if (!string.Equals(local.SyncSettingsDigest, remote.SyncSettingsDigest, StringComparison.Ordinal))
                return NegotiationResult.Reject("core sync-settings mismatch — align sync settings on both ends");

            if (!SequenceEqual(local.PortLayoutDigests, remote.PortLayoutDigests))
                return NegotiationResult.Reject("controller layout mismatch");

            if (!local.Deterministic)
                return NegotiationResult.Reject("this core is not running deterministically here");
            if (!remote.Deterministic)
                return NegotiationResult.Reject("the remote core is not running deterministically");

            // Input delay: honor the larger ask so both peers are comfortable (§8).
            int inputDelay = Math.Max(localPrefs.InputDelay, remotePrefs.InputDelay);

            // Rollback only when both peers asked for it AND both qualify on the probe (§5),
            // with the worst peer's depth deciding.
            bool bothWant = localPrefs.WantRollback && remotePrefs.WantRollback;
            int worstDepth = Math.Min(local.MaxRollbackDepth, remote.MaxRollbackDepth);
            bool rollbackViable = worstDepth >= ProbeResult.RollbackDepthThreshold;

            var mode = (bothWant && rollbackViable) ? SyncMode.Rollback : SyncMode.Lockstep;
            return NegotiationResult.Accept(mode, inputDelay);
        }

        private static bool SequenceEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
