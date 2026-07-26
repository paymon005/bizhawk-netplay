using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// Dependency-free text encoding of a peer's identity + preferences for the HELLO/WELCOME
    /// bodies (a compact <c>key=value</c> line format, so Core needs no JSON package). All values
    /// are peer-supplied and untrusted; the decoder tolerates missing/garbage fields by falling
    /// back to safe defaults, and the negotiator does the real validation.
    /// </summary>
    public static class HandshakeCodec
    {
        // Hard bounds on peer-supplied numbers. The wire is untrusted: without an upper clamp a peer can
        // report delay=int.MaxValue, and the host would then loop billions of times seeding neutral
        // inputs on the UI thread (a trivial hang/DoS). Player count bounds the pipeline's array sizing.
        public const int MaxInputDelay = 60;   // generous vs the UI's 20, but finite
        public const int MaxPlayers = 8;

        private static int ClampDelay(int d) => d < 1 ? 1 : d > MaxInputDelay ? MaxInputDelay : d;

        public static byte[] Encode(PeerIdentity id, SessionPreferences prefs, int udpPort, byte[] nonce)
        {
            var sb = new StringBuilder();
            sb.Append("proto=").Append(id.ProtocolVersion).Append('\n');
            sb.Append("rom=").Append(id.RomHash).Append('\n');
            sb.Append("core=").Append(id.CoreName).Append('\n');
            sb.Append("corever=").Append(id.CoreVersion).Append('\n');
            sb.Append("sync=").Append(id.SyncSettingsDigest).Append('\n');
            sb.Append("layouts=").Append(string.Join(",", id.PortLayoutDigests)).Append('\n');
            sb.Append("det=").Append(id.Deterministic ? '1' : '0').Append('\n');
            sb.Append("depth=").Append(id.MaxRollbackDepth).Append('\n');
            sb.Append("delay=").Append(prefs.InputDelay).Append('\n');
            sb.Append("rollback=").Append(prefs.WantRollback ? '1' : '0').Append('\n');
            // The password never crosses the wire — only this fresh nonce, which seeds the challenge-
            // response proof exchanged afterward (see SessionAuth). Empty nonce is tolerated (open session).
            sb.Append("nonce=").Append(nonce == null ? "" : SessionAuth.ToHex(nonce)).Append('\n');
            sb.Append("udpport=").Append(udpPort).Append('\n');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Encode the host's WELCOME: assignment, negotiated settings, input-timeline
        /// generation, and the candidate endpoints grouped by remote controller port.</summary>
        public static byte[] EncodeWelcome(
            int assignedPort, int playerCount, int inputDelay, SyncMode mode,
            SessionGeneration generation, IEnumerable<PeerRoute>? peerRoutes = null)
        {
            if (!generation.IsValid) throw new ArgumentException("A valid session generation is required", nameof(generation));
            var sb = new StringBuilder();
            sb.Append("port=").Append(assignedPort).Append('\n');
            sb.Append("players=").Append(playerCount).Append('\n');
            sb.Append("delay=").Append(inputDelay).Append('\n');
            sb.Append("mode=").Append(mode == SyncMode.Rollback ? "rollback" : "lockstep").Append('\n');
            sb.Append("session=").Append(generation.SessionId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("epoch=").Append(generation.Epoch.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(Encoding.UTF8.GetString(EncodeRoutes(peerRoutes ?? Array.Empty<PeerRoute>())));
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Encode candidate endpoints grouped by their logical remote controller port.
        /// The same body is used inside WELCOME and for live PeerList route updates.</summary>
        public static byte[] EncodeRoutes(IEnumerable<PeerRoute> routes)
        {
            if (routes == null) throw new ArgumentNullException(nameof(routes));

            var byPort = new Dictionary<int, List<IPEndPoint>>();
            var seenByPort = new Dictionary<int, HashSet<IPEndPoint>>();
            var order = new List<int>();
            foreach (var route in routes)
            {
                if (route == null) throw new ArgumentException("Routes cannot contain null", nameof(routes));
                if (route.RemotePort < 0 || route.RemotePort >= MaxPlayers)
                    throw new ArgumentOutOfRangeException(nameof(routes), $"Remote port must be between 0 and {MaxPlayers - 1}");
                if (!byPort.TryGetValue(route.RemotePort, out var candidates))
                {
                    candidates = new List<IPEndPoint>();
                    byPort.Add(route.RemotePort, candidates);
                    seenByPort.Add(route.RemotePort, new HashSet<IPEndPoint>());
                    order.Add(route.RemotePort);
                }
                var seen = seenByPort[route.RemotePort];
                foreach (var candidate in route.Candidates)
                    if (seen.Add(candidate)) candidates.Add(candidate);
            }

            var sb = new StringBuilder();
            foreach (int remotePort in order)
            {
                sb.Append("route=").Append(remotePort);
                foreach (var candidate in byPort[remotePort])
                {
                    sb.Append('|');
                    AppendEndpoint(sb, candidate);
                }
                sb.Append('\n');
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Decode grouped peer routes, combining repeated groups and skipping malformed
        /// candidate endpoints. Invalid remote controller ports are ignored as untrusted input.</summary>
        public static List<PeerRoute> DecodeRoutes(byte[] body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            var byPort = new Dictionary<int, List<IPEndPoint>>();
            var seenByPort = new Dictionary<int, HashSet<IPEndPoint>>();
            var order = new List<int>();
            foreach (var raw in Encoding.UTF8.GetString(body).Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("route=", StringComparison.Ordinal)) continue;
                var fields = line.Substring(6).Split('|');
                if (fields.Length == 0 ||
                    !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int remotePort) ||
                    remotePort < 0 || remotePort >= MaxPlayers)
                    continue;

                if (!byPort.TryGetValue(remotePort, out var candidates))
                {
                    candidates = new List<IPEndPoint>();
                    byPort.Add(remotePort, candidates);
                    seenByPort.Add(remotePort, new HashSet<IPEndPoint>());
                    order.Add(remotePort);
                }
                var seen = seenByPort[remotePort];
                for (int i = 1; i < fields.Length; i++)
                    if (TryParseEndpoint(fields[i], out var candidate) && seen.Add(candidate))
                        candidates.Add(candidate);
            }

            var routes = new List<PeerRoute>(order.Count);
            foreach (int remotePort in order)
                routes.Add(new PeerRoute(remotePort, byPort[remotePort]));
            return routes;
        }

        /// <summary>Encode a set of peer UDP endpoints (one "ip:port" per line) for the PeerList body.</summary>
        public static byte[] EncodeEndpoints(IEnumerable<IPEndPoint> endpoints)
        {
            var sb = new StringBuilder();
            foreach (var ep in endpoints)
            {
                AppendEndpoint(sb, ep);
                sb.Append('\n');
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Decode a PeerList body into endpoints, skipping any malformed line (untrusted input).</summary>
        public static List<IPEndPoint> DecodeEndpoints(byte[] body)
        {
            var list = new List<IPEndPoint>();
            foreach (var raw in Encoding.UTF8.GetString(body).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (TryParseEndpoint(line, out var endpoint)) list.Add(endpoint);
            }
            return list;
        }

        public static (int assignedPort, int playerCount, int inputDelay, SyncMode mode,
            SessionGeneration generation, IReadOnlyList<PeerRoute> peerRoutes) DecodeWelcome(byte[] body)
        {
            var map = ParseLines(body);
            int players = Math.Min(MaxPlayers, Math.Max(2, GetInt(map, "players", 2)));
            int port = Math.Min(players - 1, Math.Max(0, GetInt(map, "port", 1)));
            int delay = ClampDelay(GetInt(map, "delay", 1));
            var mode = Get(map, "mode") == "rollback" ? SyncMode.Rollback : SyncMode.Lockstep;
            if (!map.TryGetValue("session", out var sessionText) ||
                !ulong.TryParse(sessionText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong sessionId) ||
                sessionId == 0)
                throw new FormatException("WELCOME is missing a valid non-zero session ID");
            if (!map.TryGetValue("epoch", out var epochText) ||
                !int.TryParse(epochText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int epoch) ||
                epoch < 0)
                throw new FormatException("WELCOME is missing a valid non-negative epoch");
            var generation = new SessionGeneration(sessionId, epoch);
            return (port, players, delay, mode, generation, DecodeRoutes(body));
        }

        /// <summary>Encode a generation for READY/GO and resync-boundary control messages.</summary>
        public static byte[] EncodeGeneration(SessionGeneration generation)
        {
            if (!generation.IsValid) throw new ArgumentException("A valid session generation is required", nameof(generation));
            var body = new byte[12];
            ulong sessionId = generation.SessionId;
            for (int i = 0; i < 8; i++) body[i] = (byte)(sessionId >> (56 - 8 * i));
            uint epoch = (uint)generation.Epoch;
            body[8] = (byte)(epoch >> 24);
            body[9] = (byte)(epoch >> 16);
            body[10] = (byte)(epoch >> 8);
            body[11] = (byte)epoch;
            return body;
        }

        /// <summary>Decode the fixed-width generation body used by READY/GO and resync messages.</summary>
        public static SessionGeneration DecodeGeneration(byte[] body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (body.Length != 12) throw new FormatException("generation body must be exactly 12 bytes");
            ulong sessionId = 0;
            for (int i = 0; i < 8; i++) sessionId = (sessionId << 8) | body[i];
            uint epoch = ((uint)body[8] << 24) | ((uint)body[9] << 16) | ((uint)body[10] << 8) | body[11];
            if (sessionId == 0 || epoch > int.MaxValue)
                throw new FormatException("generation body contains an invalid session ID or epoch");
            return new SessionGeneration(sessionId, (int)epoch);
        }

        public static (PeerIdentity id, SessionPreferences prefs, int udpPort, byte[]? nonce) Decode(byte[] body)
        {
            var map = ParseLines(body);

            var layouts = map.TryGetValue("layouts", out var l) && l.Length > 0
                ? l.Split(',')
                : Array.Empty<string>();

            var id = new PeerIdentity(
                GetInt(map, "proto", 0),
                Get(map, "rom"),
                Get(map, "core"),
                Get(map, "corever"),
                Get(map, "sync"),
                layouts,
                Get(map, "det") == "1",
                GetInt(map, "depth", 0));

            // The remote's password is never on the wire — prefs carries only delay/rollback here. Clamp
            // delay to a sane range so a malformed/hostile peer can't request delay < 1 or a huge value
            // that would hang the host seeding that many neutral frames on the UI thread.
            var prefs = new SessionPreferences(ClampDelay(GetInt(map, "delay", 1)), Get(map, "rollback") == "1");
            int udpPort = GetInt(map, "udpport", 0);
            byte[]? nonce = SessionAuth.FromHex(Get(map, "nonce")); // null if missing/malformed
            return (id, prefs, udpPort, nonce);
        }

        private static Dictionary<string, string> ParseLines(byte[] body)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Encoding.UTF8.GetString(body).Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return map;
        }

        private static string Get(Dictionary<string, string> m, string k) => m.TryGetValue(k, out var v) ? v : "";

        private static int GetInt(Dictionary<string, string> m, string k, int fallback) =>
            m.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : fallback;

        private static void AppendEndpoint(StringBuilder sb, IPEndPoint endpoint)
        {
            if (endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                sb.Append('[').Append(endpoint.Address).Append(']');
            else
                sb.Append(endpoint.Address);
            sb.Append(':').Append(endpoint.Port.ToString(CultureInfo.InvariantCulture));
        }

        private static bool TryParseEndpoint(string text, out IPEndPoint endpoint)
        {
            endpoint = null!;
            string addressText;
            string portText;
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                int close = text.LastIndexOf("]:", StringComparison.Ordinal);
                if (close <= 1) return false;
                addressText = text.Substring(1, close - 1);
                portText = text.Substring(close + 2);
            }
            else
            {
                int colon = text.LastIndexOf(':');
                if (colon <= 0) return false;
                addressText = text.Substring(0, colon);
                portText = text.Substring(colon + 1);
            }
            if (!IPAddress.TryParse(addressText, out var address) ||
                !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                port <= 0 || port > 65535)
                return false;
            endpoint = new IPEndPoint(address, port);
            return true;
        }
    }
}
