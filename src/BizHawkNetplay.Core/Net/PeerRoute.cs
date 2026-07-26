using System;
using System.Collections.Generic;
using System.Net;

namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// The candidate UDP endpoints that can reach one logical remote player. A peer may advertise both
    /// a LAN endpoint and a reflexive/public endpoint; the mesh probes every candidate but sends input
    /// through only the best currently-live one.
    /// </summary>
    public sealed class PeerRoute
    {
        public PeerRoute(int remotePort, IEnumerable<IPEndPoint> candidates)
        {
            if (remotePort < 0) throw new ArgumentOutOfRangeException(nameof(remotePort));
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));

            var unique = new List<IPEndPoint>();
            var seen = new HashSet<IPEndPoint>();
            foreach (var candidate in candidates)
            {
                if (candidate == null)
                    throw new ArgumentException("Candidate endpoints cannot contain null", nameof(candidates));
                if (seen.Add(candidate)) unique.Add(candidate);
            }

            RemotePort = remotePort;
            Candidates = unique.AsReadOnly();
        }

        /// <summary>The controller port owned by this remote peer.</summary>
        public int RemotePort { get; }

        /// <summary>De-duplicated candidate endpoints, in preference/fallback order.</summary>
        public IReadOnlyList<IPEndPoint> Candidates { get; }
    }
}
