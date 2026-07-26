using System;

namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// Identifies one input timeline. <see cref="SessionId"/> is a session-unique nonce and
    /// <see cref="Epoch"/> advances whenever that session replaces its simulation timeline (for
    /// example, after applying a resync state). Both values travel on every input datagram so a
    /// delayed packet from another session or an earlier timeline cannot enter the new pipeline.
    /// </summary>
    public readonly struct SessionGeneration : IEquatable<SessionGeneration>
    {
        /// <summary>
        /// Compatibility generation for callers which have not negotiated a session nonce yet.
        /// Production network sessions should always supply their negotiated generation explicitly.
        /// </summary>
        public static SessionGeneration Legacy { get; } = new SessionGeneration(1, 0);

        public SessionGeneration(ulong sessionId, int epoch)
        {
            if (sessionId == 0) throw new ArgumentOutOfRangeException(nameof(sessionId), "Session ID must be non-zero");
            if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch), "Epoch must be non-negative");
            SessionId = sessionId;
            Epoch = epoch;
        }

        public ulong SessionId { get; }
        public int Epoch { get; }
        public bool IsValid => SessionId != 0 && Epoch >= 0;

        /// <summary>Return the next input timeline within the same session.</summary>
        public SessionGeneration Next() => new SessionGeneration(SessionId, checked(Epoch + 1));

        public bool Equals(SessionGeneration other) =>
            SessionId == other.SessionId && Epoch == other.Epoch;

        public override bool Equals(object? obj) => obj is SessionGeneration other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)SessionId ^ (int)(SessionId >> 32);
                return (hash * 397) ^ Epoch;
            }
        }

        public static bool operator ==(SessionGeneration left, SessionGeneration right) => left.Equals(right);
        public static bool operator !=(SessionGeneration left, SessionGeneration right) => !left.Equals(right);

        public override string ToString() => $"{SessionId:x16}:{Epoch}";
    }
}
