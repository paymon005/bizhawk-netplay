using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Session
{
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
            int playerCount = 2, IReadOnlyList<PeerRoute>? peerRoutes = null)
        {
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
        /// <summary>Host side: accept a joiner, transfer initial state, agree on parameters.</summary>
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
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(hostId, hostPrefs, localUdpPort, hostNonce));

            var (type, body) = channel.Receive();
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from joiner, got {type}");
            var (clientId, clientPrefs, clientUdpPort, joinNonce) = HandshakeCodec.Decode(body);

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

            // Verify the session password (nonce challenge-response) before transferring any state.
            VerifyPassword(channel, hostPrefs.Password, hostNonce, joinNonce, isHost: true);

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

        /// <summary>Client side: join a host, receive initial state, agree on parameters.</summary>
        public static SessionParams RunClient(
            ControlChannel channel, PeerIdentity clientId, SessionPreferences clientPrefs, int localUdpPort,
            Action<SessionParams>? beforeReady = null, Action? afterGreet = null)
        {
            var joinNonce = SessionAuth.NewNonce();
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(clientId, clientPrefs, localUdpPort, joinNonce));

            // First frame back is the host's HELLO (or an early ERROR).
            var (type, body) = channel.Receive();
            if (type == ControlMessageType.Error)
                throw new HandshakeException(Encoding.UTF8.GetString(body));
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from host, got {type}");
            var (hostId, hostPrefs, hostUdpPort, hostNonce) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(clientId, hostId, clientPrefs, hostPrefs);
            if (!result.Accepted)
                throw new HandshakeException(result.RejectReason ?? "rejected");

            // Prove we know the session password (and verify the host does too) before taking any state.
            VerifyPassword(channel, clientPrefs.Password, hostNonce, joinNonce, isHost: false);
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
            string myProof = SessionAuth.Proof(password, myRole, hostNonce, joinNonce);
            string peerExpected = SessionAuth.Proof(password, peerRole, hostNonce, joinNonce);

            if (isHost)
            {
                var (t, b) = channel.Receive();
                if (t == ControlMessageType.Error) throw new HandshakeException(Encoding.UTF8.GetString(b));
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
                if (t == ControlMessageType.Error) throw new HandshakeException(Encoding.UTF8.GetString(b));
                if (t != ControlMessageType.Auth) throw new HandshakeException($"expected AUTH from host, got {t}");
                if (!SessionAuth.FixedTimeEquals(Encoding.UTF8.GetString(b), peerExpected))
                    throw new HandshakeException("session password mismatch (could not verify the host)");
            }
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
            public JoinerGreeting(PeerIdentity id, SessionPreferences prefs, int udpPort)
            {
                Id = id; Prefs = prefs; UdpPort = udpPort;
            }
            public PeerIdentity Id { get; }
            public SessionPreferences Prefs { get; }
            public int UdpPort { get; }
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
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(hostId, hostPrefs, hostUdpPort, hostNonce));

            var (type, body) = channel.Receive();
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from joiner, got {type}");
            var (joinerId, joinerPrefs, joinerUdpPort, joinNonce) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(hostId, joinerId, hostPrefs, joinerPrefs);
            if (!result.Accepted)
            {
                channel.Send(ControlMessageType.Error, Encoding.UTF8.GetBytes(result.RejectReason ?? "rejected"));
                throw new HandshakeException(result.RejectReason ?? "rejected");
            }

            // Verify the session password during the greet, so a wrong-password joiner is refused before
            // the host commits it a port / includes it in the delay + mode decisions.
            VerifyPassword(channel, hostPrefs.Password, hostNonce, joinNonce, isHost: true);

            return new JoinerGreeting(joinerId, joinerPrefs, joinerUdpPort);
        }

        /// <summary>
        /// Measure the settled round-trip time of an authenticated lobby control link before WELCOME.
        /// The client-side start loop echoes these probes while it waits. Median filtering keeps one
        /// scheduler/TCP spike from permanently adding another frame of input latency.
        /// </summary>
        public static double MeasureLobbyRoundTrip(ControlChannel channel, int samples = 5)
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
                    throw new HandshakeException(Encoding.UTF8.GetString(reply));
                if (type != ControlMessageType.Pong || reply.Length != 8
                    || BitConverter.ToInt64(reply, 0) != token)
                    throw new HandshakeException($"expected lobby PONG from joiner, got {type}");
                measured[i] = timer.Elapsed.TotalMilliseconds;
            }

            Array.Sort(measured);
            int middle = measured.Length / 2;
            return measured.Length % 2 == 0
                ? (measured[middle - 1] + measured[middle]) / 2.0
                : measured[middle];
        }

        /// <summary>
        /// Host, per joiner: send the assignment, generation, grouped routes for every OTHER joiner,
        /// and initial state, then request an apply-complete READY acknowledgement.
        /// </summary>
        public static void HostSendWelcome(
            ControlChannel channel, int assignedPort, int playerCount, int inputDelay, SyncMode mode, byte[] state,
            SessionGeneration generation, IEnumerable<PeerRoute>? peerRoutes = null)
        {
            channel.Send(ControlMessageType.Welcome,
                HandshakeCodec.EncodeWelcome(assignedPort, playerCount, inputDelay, mode, generation, peerRoutes));
            channel.Send(ControlMessageType.State, state ?? Array.Empty<byte>());
            channel.Send(ControlMessageType.Ready, HandshakeCodec.EncodeGeneration(generation));
        }

        /// <summary>Host side of the apply barrier: wait until this joiner imported the state and rebuilt
        /// its driver for <paramref name="generation"/>.</summary>
        public static void HostWaitReady(ControlChannel channel, SessionGeneration generation)
        {
            var (type, body) = channel.Receive();
            if (type == ControlMessageType.Error)
                throw new HandshakeException(Encoding.UTF8.GetString(body));
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
            Action<SessionParams>? beforeReady = null, Action? afterGreet = null)
        {
            var joinNonce = SessionAuth.NewNonce();
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(clientId, clientPrefs, localUdpPort, joinNonce));

            var (type, body) = channel.Receive();
            if (type == ControlMessageType.Error)
                throw new HandshakeException(Encoding.UTF8.GetString(body));
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from host, got {type}");
            var (hostId, hostPrefs, hostUdpPort, hostNonce) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(clientId, hostId, clientPrefs, hostPrefs);
            if (!result.Accepted)
                throw new HandshakeException(result.RejectReason ?? "rejected");

            // Prove we know the session password (and verify the host does too) before the Welcome flow.
            VerifyPassword(channel, clientPrefs.Password, hostNonce, joinNonce, isHost: false);
            afterGreet?.Invoke();

            return ReceiveStartData(channel, hostUdpPort, beforeReady);
        }

        /// <summary>Receive the authoritative WELCOME and state, let the caller apply them, acknowledge
        /// that exact generation, then remain blocked until the host releases the same generation.</summary>
        private static SessionParams ReceiveStartData(
            ControlChannel channel, int hostUdpPort, Action<SessionParams>? beforeReady)
        {
            int assignedPort = 0, playerCount = 0, delay = 0;
            SyncMode mode = SyncMode.Lockstep;
            SessionGeneration generation = default;
            IReadOnlyList<PeerRoute> peerRoutes = Array.Empty<PeerRoute>();
            byte[]? initialState = null;
            bool haveWelcome = false;
            bool readySent = false;
            SessionParams? session = null;

            while (true)
            {
                var (type, body) = channel.Receive();
                if (type == ControlMessageType.Error)
                    throw new HandshakeException(Encoding.UTF8.GetString(body));
                if (type == ControlMessageType.Ping)
                {
                    if (body.Length != 8)
                        throw new HandshakeException("invalid lobby PING body");
                    channel.Send(ControlMessageType.Pong, body);
                    continue;
                }
                if (type == ControlMessageType.Welcome)
                {
                    if (haveWelcome) throw new HandshakeException("host sent WELCOME more than once");
                    try
                    {
                        (assignedPort, playerCount, delay, mode, generation, peerRoutes) =
                            HandshakeCodec.DecodeWelcome(body);
                    }
                    catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
                    {
                        throw new HandshakeException("invalid WELCOME: " + ex.Message);
                    }
                    haveWelcome = true;
                    continue;
                }
                if (type == ControlMessageType.State)
                {
                    if (initialState != null) throw new HandshakeException("host sent STATE more than once");
                    initialState = body;
                    continue;
                }
                if (type == ControlMessageType.Ready)
                {
                    if (readySent) throw new HandshakeException("host requested READY more than once");
                    if (!haveWelcome) throw new HandshakeException("host requested READY before sending WELCOME");
                    if (initialState == null) throw new HandshakeException("host requested READY before sending state");
                    RequireGeneration(ControlMessageType.Ready, body, generation);

                    session = new SessionParams(mode, delay, localPort: assignedPort, remotePort: 0,
                        remoteUdpPort: hostUdpPort, initialState, generation, playerCount, peerRoutes);
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
}
