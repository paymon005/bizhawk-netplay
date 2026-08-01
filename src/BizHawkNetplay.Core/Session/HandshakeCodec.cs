using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session;

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

    /// <summary>Length of a per-peer mesh membership token (see <see cref="EncodeTokens"/>).</summary>
    public const int TokenBytes = 16;

    private static int ClampDelay(int d) => d < 1 ? 1 : d > MaxInputDelay ? MaxInputDelay : d;

    /// <param name="reflexive">
    /// This peer's public (STUN-discovered) UDP endpoint, if it knows one yet. It travels in the
    /// HELLO rather than as a separate message after GO because the host distributes mesh routes in
    /// WELCOME, and a route that lists only <c>(public IP, local port)</c> is a guess that holds
    /// only where the NAT preserves the source port. Without this the joiner-to-joiner edges — the
    /// ones the host cannot measure and the lobby delay probe exists to cover — have no reliable
    /// candidate to punch at until the session has already started.
    /// </param>
    public static byte[] Encode(PeerIdentity id, SessionPreferences prefs, int udpPort, byte[] nonce,
        IPEndPoint? reflexive = null)
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
        foreach (var field in id.SyncSettingsFields)
        {
            if (string.IsNullOrEmpty(field.Key)) continue;
            sb.Append("syncf=").Append(Escape(field.Key)).Append('|').Append(Escape(field.Value)).Append('\n');
        }
        if (reflexive != null)
        {
            sb.Append("refl=");
            AppendEndpoint(sb, reflexive);
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Encode the host's WELCOME: assignment, negotiated settings, input-timeline
    /// generation, and the candidate endpoints grouped by remote controller port.</summary>
    /// <param name="tokens">
    /// This recipient's mesh identity — the token it announces as itself, plus the tokens it should
    /// accept. The accepted set includes the host, which is never listed as a route.
    /// </param>
    public static byte[] EncodeWelcome(
        int assignedPort, int playerCount, int inputDelay, SyncMode mode,
        SessionGeneration generation, IEnumerable<PeerRoute>? peerRoutes = null,
        MeshTokens? tokens = null)
    {
        if (!generation.IsValid) throw new ArgumentException("A valid session generation is required", nameof(generation));
        var sb = new StringBuilder();
        sb.Append("port=").Append(assignedPort).Append('\n');
        sb.Append("players=").Append(playerCount).Append('\n');
        sb.Append("delay=").Append(inputDelay).Append('\n');
        sb.Append("mode=").Append(mode == SyncMode.Rollback ? "rollback" : "lockstep").Append('\n');
        sb.Append("session=").Append(generation.SessionId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("epoch=").Append(generation.Epoch.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (tokens?.Local is { } local)
        {
            if (local.Length != TokenBytes)
                throw new ArgumentException($"A token must be exactly {TokenBytes} bytes", nameof(tokens));
            sb.Append("mytok=").Append(SessionAuth.ToHex(local)).Append('\n');
        }
        sb.Append(Encoding.UTF8.GetString(EncodeRoutes(peerRoutes ?? Array.Empty<PeerRoute>())));
        if (tokens != null) sb.Append(Encoding.UTF8.GetString(EncodeTokens(tokens.Peers)));
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

    /// <summary>
    /// Largest candidate list one route may carry.
    ///
    /// A peer advertises a LAN address, a reflexive address and occasionally a second interface —
    /// three or four in practice. The cap is not about those: nothing bounded this at all, and the
    /// candidates feed the punch loop, which probes every one of them four times a second, and the
    /// no-confirmed-path fallback, which broadcasts input to every one of them. A WELCOME claiming
    /// a million endpoints turned the receiving joiner into a packet engine aimed wherever the
    /// sender chose. Extra candidates past the cap are dropped, which degrades exactly as a
    /// candidate that never answers already does.
    /// </summary>
    public const int MaxCandidatesPerRoute = 8;

    /// <summary>
    /// Largest text control body worth parsing.
    ///
    /// Not a guess: a peer may legitimately send MaxSyncFields entries, and a peer on a build with
    /// different truncation rules may send values longer than MaxSyncFieldChars — the decoder's job
    /// is to CAP those, gracefully, so a core with verbose sync settings still connects with a
    /// truncated explanation rather than failing the handshake. A megabyte is an order of magnitude
    /// above anything that shape can produce and still 64× below the control channel's savestate
    /// ceiling, which is what used to apply here: a 64MiB WELCOME was several hundred megabytes of
    /// transient strings on the joiner, because these bodies get split and dictionary-built two or
    /// three separate times each.
    /// </summary>
    public const int MaxTextBodyBytes = 1024 * 1024;

    private static void RefuseOversizedText(byte[] body)
    {
        if (body.Length > MaxTextBodyBytes)
            throw new FormatException(
                $"control body of {body.Length} bytes exceeds the {MaxTextBodyBytes}-byte limit for " +
                "a text frame");
    }

    /// <summary>Decode grouped peer routes, combining repeated groups and skipping malformed
    /// candidate endpoints. Invalid remote controller ports are ignored as untrusted input.</summary>
    public static List<PeerRoute> DecodeRoutes(byte[] body)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        RefuseOversizedText(body);

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
            for (int i = 1; i < fields.Length && candidates.Count < MaxCandidatesPerRoute; i++)
                if (TryParseEndpoint(fields[i], out var candidate) && seen.Add(candidate))
                    candidates.Add(candidate);
        }

        var routes = new List<PeerRoute>(order.Count);
        foreach (int remotePort in order)
            routes.Add(new PeerRoute(remotePort, byPort[remotePort]));
        return routes;
    }

    /// <summary>
    /// Encode per-peer membership tokens, one <c>tok=&lt;port&gt;:&lt;hex&gt;</c> line each.
    ///
    /// Kept as its own section rather than folded into the route lines because a token is not a
    /// route: the host's own token has to reach every joiner even though the host is never listed
    /// as one. Every WELCOME carries every seat, so a token never has to be re-sent — a seat's
    /// token outlives the player in it, which is what makes a rejoin on a new address recognisable.
    /// </summary>
    private static byte[] EncodeTokens(IEnumerable<KeyValuePair<int, byte[]>> tokens)
    {
        if (tokens == null) throw new ArgumentNullException(nameof(tokens));
        var sb = new StringBuilder();
        foreach (var kv in tokens)
        {
            if (kv.Key < 0 || kv.Key >= MaxPlayers)
                throw new ArgumentOutOfRangeException(nameof(tokens), $"Port must be between 0 and {MaxPlayers - 1}");
            if (kv.Value == null || kv.Value.Length != TokenBytes)
                throw new ArgumentException($"A token must be exactly {TokenBytes} bytes", nameof(tokens));
            sb.Append("tok=").Append(kv.Key).Append(':').Append(SessionAuth.ToHex(kv.Value)).Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Decode the token section of any body that carries one — <c>mytok=</c> becomes
    /// <see cref="MeshTokens.Local"/>, the <c>tok=</c> lines become <see cref="MeshTokens.Peers"/>.
    /// Malformed or wrong-length entries are skipped (untrusted input); last entry per port wins.</summary>
    public static MeshTokens DecodeTokens(byte[] body)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        RefuseOversizedText(body);
        byte[]? local = null;
        var peers = new Dictionary<int, byte[]>();
        foreach (var raw in Encoding.UTF8.GetString(body).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("mytok=", StringComparison.Ordinal))
            {
                local = ValidToken(line.Substring(6));
                continue;
            }
            if (!line.StartsWith("tok=", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (!int.TryParse(line.Substring(4, colon - 4), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int port) || port < 0 || port >= MaxPlayers)
                continue;
            if (ValidToken(line.Substring(colon + 1)) is { } token) peers[port] = token;
        }
        return new MeshTokens(local, peers);
    }

    private static byte[]? ValidToken(string hex)
    {
        var token = SessionAuth.FromHex(hex);
        return token != null && token.Length == TokenBytes ? token : null;
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
        if (body == null) throw new ArgumentNullException(nameof(body));
        RefuseOversizedText(body);
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

    public static (PeerIdentity id, SessionPreferences prefs, int udpPort, byte[]? nonce,
        IPEndPoint? reflexive) Decode(byte[] body)
    {
        var map = ParseLines(body);

        var layouts = map.TryGetValue("layouts", out var l) && l.Length > 0
            ? l.Split(',')
            : [];

        var id = new PeerIdentity(
            GetInt(map, "proto", 0),
            Get(map, "rom"),
            Get(map, "core"),
            Get(map, "corever"),
            Get(map, "sync"),
            layouts,
            Get(map, "det") == "1",
            GetInt(map, "depth", 0),
            DecodeSyncFields(body));

        // The remote's password is never on the wire — prefs carries only delay/rollback here. Clamp
        // delay to a sane range so a malformed/hostile peer can't request delay < 1 or a huge value
        // that would hang the host seeding that many neutral frames on the UI thread.
        var prefs = new SessionPreferences(ClampDelay(GetInt(map, "delay", 1)), Get(map, "rollback") == "1");
        // Every other number here is clamped; this one was not, and it is the one that reaches an
        // IPEndPoint constructor. A peer announcing udpport=70000 threw ArgumentOutOfRangeException
        // out of the host's accept loop, past the per-joiner handler, and took the whole lobby down
        // — every other player losing their seat for one bad greet. 0 means "no usable UDP
        // endpoint", which the caller already has to handle for a peer that never discovered one.
        int udpPort = GetInt(map, "udpport", 0);
        if (udpPort < 1 || udpPort > 65535) udpPort = 0;
        byte[]? nonce = SessionAuth.FromHex(Get(map, "nonce")); // null if missing/malformed
        // Absent or malformed is simply "not discovered" — STUN can be blocked, and a peer that
        // cannot name its public endpoint still plays over whatever candidates do work.
        IPEndPoint? reflexive = TryParseEndpoint(Get(map, "refl"), out var parsed) ? parsed : null;
        return (id, prefs, udpPort, nonce, reflexive);
    }

    /// <summary>Hard cap on how many sync-setting fields a peer may describe. These are core-defined
    /// — this code does not choose what is in them — and the list is explanatory, not load-bearing,
    /// so it is bounded rather than trusted. Beyond the cap the digest still refuses the mismatch;
    /// only the explanation is truncated.</summary>
    public const int MaxSyncFields = 256;

    /// <summary>Longest value kept for one field, before truncation. Long enough for a plugin name
    /// or a path tail, short enough that a core with a surprising field cannot flood the log.</summary>
    public const int MaxSyncFieldChars = 96;

    /// <summary>Read the <c>syncf=</c> section. Its own scan rather than <see cref="ParseLines"/>,
    /// which is a map and would keep only the last of a repeated key.</summary>
    private static List<KeyValuePair<string, string>> DecodeSyncFields(byte[] body)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var raw in Encoding.UTF8.GetString(body).Split('\n'))
        {
            if (fields.Count >= MaxSyncFields) break;
            var line = raw.Trim();
            if (!line.StartsWith("syncf=", StringComparison.Ordinal)) continue;
            var payload = line.Substring(6);
            int bar = IndexOfUnescaped(payload, '|');
            if (bar < 0) continue;
            var key = Unescape(payload.Substring(0, bar));
            if (key.Length == 0) continue;
            fields.Add(new KeyValuePair<string, string>(
                Truncate(key), Truncate(Unescape(payload.Substring(bar + 1)))));
        }
        return fields;
    }

    private static string Truncate(string s) =>
        s.Length <= MaxSyncFieldChars ? s : s.Substring(0, MaxSyncFieldChars) + "…";

    // A value is core-defined text: it can hold the separator, the newline the line format ends on,
    // or the backslash doing the escaping. All three are escaped rather than assumed absent.
    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s!.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '|': sb.Append("\\p"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            switch (s[++i])
            {
                case '\\': sb.Append('\\'); break;
                case 'p': sb.Append('|'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                default: sb.Append(s[i]); break; // unknown escape: keep the character, drop nothing
            }
        }
        return sb.ToString();
    }

    private static int IndexOfUnescaped(string s, char target)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == target) return i;
        }
        return -1;
    }

    private static Dictionary<string, string> ParseLines(byte[] body)
    {
        RefuseOversizedText(body);
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
