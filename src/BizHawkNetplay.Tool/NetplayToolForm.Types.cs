using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Tool
{
    public sealed partial class NetplayToolForm
    {
        // Private nested types shared across the partials. They live here rather than among the
        // fields they used to be buried in, which made both harder to find.

        /// <summary>One control link to a peer. Host: one per joiner. Joiner: one (the host).</summary>
        private sealed class PeerLink
        {
            public TcpClient Tcp = null!;      // null for a punched link (control rides the mesh socket)
            public System.IO.Stream? ControlStream; // punched links: the reliable stream under Control
            public ControlChannel Control = null!;
            public int RemotePort;            // the controller port this peer owns (host peer = 0)
            public Handshake.JoinerGreeting? Greeting; // what this peer asked for; lobby-only
            public bool HoldsState;           // has already been sent the initial savestate (lobby-only)
            public IPEndPoint UdpEndpoint = null!;      // LAN/observed endpoint (from TCP source + reported port)
            public IPEndPoint? ReflexiveEndpoint;       // public (STUN) endpoint, for NAT traversal; null until reported
            public Thread? Reader;
            public Thread? Writer;
            public readonly ConcurrentQueue<OutboundMessage> Outbound = new ConcurrentQueue<OutboundMessage>();
            public readonly AutoResetEvent OutboundSignal = new AutoResetEvent(false);
            public volatile bool WriterRunning;
            public long QueuedBytes;
            public int Attempt;               // connection-attempt token for stale reader/writer callbacks
            public double PingMs = -1;        // guarded by _pingLock
            public int PingCount;             // guarded by _pingLock
            public long LastRecvTicks;        // Stopwatch ticks of the last message from this peer (Interlocked)
            public volatile bool ResyncReceiving; // large inbound state frame is allowed to exceed ping timeout
            public int ReceivingResyncEpoch;      // expected generation while ResyncReceiving is true
            public int ReceivingResyncBytes;      // declared state size, checked against the completed frame
            // Parameters the announced state must be rebuilt under, taken from its ResyncBegin.
            public int ReceivingResyncDelay;
            public SyncMode ReceivingResyncMode;
            public bool ReceivingResyncIsSettingsChange;
            public long ResyncReceiveDeadlineTicks; // bounds BEGIN-without-a-complete-state stalls
            public long TimeoutGraceUntilTicks;   // we sent this peer a whole state: its reader is busy consuming
                                                  // that frame and can't pong until it lands (Interlocked)
            // Frame-advantage exchange (ControlMessageType.Pacing), guarded by _pingLock. Advantage
            // measured locally is inflated by one-way latency; subtracting the peer's own measurement
            // cancels that term, which is why both numbers have to travel.
            public int LocalAdvantage;            // our frame minus theirs, as of their last report
            public int RemoteAdvantage;           // the same quantity as they measured it
            public bool AdvantageKnown;           // false until a peer on a build that reports has answered
            public int PacingSendSequence;         // our monotonically increasing wire sample id
            public int LastReceivedPacingSequence; // peer sample most recently incorporated
            public int AwaitingAppliedEpoch;       // host barrier: non-zero until this peer applies that epoch
            public long AppliedDeadlineTicks;      // bounds a peer that stays alive but never applies state
            public bool DirectLogged;         // one-time flag: logged that this peer's direct UDP path opened
            public string Label = "";
        }

        private sealed class OutboundMessage
        {
            public OutboundMessage(ControlMessageType type, byte[] body, Action<bool>? completed)
            {
                Type = type; Body = body; Completed = completed;
            }
            public ControlMessageType Type { get; }
            public byte[] Body { get; }
            public Action<bool>? Completed { get; }
        }

        /// <summary>
        /// A socket receive timeout applies to each individual read, so a peer can otherwise keep a
        /// greeting alive forever by sending one byte just before every timeout. This timer bounds the
        /// whole authentication phase and closes the socket to unblock a pending read at the deadline.
        /// </summary>
        private sealed class AbsoluteSocketDeadline : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly System.Threading.Timer _timer;
            // 0 = armed, 1 = completed/disarmed, 2 = expired and owns closing the socket.
            private int _state;

            public AbsoluteSocketDeadline(TcpClient tcp, int timeoutMs)
            {
                _tcp = tcp;
                _timer = new System.Threading.Timer(_ => Expire(), null, timeoutMs, Timeout.Infinite);
            }

            public bool Expired => Volatile.Read(ref _state) == 2;

            public bool TryComplete()
            {
                int previous = Interlocked.CompareExchange(ref _state, 1, 0);
                if (previous == 0)
                    try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                return previous != 2;
            }

            private void Expire()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
                try { _tcp.Close(); } catch { }
            }

            public void Dispose()
            {
                TryComplete();
                try { _timer.Dispose(); } catch { }
            }
        }

        private volatile TcpClient? _greetingTcp; // a joiner we've accepted but are still greeting, so teardown can abort it
        // Attempt tokens + tracked handshake sockets live in Core (ConnectionLifecycle), which
        // atomically closes the accept-vs-teardown registration race.
        private readonly ConnectionLifecycle _lifecycle = new ConnectionLifecycle();
        private const int HandshakeReceiveTimeoutMs = 15000; // a joiner that connects but never HELLOs can't wedge the host
    }
}
