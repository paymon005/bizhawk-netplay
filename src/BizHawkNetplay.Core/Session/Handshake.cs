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
        public SessionParams(SyncMode mode, int inputDelay, int localPort, int remotePort, byte[]? initialState)
        {
            Mode = mode;
            InputDelay = inputDelay;
            LocalPort = localPort;
            RemotePort = remotePort;
            InitialState = initialState;
        }

        public SyncMode Mode { get; }
        public int InputDelay { get; }
        public int LocalPort { get; }
        public int RemotePort { get; }

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
            ControlChannel channel, PeerIdentity hostId, SessionPreferences hostPrefs, byte[] hostState)
        {
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(hostId, hostPrefs));

            var (type, body) = channel.Receive();
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from joiner, got {type}");
            var (clientId, clientPrefs) = HandshakeCodec.Decode(body);

            var result = SessionNegotiator.Negotiate(hostId, clientId, hostPrefs, clientPrefs);
            if (!result.Accepted)
            {
                channel.Send(ControlMessageType.Error, Encoding.UTF8.GetBytes(result.RejectReason ?? "rejected"));
                throw new HandshakeException(result.RejectReason ?? "rejected");
            }

            // Host owns port 0; transfer the reference state, then release the synchronized start.
            channel.Send(ControlMessageType.State, hostState ?? Array.Empty<byte>());
            channel.Send(ControlMessageType.Start, Array.Empty<byte>());

            return new SessionParams(result.Mode, result.InputDelay, localPort: 0, remotePort: 1, initialState: null);
        }

        /// <summary>Client side: join a host, receive initial state, agree on parameters.</summary>
        public static SessionParams RunClient(
            ControlChannel channel, PeerIdentity clientId, SessionPreferences clientPrefs)
        {
            channel.Send(ControlMessageType.Hello, HandshakeCodec.Encode(clientId, clientPrefs));

            // First frame back is the host's HELLO (or an early ERROR).
            var (type, body) = channel.Receive();
            if (type == ControlMessageType.Error)
                throw new HandshakeException(Encoding.UTF8.GetString(body));
            if (type != ControlMessageType.Hello)
                throw new HandshakeException($"expected HELLO from host, got {type}");
            var (hostId, hostPrefs) = HandshakeCodec.Decode(body);

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
            return new SessionParams(result.Mode, result.InputDelay, localPort: 1, remotePort: 0, initialState);
        }
    }
}
