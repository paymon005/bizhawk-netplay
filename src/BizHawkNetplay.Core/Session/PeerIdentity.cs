using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// Everything two peers must agree on before a session can be deterministic, exchanged in the
    /// handshake. The tool verifies configuration rather than trusting it (§3.1): any mismatch
    /// here refuses the session up front instead of surfacing later as a silent desync.
    /// </summary>
    public sealed class PeerIdentity
    {
        public PeerIdentity(
            int protocolVersion,
            string romHash,
            string coreName,
            string coreVersion,
            string syncSettingsDigest,
            IReadOnlyList<string> portLayoutDigests,
            bool deterministic,
            int maxRollbackDepth)
        {
            ProtocolVersion = protocolVersion;
            RomHash = romHash ?? "";
            CoreName = coreName ?? "";
            CoreVersion = coreVersion ?? "";
            SyncSettingsDigest = syncSettingsDigest ?? "";
            PortLayoutDigests = portLayoutDigests ?? Array.Empty<string>();
            Deterministic = deterministic;
            MaxRollbackDepth = maxRollbackDepth;
        }

        public int ProtocolVersion { get; }
        public string RomHash { get; }
        public string CoreName { get; }
        public string CoreVersion { get; }
        public string SyncSettingsDigest { get; }
        public IReadOnlyList<string> PortLayoutDigests { get; }
        public bool Deterministic { get; }

        /// <summary>This peer's capability-probe result (§5); rollback needs both peers to qualify.</summary>
        public int MaxRollbackDepth { get; }
    }

    /// <summary>The sync mode a negotiated session will run in.</summary>
    public enum SyncMode
    {
        Lockstep,
        Rollback,
    }
}
