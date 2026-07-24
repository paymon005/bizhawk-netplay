using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        public static byte[] Encode(PeerIdentity id, SessionPreferences prefs, int udpPort)
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
            sb.Append("udpport=").Append(udpPort).Append('\n');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Encode the host's WELCOME: a joiner's assigned port, the player count, the
        /// authoritative input delay, and the sync mode.</summary>
        public static byte[] EncodeWelcome(int assignedPort, int playerCount, int inputDelay, SyncMode mode)
        {
            var sb = new StringBuilder();
            sb.Append("port=").Append(assignedPort).Append('\n');
            sb.Append("players=").Append(playerCount).Append('\n');
            sb.Append("delay=").Append(inputDelay).Append('\n');
            sb.Append("mode=").Append(mode == SyncMode.Rollback ? "rollback" : "lockstep").Append('\n');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public static (int assignedPort, int playerCount, int inputDelay, SyncMode mode) DecodeWelcome(byte[] body)
        {
            var map = ParseLines(body);
            int port = Math.Max(0, GetInt(map, "port", 1));
            int players = Math.Max(2, GetInt(map, "players", 2));
            int delay = Math.Max(1, GetInt(map, "delay", 1));
            var mode = Get(map, "mode") == "rollback" ? SyncMode.Rollback : SyncMode.Lockstep;
            return (port, players, delay, mode);
        }

        public static (PeerIdentity id, SessionPreferences prefs, int udpPort) Decode(byte[] body)
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

            // Clamp delay to a sane floor so a malformed peer can't request delay < 1.
            var prefs = new SessionPreferences(Math.Max(1, GetInt(map, "delay", 1)), Get(map, "rollback") == "1");
            int udpPort = GetInt(map, "udpport", 0);
            return (id, prefs, udpPort);
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
    }
}
