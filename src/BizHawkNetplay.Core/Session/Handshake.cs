using System;
using System.Text;

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
            int remoteUdpPort, byte[]? initialState, int playerCount = 2)
        {
            Mode = mode;
            InputDelay = inputDelay;
            LocalPort = localPort;
            RemotePort = remotePort;
            RemoteUdpPort = remoteUdpPort;
            InitialState = initialState;
            PlayerCount = playerCount;
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
            byte[] hostState, int localUdpPort)
        {
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(hostId, hostPrefs, localUdpPort));

            var (type, body) = channel.Receive();
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from joiner, got {type}");
            var (clientId, clientPrefs, clientUdpPort) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(hostId, clientId, hostPrefs, clientPrefs);
            if (!result.Accepted)
            {
                channel.Send(ControlMessageType.Error, Encoding.UTF8.GetBytes(result.RejectReason ?? "rejected"));
                throw new HandshakeException(result.RejectReason ?? "rejected");
            }

            // Host owns port 0; transfer the reference state, then release the synchronized start.
            channel.Send(ControlMessageType.State, hostState ?? Array.Empty<byte>());
            channel.Send(ControlMessageType.Start, Array.Empty<byte>());

            return new SessionParams(result.Mode, result.InputDelay, localPort: 0, remotePort: 1,
                remoteUdpPort: clientUdpPort, initialState: null);
        }

        /// <summary>Client side: join a host, receive initial state, agree on parameters.</summary>
        public static SessionParams RunClient(
            ControlChannel channel, PeerIdentity clientId, SessionPreferences clientPrefs, int localUdpPort)
        {
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(clientId, clientPrefs, localUdpPort));

            // First frame back is the host's HELLO (or an early ERROR).
            var (type, body) = channel.Receive();
            if (type == ControlMessageType.Error)
                throw new HandshakeException(Encoding.UTF8.GetString(body));
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from host, got {type}");
            var (hostId, hostPrefs, hostUdpPort) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(clientId, hostId, clientPrefs, hostPrefs);
            if (!result.Accepted)
                throw new HandshakeException(result.RejectReason ?? "rejected");

            byte[]? initialState = null;
            while (true)
            {
                var (t, b) = channel.Receive();
                if (t == ControlMessageType.Error) throw new HandshakeException(Encoding.UTF8.GetString(b));
                if (t == ControlMessageType.State) { initialState = b; continue; }
                if (t == ControlMessageType.Start) break;
                throw new HandshakeException($"unexpected control frame during start: {t}");
            }
            if (initialState == null)
                throw new HandshakeException("host never sent the initial state");

            // Client owns port 1.
            return new SessionParams(result.Mode, result.InputDelay, localPort: 1, remotePort: 0,
                remoteUdpPort: hostUdpPort, initialState);
        }

        // ---- N-player (host-relay) handshake ---------------------------------------------
        // The 3–4 player flow splits the host side into per-joiner steps the caller orchestrates:
        // greet every joiner first (so all identities are validated and the authoritative input
        // delay = max over everyone is known), then send each a Welcome carrying its assigned port,
        // the player count and the final delay, followed by the shared initial state and Start.

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
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(hostId, hostPrefs, hostUdpPort));

            var (type, body) = channel.Receive();
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from joiner, got {type}");
            var (joinerId, joinerPrefs, joinerUdpPort) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(hostId, joinerId, hostPrefs, joinerPrefs);
            if (!result.Accepted)
            {
                channel.Send(ControlMessageType.Error, Encoding.UTF8.GetBytes(result.RejectReason ?? "rejected"));
                throw new HandshakeException(result.RejectReason ?? "rejected");
            }
            return new JoinerGreeting(joinerId, joinerPrefs, joinerUdpPort);
        }

        /// <summary>Host, per joiner: send the assignment (port, player count, final delay) + state + Start.</summary>
        public static void HostSendWelcome(
            ControlChannel channel, int assignedPort, int playerCount, int inputDelay, SyncMode mode, byte[] state)
        {
            channel.Send(ControlMessageType.Welcome, HandshakeCodec.EncodeWelcome(assignedPort, playerCount, inputDelay, mode));
            channel.Send(ControlMessageType.State, state ?? Array.Empty<byte>());
            channel.Send(ControlMessageType.Start, Array.Empty<byte>());
        }

        /// <summary>
        /// Client (N-player): send HELLO, validate the host's HELLO, then take the authoritative
        /// assignment from WELCOME (port/players/delay), receive the initial state, and wait for Start.
        /// </summary>
        public static SessionParams RunClientMulti(
            ControlChannel channel, PeerIdentity clientId, SessionPreferences clientPrefs, int localUdpPort)
        {
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(clientId, clientPrefs, localUdpPort));

            var (type, body) = channel.Receive();
            if (type == ControlMessageType.Error)
                throw new HandshakeException(Encoding.UTF8.GetString(body));
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from host, got {type}");
            var (hostId, hostPrefs, hostUdpPort) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(clientId, hostId, clientPrefs, hostPrefs);
            if (!result.Accepted)
                throw new HandshakeException(result.RejectReason ?? "rejected");

            int assignedPort = 1, playerCount = 2, delay = result.InputDelay;
            SyncMode mode = result.Mode;
            byte[]? initialState = null;
            while (true)
            {
                var (t, b) = channel.Receive();
                if (t == ControlMessageType.Error) throw new HandshakeException(Encoding.UTF8.GetString(b));
                if (t == ControlMessageType.Welcome)
                {
                    (assignedPort, playerCount, delay, mode) = HandshakeCodec.DecodeWelcome(b);
                    continue;
                }
                if (t == ControlMessageType.State) { initialState = b; continue; }
                if (t == ControlMessageType.Start) break;
                throw new HandshakeException($"unexpected control frame during start: {t}");
            }
            if (initialState == null)
                throw new HandshakeException("host never sent the initial state");

            return new SessionParams(mode, delay, localPort: assignedPort, remotePort: 0,
                remoteUdpPort: hostUdpPort, initialState, playerCount);
        }
    }
}
