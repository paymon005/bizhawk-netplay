using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Session;

/// <summary>Thrown when the handshake refuses a session; the message is user-facing.</summary>
public sealed class HandshakeException : Exception
{
    public HandshakeException(string message) : base(message) { }
}

/// <summary>The agreed parameters a FrameDriver needs after a successful handshake.</summary>
public sealed class SessionParams
{
    public SessionParams(SyncMode mode, int inputDelay, int localPort, int remotePort,
        int remoteUdpPort, byte[]? initialState, SessionGeneration generation,
        int playerCount = 2, IReadOnlyList<PeerRoute>? peerRoutes = null, MeshTokens? tokens = null,
        IReadOnlyList<int>? vacatedSeats = null,
        int checksumInterval = ChecksumCadence.DefaultIntervalFrames)
    {
        Tokens = tokens ?? MeshTokens.None;
        VacatedSeats = vacatedSeats ?? Array.Empty<int>();
        ChecksumInterval = ChecksumCadence.IsAcceptable(checksumInterval)
            ? checksumInterval : ChecksumCadence.DefaultIntervalFrames;
        Mode = mode;
        InputDelay = inputDelay;
        LocalPort = localPort;
        RemotePort = remotePort;
        RemoteUdpPort = remoteUdpPort;
        InitialState = initialState;
        Generation = generation.IsValid
            ? generation
            : throw new ArgumentException("A valid session generation is required", nameof(generation));
        PlayerCount = playerCount;
        var routes = peerRoutes == null ? new List<PeerRoute>() : new List<PeerRoute>(peerRoutes);
        PeerRoutes = routes.AsReadOnly();

        // Compatibility projection for callers not yet route-aware. PeerRoutes is canonical: unlike
        // this flattened view it preserves which fallback candidates belong to which remote player.
        var endpoints = new List<IPEndPoint>();
        var seen = new HashSet<IPEndPoint>();
        foreach (var route in routes)
            foreach (var candidate in route.Candidates)
                if (seen.Add(candidate)) endpoints.Add(candidate);
        MeshPeers = endpoints.AsReadOnly();
    }

    public SyncMode Mode { get; }
    public int InputDelay { get; }
    public int LocalPort { get; }
    public int RemotePort { get; }

    /// <summary>Total number of players (= controller ports sourced by peers) in this session.</summary>
    public int PlayerCount { get; }

    /// <summary>The peer's UDP port for the input channel (combine with the peer IP from the control socket).</summary>
    public int RemoteUdpPort { get; }

    /// <summary>Whole-core state to import before starting; null for the host (it keeps its own).</summary>
    public byte[]? InitialState { get; }

    /// <summary>The input timeline accepted by this session. It changes on every resync/reconnect.</summary>
    public SessionGeneration Generation { get; }

    /// <summary>Candidate UDP endpoints grouped by the remote controller port they reach. For a
    /// joiner these are the other joiners; the host/control peer remains described by
    /// <see cref="RemotePort"/> and <see cref="RemoteUdpPort"/>.</summary>
    public IReadOnlyList<PeerRoute> PeerRoutes { get; }

    /// <summary>This peer's mesh identity for the session: the token it announces as itself and the
    /// ones it accepts. Empty on a session whose host predates them.</summary>
    public MeshTokens Tokens { get; }

    /// <summary>Seats that are permanently empty — their players left and the session carries on
    /// without them. Non-empty only for a rejoiner entering a session that already lost someone
    /// for good; the driver must be built with these ports vacated.</summary>
    public IReadOnlyList<int> VacatedSeats { get; }

    /// <summary>The session's checksum cadence, chosen by the host from its measured hash cost
    /// (see <see cref="ChecksumCadence"/>). Every peer must quantize to this figure.</summary>
    public int ChecksumInterval { get; }

    /// <summary>The OTHER peers' UDP endpoints for the direct input mesh (excludes self and the host,
    /// which the joiner reaches at the address it connected to). Empty for a 2-player session.</summary>
    public IReadOnlyList<IPEndPoint> MeshPeers { get; }
}

/// <summary>
/// Drives the §3.4 handshake over a <see cref="ControlChannel"/>. Because
/// <see cref="SessionNegotiator"/> is symmetric, both peers derive the same mode and delay from
/// the exchanged HELLOs — only the port roles (host=0, client=1) and the initial state transfer
/// are directional. All emulator access stays outside this class: the caller passes its own
/// identity/prefs/state in and applies the returned params.
/// </summary>
public static class Handshake
{
    /// <summary>
    /// Host side: accept a joiner, transfer initial state, agree on parameters.
    ///
    /// SUPERSEDED, and no longer called by the tool. The session path is the multi-peer sequence —
    /// <see cref="HostGreet"/>, then <see cref="HostSendWelcome"/> / <see cref="HostSendStart"/> /
    /// <see cref="HostSendAssignment"/>, then the READY barrier and <see cref="HostSendGo"/> — which
    /// handles one joiner as the two-player case rather than treating it specially.
    ///
    /// Kept because it is the fixture the handshake tests drive: rom mismatch, the password
    /// challenge, state transfer, the post-apply callback and the rollback downgrade are all
    /// exercised through here, and every step it performs is a call into the same production code
    /// the multi-peer path uses. Only this ORDERING is test-only. It has not been moved into the
    /// test project because <c>ReceiveStartData</c>, which its client half needs, is private and
    /// shared with the live <see cref="RunClientMulti"/> — exposing that to delete this would be the
    /// worse trade.
    /// </summary>
    public static SessionParams RunHost(
        ControlChannel channel, PeerIdentity hostId, SessionPreferences hostPrefs,
        byte[] hostState, int localUdpPort, Action<SessionParams>? beforeGo = null,
        bool forceHostRollback = false,
        Func<ControlChannel, SyncMode, int, int>? selectInputDelay = null)
        => RunHost(channel, hostId, hostPrefs, hostState, localUdpPort,
            new SessionGeneration(SessionAuth.NewSessionId(), epoch: 1), beforeGo: beforeGo,
            forceHostRollback: forceHostRollback, selectInputDelay: selectInputDelay);

    /// <summary>Host side with an explicit input-timeline generation.</summary>
    public static SessionParams RunHost(
        ControlChannel channel, PeerIdentity hostId, SessionPreferences hostPrefs,
        byte[] hostState, int localUdpPort, SessionGeneration generation,
        IEnumerable<PeerRoute>? peerRoutes = null, Action<SessionParams>? beforeGo = null,
        bool forceHostRollback = false,
        Func<ControlChannel, SyncMode, int, int>? selectInputDelay = null)
    {
        var hostNonce = SessionAuth.NewNonce();
        channel.Send(ControlMessageType.Hello,
            HandshakeCodec.EncodeChallenge(hostId.ProtocolVersion, hostNonce));

        var (type, body) = channel.Receive();
        if (type != ControlMessageType.Hello)
            throw new HandshakeException($"expected HELLO from joiner, got {type}");
        // Intro first, identities only after the proofs, negotiate last — see HostGreet for all.
        var (_, joinNonce, clientUdpPort, _) = HandshakeCodec.DecodeJoinerIntro(body);

        VerifyPassword(channel, hostPrefs.Password, hostNonce, joinNonce, isHost: true);
        channel.Send(ControlMessageType.Hello,
            HandshakeCodec.Encode(hostId, hostPrefs, localUdpPort, hostNonce));

        (type, body) = channel.Receive();
        if (type == ControlMessageType.Error) throw new HandshakeException(RemoteText(body));
        if (type != ControlMessageType.Hello)
            throw new HandshakeException($"expected the joiner's identity after AUTH, got {type}");
        var (clientId, clientPrefs, _, _, _) = HandshakeCodec.Decode(body);

        var result = SessionNegotiator.Negotiate(hostId, clientId, hostPrefs, clientPrefs);
        if (!result.Accepted)
        {
            channel.Send(ControlMessageType.Error, Encoding.UTF8.GetBytes(result.RejectReason ?? "rejected"));
            throw new HandshakeException(result.RejectReason ?? "rejected");
        }

        // A forced host may bypass only its own probe recommendation. The remote still has to opt
        // into rollback and advertise a viable measured depth; otherwise WELCOME remains lockstep.
        if (forceHostRollback && hostPrefs.WantRollback && clientPrefs.WantRollback
            && clientId.MaxRollbackDepth >= ProbeResult.RollbackDepthThreshold)
            result = NegotiationResult.Accept(SyncMode.Rollback, result.InputDelay);

        int finalDelay = result.InputDelay;
        if (selectInputDelay != null)
        {
            // The callback may raise the negotiated floor from a pre-WELCOME lobby measurement,
            // but it may never undo a peer's explicit request or exceed the wire safety bound.
            int selected = selectInputDelay(channel, result.Mode, result.InputDelay);
            finalDelay = Math.Max(result.InputDelay,
                Math.Min(HandshakeCodec.MaxInputDelay, Math.Max(1, selected)));
        }

        // Host owns port 0. The joiner must apply the state and rebuild for this generation before
        // acknowledging READY; only then does GO release the shared frame clock.
        HostSendWelcome(channel, assignedPort: 1, playerCount: 2, finalDelay, result.Mode,
            hostState, generation, peerRoutes);
        HostWaitReady(channel, generation);
        var session = new SessionParams(result.Mode, finalDelay, localPort: 0, remotePort: 1,
            remoteUdpPort: clientUdpPort, initialState: null, generation);
        beforeGo?.Invoke(session);
        HostSendGo(channel, generation);

        return session;
    }

    /// <summary>Client half of <see cref="RunHost"/>, and superseded by
    /// <see cref="RunClientMulti"/> on the same terms — see that summary.</summary>
    public static SessionParams RunClient(
        ControlChannel channel, PeerIdentity clientId, SessionPreferences clientPrefs, int localUdpPort,
        Action<SessionParams>? beforeReady = null, Action? afterGreet = null)
    {
        var joinNonce = SessionAuth.NewNonce();
        channel.Send(ControlMessageType.Hello,
            HandshakeCodec.EncodeJoinerIntro(clientId.ProtocolVersion, joinNonce, localUdpPort));

        int hostUdpPort = GreetHost(channel, clientId, clientPrefs, joinNonce, localUdpPort);
        afterGreet?.Invoke();

        return ReceiveStartData(channel, hostUdpPort, beforeReady);
    }

    /// <summary>
    /// The session-password step: a mutual, nonce-bound challenge-response (see <see cref="SessionAuth"/>)
    /// run right after the HELLO exchange. The host verifies the joiner's proof BEFORE revealing its own,
    /// so a wrong-password joiner learns nothing to work with; then it proves itself so the joiner can
    /// trust the host too. An empty password on both ends produces matching proofs — the open-session
    /// case. Throws <see cref="HandshakeException"/> on a mismatch (message is user-facing).
    /// </summary>
    public static void VerifyPassword(
        ControlChannel channel, string? password, byte[]? hostNonce, byte[]? joinNonce, bool isHost)
    {
        if (hostNonce == null || joinNonce == null)
            throw new HandshakeException("handshake is missing an auth nonce — the peer may be on an incompatible build");

        string myRole = isHost ? SessionAuth.RoleHost : SessionAuth.RoleJoin;
        string peerRole = isHost ? SessionAuth.RoleJoin : SessionAuth.RoleHost;
        // One key derivation for both proofs — they differ only in a role tag applied after it —
        // and the key is KEPT this time: it becomes the control-frame MAC below, which is the
        // whole of the KI-13 network fix.
        var (myProof, peerExpected, sessionKey) =
            SessionAuth.ProofPairWithKey(password, myRole, peerRole, hostNonce, joinNonce);

        if (isHost)
        {
            var (t, b) = channel.Receive();
            if (t == ControlMessageType.Error) throw new HandshakeException(RemoteText(b));
            if (t != ControlMessageType.Auth) throw new HandshakeException($"expected AUTH from joiner, got {t}");
            if (!SessionAuth.FixedTimeEquals(Encoding.UTF8.GetString(b), peerExpected))
            {
                channel.Send(ControlMessageType.Error, Encoding.UTF8.GetBytes("wrong session password"));
                throw new HandshakeException("session password mismatch");
            }
            channel.Send(ControlMessageType.Auth, Encoding.UTF8.GetBytes(myProof));
        }
        else
        {
            channel.Send(ControlMessageType.Auth, Encoding.UTF8.GetBytes(myProof));
            var (t, b) = channel.Receive();
            if (t == ControlMessageType.Error) throw new HandshakeException(RemoteText(b));
            if (t != ControlMessageType.Auth) throw new HandshakeException($"expected AUTH from host, got {t}");
            if (!SessionAuth.FixedTimeEquals(Encoding.UTF8.GetString(b), peerExpected))
                throw new HandshakeException("session password mismatch (could not verify the host)");
        }

        // The savestate-sized frame ceiling is unlocked here and nowhere else. Everything that may
        // legitimately be large — the initial state, a resync — comes after this point, so before it
        // a peer declaring 64 MiB is asking for an allocation it has no standing to ask for.
        channel.Authenticated = true;

        // From here every frame in both directions carries a MAC. This is the end of the exchange
        // on both sides — the host has read the joiner's AUTH and sent its own; the joiner has
        // sent its AUTH and verified the host's — so each peer enables before sending any
        // post-auth frame, and the first MACed frame either sends is the first the other expects.
        // The AUTH frames themselves cannot be covered: the key they prove is the key this uses.
        var macKey = SessionAuth.MacKey(sessionKey);
        Array.Clear(sessionKey, 0, sessionKey.Length);
        channel.EnableIntegrity(macKey, isHost);
        Array.Clear(macKey, 0, macKey.Length); // the channel's HMAC holds its own copy
    }

    // ---- N-player (host-relay) handshake ---------------------------------------------
    // The 3–4 player flow splits the host side into per-joiner steps the caller orchestrates:
    // greet every joiner first (so all identities are validated and the authoritative input
    // delay = max over everyone is known), then send each a Welcome carrying its assigned port,
    // the player count, final delay, generation and grouped routes, followed by the shared state.
    // Every joiner applies that data before acknowledging READY; GO releases all of them together.

    /// <summary>Info a host records about one joiner after the HELLO exchange.</summary>
    public sealed class JoinerGreeting
    {
        public JoinerGreeting(PeerIdentity id, SessionPreferences prefs, int udpPort,
            IPEndPoint? reflexive = null)
        {
            Id = id; Prefs = prefs; UdpPort = udpPort; Reflexive = reflexive;
        }
        public PeerIdentity Id { get; }
        public SessionPreferences Prefs { get; }
        public int UdpPort { get; }

        /// <summary>The joiner's public UDP endpoint, if STUN found one before it connected. Null
        /// means the mesh has only this peer's observed address to punch at — which is a guess
        /// about port preservation, not a candidate.</summary>
        public IPEndPoint? Reflexive { get; }
    }

    /// <summary>
    /// Host, per joiner: send our HELLO, receive theirs, and validate the pairing (ROM/core/etc.).
    /// Throws <see cref="HandshakeException"/> on mismatch. Does NOT send state/Start yet — the
    /// caller greets every joiner first, then calls <see cref="HostSendWelcome"/> on each.
    /// </summary>
    public static JoinerGreeting HostGreet(
        ControlChannel channel, PeerIdentity hostId, SessionPreferences hostPrefs, int hostUdpPort)
    {
        var hostNonce = SessionAuth.NewNonce();
        // A challenge, not an identity — see HandshakeCodec.EncodeChallenge. Ours goes out once the
        // joiner has proved the password, below.
        channel.Send(ControlMessageType.Hello,
            HandshakeCodec.EncodeChallenge(hostId.ProtocolVersion, hostNonce));

        var (type, body) = channel.Receive();
        if (type != ControlMessageType.Hello)
            throw new HandshakeException($"expected HELLO from joiner, got {type}");
        // An intro, not an identity — the joiner's mirror of our challenge above. Its full HELLO
        // arrives only after the AUTH exchange proves both ends, closing the same pre-auth leak
        // EncodeChallenge closed on this side: previously a joiner tricked into dialing a
        // stranger's address handed over its complete identity before anything was proved.
        var (joinerProtocol, joinNonce, joinerUdpPort, joinerReflexive) =
            HandshakeCodec.DecodeJoinerIntro(body);

        // Checked here for the same reason the joiner checks the challenge's: a build that
        // disagrees about the protocol also disagrees about this very message sequence, and naming
        // both versions beats failing on a shape it does not recognise.
        if (joinerProtocol != hostId.ProtocolVersion)
            throw new HandshakeException(
                $"protocol mismatch (local v{hostId.ProtocolVersion}, remote v{joinerProtocol})");

        // Refused here, where a bad greet costs its author a seat. The codec turns an out-of-range
        // port into 0, and this value goes straight into an IPEndPoint at the call sites — where a
        // throw escapes the per-joiner handler and takes the whole lobby with it. A real build
        // always knows the port it bound, so 0 means the announcement was malformed.
        if (joinerUdpPort < 1 || joinerUdpPort > 65535)
            throw new HandshakeException(
                "the joiner did not announce a usable UDP port for its input path");

        // Verify the session password during the greet, so a wrong-password joiner is refused before
        // the host commits it a port / includes it in the delay + mode decisions — and before either
        // side has revealed an identity for a stranger to collect.
        VerifyPassword(channel, hostPrefs.Password, hostNonce, joinNonce, isHost: true);

        // Proved. Now they may know what we are running — and unconditionally, ahead of the verdict
        // below, because a mismatch is described from each peer's own side ("yours X, theirs Y") and
        // a joiner with no identity to compare against could only echo ours back at its player.
        channel.Send(ControlMessageType.Hello,
            HandshakeCodec.Encode(hostId, hostPrefs, hostUdpPort, hostNonce));

        (type, body) = channel.Receive();
        if (type == ControlMessageType.Error) throw new HandshakeException(RemoteText(body));
        if (type != ControlMessageType.Hello)
            throw new HandshakeException($"expected the joiner's identity after AUTH, got {type}");
        var (joinerId, joinerPrefs, _, _, _) = HandshakeCodec.Decode(body);

        var result = SessionNegotiator.Negotiate(hostId, joinerId, hostPrefs, joinerPrefs);
        if (!result.Accepted)
        {
            channel.Send(ControlMessageType.Error, Encoding.UTF8.GetBytes(result.RejectReason ?? "rejected"));
            throw new HandshakeException(result.RejectReason ?? "rejected");
        }

        return new JoinerGreeting(joinerId, joinerPrefs, joinerUdpPort, joinerReflexive);
    }

    /// <summary>
    /// Measure the settled round-trip time of an authenticated lobby control link before WELCOME,
    /// and how far the link swings above it. The client-side start loop echoes these probes while it
    /// waits. Median filtering keeps one scheduler/TCP spike from permanently adding another frame
    /// of input latency, and the jitter figure is reported alongside because delay has to cover the
    /// worst packet rather than the typical one — see <see cref="LobbyDelayPolicy"/>.
    /// </summary>
    public static LobbyRttSample MeasureLobbyRtt(ControlChannel channel, int samples = 9)
    {
        if (channel == null) throw new ArgumentNullException(nameof(channel));
        if (samples < 1 || samples > 20) throw new ArgumentOutOfRangeException(nameof(samples));

        var measured = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            long token = Stopwatch.GetTimestamp() ^ ((long)i << 48);
            var body = BitConverter.GetBytes(token);
            var timer = Stopwatch.StartNew();
            channel.Send(ControlMessageType.Ping, body);
            var (type, reply) = channel.Receive();
            timer.Stop();

            if (type == ControlMessageType.Error)
                throw new HandshakeException(RemoteText(reply));
            if (type != ControlMessageType.Pong || reply.Length != 8
                || BitConverter.ToInt64(reply, 0) != token)
                throw new HandshakeException($"expected lobby PONG from joiner, got {type}");
            measured[i] = timer.Elapsed.TotalMilliseconds;
        }

        Array.Sort(measured);
        int middle = measured.Length / 2;
        double median = measured.Length % 2 == 0
            ? (measured[middle - 1] + measured[middle]) / 2.0
            : measured[middle];

        // Nearest-rank 85th percentile: at the default 9 samples that is the 8th smallest — the
        // worst probe bar one. Taking the outright maximum would let a single scheduler or TCP
        // hiccup permanently buy a frame of input latency, which is the very thing the median
        // filter above exists to prevent.
        int rank = (int)Math.Ceiling(0.85 * measured.Length);
        if (rank < 1) rank = 1;
        if (rank > measured.Length) rank = measured.Length;
        return new LobbyRttSample(median, measured[rank - 1]);
    }

    /// <summary>
    /// Host, per joiner: send the assignment, generation, grouped routes for every OTHER joiner,
    /// and initial state, then request an apply-complete READY acknowledgement.
    /// </summary>
    public static void HostSendWelcome(
        ControlChannel channel, int assignedPort, int playerCount, int inputDelay, SyncMode mode, byte[] state,
        SessionGeneration generation, IEnumerable<PeerRoute>? peerRoutes = null, MeshTokens? tokens = null,
        IEnumerable<int>? vacatedPorts = null,
        int checksumInterval = ChecksumCadence.DefaultIntervalFrames)
    {
        HostSendStart(channel, assignedPort, playerCount, inputDelay, mode, state, generation, peerRoutes,
            tokens, vacatedPorts, checksumInterval);
        HostRequestReady(channel, generation);
    }

    /// <summary>
    /// The assignment and state alone, WITHOUT the READY request — so a caller that wants to run the
    /// mesh round first has somewhere to stand. The joiner has its routes from this point on, which
    /// is what lets it punch and measure the edges the host cannot see.
    /// </summary>
    public static void HostSendStart(
        ControlChannel channel, int assignedPort, int playerCount, int inputDelay, SyncMode mode, byte[] state,
        SessionGeneration generation, IEnumerable<PeerRoute>? peerRoutes = null, MeshTokens? tokens = null,
        IEnumerable<int>? vacatedPorts = null,
        int checksumInterval = ChecksumCadence.DefaultIntervalFrames)
    {
        HostSendAssignment(channel, assignedPort, playerCount, inputDelay, mode, generation, peerRoutes,
            tokens, vacatedPorts, checksumInterval);
        // Framed and deflated: this is the transfer a joiner sits through before the game starts,
        // and on a heavy core it was the whole savestate raw over whatever link they have.
        channel.Send(ControlMessageType.State, StateCompression.Pack(state ?? []));
    }

    /// <summary>
    /// The assignment alone — a WELCOME with no state behind it. Sent on its own when the lobby
    /// changes after a joiner already holds the state: another joiner left before GO, their seat was
    /// refilled, and the survivors need corrected ports and routes. The joiner treats a repeated
    /// WELCOME as a restart and keeps the state it has, which is what makes recovering from someone
    /// else's fumbled join cost a frame of control traffic instead of another full state transfer.
    /// </summary>
    public static void HostSendAssignment(
        ControlChannel channel, int assignedPort, int playerCount, int inputDelay, SyncMode mode,
        SessionGeneration generation, IEnumerable<PeerRoute>? peerRoutes = null, MeshTokens? tokens = null,
        IEnumerable<int>? vacatedPorts = null,
        int checksumInterval = ChecksumCadence.DefaultIntervalFrames)
        => channel.Send(ControlMessageType.Welcome,
            HandshakeCodec.EncodeWelcome(assignedPort, playerCount, inputDelay, mode, generation, peerRoutes,
                tokens, vacatedPorts, checksumInterval));

    /// <summary>Ask this joiner to apply everything it has been sent and acknowledge READY.</summary>
    public static void HostRequestReady(ControlChannel channel, SessionGeneration generation)
        => channel.Send(ControlMessageType.Ready, HandshakeCodec.EncodeGeneration(generation));

    /// <summary>Host: ask one joiner to measure its own UDP mesh edges and report the worst.</summary>
    public static void HostRequestMeshRtt(ControlChannel channel, SessionGeneration generation)
        => channel.Send(ControlMessageType.MeshRtt, HandshakeCodec.EncodeGeneration(generation));

    /// <summary>Host: take one joiner's mesh report. Throws if it is missing, malformed, or for a
    /// different generation — the report feeds the delay every peer will then be held to.</summary>
    public static LobbyMeshSample HostWaitMeshRtt(ControlChannel channel, SessionGeneration generation)
    {
        var (type, body) = channel.Receive();
        if (type == ControlMessageType.Error)
            throw new HandshakeException(RemoteText(body));
        if (type != ControlMessageType.MeshRtt)
            throw new HandshakeException($"expected a mesh RTT report from joiner, got {type}");
        if (!ControlMessageCodec.TryDecodeMeshRtt(body, out var reported, out double medianMs,
                out double highMs, out int measuredEdges, out int totalEdges, out int[] silentPorts))
            throw new HandshakeException("joiner sent a malformed mesh RTT report");
        if (reported != generation)
            throw new HandshakeException(
                $"mesh RTT generation mismatch: expected {generation}, got {reported}");
        return new LobbyMeshSample(new LobbyRttSample(medianMs, highMs), measuredEdges, totalEdges,
            silentPorts);
    }

    /// <summary>Host: publish the authoritative delay once every edge has reported. Sent after the
    /// mesh round and before READY, so the joiner builds its driver on the final figure rather than
    /// the provisional one WELCOME carried.</summary>
    public static void HostSendInputDelay(ControlChannel channel, SessionGeneration generation, int delay)
        => channel.Send(ControlMessageType.InputDelay,
            ControlMessageCodec.EncodeInputDelay(generation, delay));

    /// <summary>Host side of the apply barrier: wait until this joiner imported the state and rebuilt
    /// its driver for <paramref name="generation"/>.</summary>
    public static void HostWaitReady(ControlChannel channel, SessionGeneration generation)
    {
        var (type, body) = channel.Receive();
        if (type == ControlMessageType.Error)
            throw new HandshakeException(RemoteText(body));
        if (type != ControlMessageType.Ready)
            throw new HandshakeException($"expected READY from joiner, got {type}");
        RequireGeneration(ControlMessageType.Ready, body, generation);
    }

    /// <summary>Release one joiner after every participant has reached READY.</summary>
    public static void HostSendGo(ControlChannel channel, SessionGeneration generation)
        => channel.Send(ControlMessageType.Go, HandshakeCodec.EncodeGeneration(generation));

    /// <summary>
    /// Client (N-player): send HELLO, validate the host's HELLO, then take the authoritative
    /// assignment/generation/routes from WELCOME, receive and apply the initial state, acknowledge
    /// READY, and wait for generation-matching GO.
    /// </summary>
    public static SessionParams RunClientMulti(
        ControlChannel channel, PeerIdentity clientId, SessionPreferences clientPrefs, int localUdpPort,
        Action<SessionParams>? beforeReady = null, Action? afterGreet = null,
        Func<int, IReadOnlyList<PeerRoute>, MeshTokens, LobbyMeshSample>? measureMesh = null,
        IPEndPoint? localReflexive = null)
    {
        var joinNonce = SessionAuth.NewNonce();
        channel.Send(ControlMessageType.Hello,
            HandshakeCodec.EncodeJoinerIntro(clientId.ProtocolVersion, joinNonce, localUdpPort,
                localReflexive));

        int hostUdpPort = GreetHost(channel, clientId, clientPrefs, joinNonce, localUdpPort,
            localReflexive);
        afterGreet?.Invoke();

        return ReceiveStartData(channel, hostUdpPort, beforeReady, measureMesh);
    }

    /// <summary>
    /// Joiner side of the split greeting: take the host's challenge, prove the password, send our
    /// own identity only once the host's proof has verified, and then take and check the host's
    /// identity. Returns the host's UDP port for the input path.
    ///
    /// The negotiate stays here rather than moving into the caller because it is the joiner's own
    /// defence against a host that accepted a pairing it should not have: it has to happen before
    /// anything is applied, and the next thing the caller does is start accepting a savestate.
    /// </summary>
    private static int GreetHost(
        ControlChannel channel, PeerIdentity clientId, SessionPreferences clientPrefs, byte[] joinNonce,
        int localUdpPort, IPEndPoint? localReflexive = null)
    {
        var (type, body) = channel.Receive();
        if (type == ControlMessageType.Error)
            throw new HandshakeException(RemoteText(body));
        if (type != ControlMessageType.Hello)
            throw new HandshakeException($"expected HELLO from host, got {type}");
        var (hostProtocol, hostNonce) = HandshakeCodec.DecodeChallenge(body);

        // Checked here rather than left to the negotiate below. A build that disagrees about the
        // protocol may also disagree about the message sequence, and naming both versions beats
        // blocking on an identity the peer never intends to send.
        if (hostProtocol != clientId.ProtocolVersion)
            throw new HandshakeException(
                $"protocol mismatch (local v{clientId.ProtocolVersion}, remote v{hostProtocol})");

        // Prove we know the session password (and verify the host does too) before the Welcome flow.
        // Ahead of the negotiate because the host has not told us who it is yet, and because bailing
        // out without sending a proof would leave it blocked waiting for one.
        VerifyPassword(channel, clientPrefs.Password, hostNonce, joinNonce, isHost: false);

        // The host proved itself; NOW it may know who we are. The opening frame carried only the
        // intro (nonce/version/ports) — the identity waits for this moment, so a joiner tricked
        // into dialing a stranger reveals nothing the connection itself did not already.
        channel.Send(ControlMessageType.Hello,
            HandshakeCodec.Encode(clientId, clientPrefs, localUdpPort, joinNonce, localReflexive));

        (type, body) = channel.Receive();
        if (type == ControlMessageType.Error)
            throw new HandshakeException(RemoteText(body));
        if (type != ControlMessageType.Hello)
            throw new HandshakeException($"expected the host's identity after AUTH, got {type}");
        var (hostId, hostPrefs, hostUdpPort, _, _) = HandshakeCodec.Decode(body);

        var result = SessionNegotiator.Negotiate(clientId, hostId, clientPrefs, hostPrefs);
        if (!result.Accepted)
            throw new HandshakeException(result.RejectReason ?? "rejected");

        return hostUdpPort;
    }

    /// <summary>Receive the authoritative WELCOME and state, let the caller apply them, acknowledge
    /// that exact generation, then remain blocked until the host releases the same generation.</summary>
    private static SessionParams ReceiveStartData(
        ControlChannel channel, int hostUdpPort, Action<SessionParams>? beforeReady,
        Func<int, IReadOnlyList<PeerRoute>, MeshTokens, LobbyMeshSample>? measureMesh = null)
    {
        int assignedPort = 0, playerCount = 0, delay = 0;
        SyncMode mode = SyncMode.Lockstep;
        SessionGeneration generation = default;
        IReadOnlyList<PeerRoute> peerRoutes = Array.Empty<PeerRoute>();
        MeshTokens tokens = MeshTokens.None;
        IReadOnlyList<int> vacatedSeats = Array.Empty<int>();
        int checksumInterval = ChecksumCadence.DefaultIntervalFrames;
        byte[]? initialState = null;
        bool haveWelcome = false;
        bool readySent = false;
        SessionParams? session = null;

        while (true)
        {
            var (type, body) = channel.Receive();
            if (type == ControlMessageType.Error)
                throw new HandshakeException(RemoteText(body));
            if (type == ControlMessageType.Ping)
            {
                if (body.Length != 8)
                    throw new HandshakeException("invalid lobby PING body");
                channel.Send(ControlMessageType.Pong, body);
                continue;
            }
            if (type == ControlMessageType.Welcome)
            {
                // A repeat is a lobby restart, not a protocol error: someone else left before GO,
                // the host refilled their seat, and every remaining joiner needs the corrected
                // assignment and routes. Everything derived from the previous WELCOME is discarded
                // — including a READY already acknowledged, since the parameters that acknowledgement
                // stood for have just changed. The state survives, because the host has not stepped
                // the core since it exported it and re-shipping megabytes to prove that would make
                // recovering from a fumbled join cost more than the join.
                try
                {
                    (assignedPort, playerCount, delay, mode, generation, peerRoutes) =
                        HandshakeCodec.DecodeWelcome(body);
                    tokens = HandshakeCodec.DecodeTokens(body);
                    vacatedSeats = HandshakeCodec.DecodeVacatedSeats(body);
                    checksumInterval = HandshakeCodec.DecodeChecksumInterval(body);
                }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
                {
                    throw new HandshakeException("invalid WELCOME: " + ex.Message);
                }
                haveWelcome = true;
                readySent = false;
                session = null;
                continue;
            }
            if (type == ControlMessageType.State)
            {
                if (initialState != null) throw new HandshakeException("host sent STATE more than once");
                if (!StateCompression.TryUnpack(body, ControlMessageCodec.MaxStateBytes, out initialState))
                    throw new HandshakeException("host sent a malformed or oversized initial state");
                continue;
            }
            if (type == ControlMessageType.MeshRtt)
            {
                // WELCOME first, because the routes it carries are the thing being measured.
                if (!haveWelcome)
                    throw new HandshakeException("host asked for a mesh measurement before sending WELCOME");
                RequireGeneration(ControlMessageType.MeshRtt, body, generation);
                var mesh = measureMesh != null
                    ? measureMesh(hostUdpPort, peerRoutes, tokens)
                    : LobbyMeshSample.None;
                channel.Send(ControlMessageType.MeshRtt, ControlMessageCodec.EncodeMeshRtt(
                    generation, mesh.Rtt.MedianMs, mesh.Rtt.HighMs, mesh.MeasuredEdges,
                    mesh.TotalEdges, mesh.SilentPorts));
                continue;
            }
            if (type == ControlMessageType.InputDelay)
            {
                if (!haveWelcome)
                    throw new HandshakeException("host sent an input delay before WELCOME");
                // After READY this peer's driver is already built for a delay; a late change would
                // put it on a different timeline from everyone else, which is a desync, not a tweak.
                if (readySent)
                    throw new HandshakeException("host changed the input delay after READY");
                if (!ControlMessageCodec.TryDecodeInputDelay(body, out var delayGeneration, out int finalDelay))
                    throw new HandshakeException("host sent a malformed input delay");
                if (delayGeneration != generation)
                    throw new HandshakeException(
                        $"input delay generation mismatch: expected {generation}, got {delayGeneration}");
                delay = finalDelay;
                continue;
            }
            if (type == ControlMessageType.Ready)
            {
                if (readySent) throw new HandshakeException("host requested READY more than once");
                if (!haveWelcome) throw new HandshakeException("host requested READY before sending WELCOME");
                if (initialState == null) throw new HandshakeException("host requested READY before sending state");
                RequireGeneration(ControlMessageType.Ready, body, generation);

                session = new SessionParams(mode, delay, localPort: assignedPort, remotePort: 0,
                    remoteUdpPort: hostUdpPort, initialState, generation, playerCount, peerRoutes,
                    tokens, vacatedSeats, checksumInterval);
                beforeReady?.Invoke(session);
                channel.Send(ControlMessageType.Ready, HandshakeCodec.EncodeGeneration(generation));
                readySent = true;
                continue;
            }
            if (type == ControlMessageType.Go)
            {
                if (!readySent || session == null)
                    throw new HandshakeException("host sent GO before the client acknowledged READY");
                RequireGeneration(ControlMessageType.Go, body, generation);
                return session;
            }
            throw new HandshakeException($"unexpected control frame during start: {type}");
        }
    }

    /// <summary>Longest a peer's own words may be before they are cut short.</summary>
    private const int MaxRemoteTextBytes = 400;

    /// <summary>
    /// A peer's own words, made safe to put in a message a person reads and a file keeps.
    ///
    /// An ERROR body is text the far end chose, and it reached the on-screen log and the session
    /// file verbatim and unbounded — up to the 256 KiB a control frame allows, newlines and all.
    /// That is two problems. A peer could write the disk full a quarter-megabyte at a time; and it
    /// could embed newlines to forge log lines indistinguishable from this tool's own, in the file
    /// people are asked to send when something goes wrong.
    /// </summary>
    private static string RemoteText(byte[] body)
    {
        if (body == null || body.Length == 0) return "(no reason given)";
        int take = Math.Min(body.Length, MaxRemoteTextBytes);
        var text = Encoding.UTF8.GetString(body, 0, take);
        var sb = new StringBuilder(text.Length + 1);
        // Every control character, not just the newline: a bare carriage return rewrites a console
        // line, and the rest are noise in a file meant to be read.
        foreach (var c in text) sb.Append(char.IsControl(c) ? ' ' : c);
        if (body.Length > take) sb.Append('…');
        return sb.ToString();
    }

    private static void RequireGeneration(
        ControlMessageType messageType, byte[] body, SessionGeneration expected)
    {
        SessionGeneration actual;
        try
        {
            actual = HandshakeCodec.DecodeGeneration(body);
        }
        catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
        {
            throw new HandshakeException($"invalid {messageType.ToString().ToUpperInvariant()} generation: {ex.Message}");
        }
        if (actual != expected)
            throw new HandshakeException(
                $"{messageType.ToString().ToUpperInvariant()} generation mismatch: expected {expected}, got {actual}");
    }
}
