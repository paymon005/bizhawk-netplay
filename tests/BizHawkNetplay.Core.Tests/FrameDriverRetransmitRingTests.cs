using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Cover for the local-input retransmit ring, and specifically for what happens when it wraps.
///
/// The send path keeps the local port's recent payloads in one contiguous byte ring indexed by
/// frame, and builds each datagram by copying a run of slots out of it. A run that straddles the
/// wrap needs two copies rather than one, and every existing driver test stops around frame 60 —
/// well short of the 240-frame ring — so the wrap was reachable only in real play, where a wrong
/// slot means one peer silently applies another frame's buttons: a desync with no error attached.
///
/// The oracle is arranged so the check is exact rather than approximate: with the fake's 8-button,
/// no-axis layout a port serializes to exactly one byte, LSB-first, so a frame whose pressed
/// buttons are the bits of <c>n % 256</c> serializes to the byte <c>(byte)n</c>.
/// </summary>
public class FrameDriverRetransmitRingTests
{
    /// <summary>Frames to run. Comfortably past the 240-frame ring, so the window wraps repeatedly.</summary>
    private const int PastTheWrap = 320;

    private const int Delay = 2;

    /// <summary>Buttons spelling out the low 8 bits of the number.</summary>
    private static PortInput FrameStamped(int n)
    {
        var arr = new bool[8];
        for (int i = 0; i < 8; i++) arr[i] = ((n >> i) & 1) != 0;
        return new PortInput(arr, []);
    }

    /// <summary>
    /// The byte frame <paramref name="frame"/> must carry.
    ///
    /// Input read at frame N applies at N + delay, so the payload stamped for frame N is the script's
    /// value at N - delay. Start seeds the first `delay` frames neutral, before any input is read.
    /// </summary>
    private static byte Expected(int frame) => frame < Delay ? (byte)0 : (byte)(frame - Delay);

    /// <summary>Paired transport that also records everything the local side sent, and allows a
    /// datagram to be pushed into the local side's inbound queue directly.</summary>
    private sealed class CapturingPipe : ITransport
    {
        private readonly Queue<byte[]> _in = new();
        private CapturingPipe _peer = null!;
        public List<byte[]> Sent { get; } = new();

        public static (CapturingPipe a, CapturingPipe b) Pair()
        {
            var a = new CapturingPipe();
            var b = new CapturingPipe();
            a._peer = b; b._peer = a;
            return (a, b);
        }

        public void Send(byte[] datagram)
        {
            Sent.Add((byte[])datagram.Clone());
            _peer._in.Enqueue((byte[])datagram.Clone());
        }

        /// <summary>Deliver a datagram to THIS side, as if a peer had sent it.</summary>
        public void Inject(byte[] datagram) => _in.Enqueue(datagram);

        public bool TryReceive(out byte[] datagram)
        {
            if (_in.Count > 0) { datagram = _in.Dequeue(); return true; }
            datagram = null!; return false;
        }
    }

    private static (CapturingPipe pa, FakeEmuAdapter ea, FrameDriver da, FrameDriver db) RunPastWrap()
    {
        var (pa, pb) = CapturingPipe.Pair();
        var ea = new FakeEmuAdapter(portCount: 2) { LocalInputScript = FrameStamped };
        var eb = new FakeEmuAdapter(portCount: 2) { LocalInputScript = FrameStamped };
        var da = new FrameDriver(ea, pa, p => new LockstepStrategy(p), localPort: 0, delay: Delay);
        var db = new FrameDriver(eb, pb, p => new LockstepStrategy(p), localPort: 1, delay: Delay);
        da.Start(); db.Start();

        for (int iter = 0; iter < PastTheWrap * 20
             && (da.CurrentFrame < PastTheWrap || db.CurrentFrame < PastTheWrap); iter++)
        {
            if (da.OnPreFrame() == FrameStep.Ran) { ea.AdvanceAppliedFrame(); da.OnPostFrame(); }
            if (db.OnPreFrame() == FrameStep.Ran) { eb.AdvanceAppliedFrame(); db.OnPostFrame(); }
        }

        Assert.True(da.CurrentFrame >= PastTheWrap, $"A stalled at {da.CurrentFrame}");
        Assert.True(db.CurrentFrame >= PastTheWrap, $"B stalled at {db.CurrentFrame}");
        return (pa, ea, da, db);
    }

    /// <summary>The codec a peer would use to read what this session sends.</summary>
    private static InputPacketCodec ReaderCodec(FrameDriver driver) =>
        new(new[] { 1, 1 }, driver.Generation);

    [Fact]
    public void EveryDatagramCarriesTheFramesItClaims_AcrossTheRingWrap()
    {
        var (pa, _, da, _) = RunPastWrap();
        var codec = ReaderCodec(da);

        int checkedFrames = 0, highestFrameSeen = -1;
        foreach (var datagram in pa.Sent)
        {
            // Gap requests and anything else are not input windows; only input is under test here.
            if (!codec.TryDecodeInputWindow(datagram, out var window)) continue;
            Assert.Equal(0, window.Port);
            for (int i = 0; i < window.Count; i++)
            {
                int frame = window.BaseFrame + i;
                byte payload = datagram[window.OffsetOf(i)];
                Assert.True(Expected(frame) == payload,
                    $"frame {frame} was sent carrying {payload} instead of {Expected(frame)} " +
                    $"(datagram base {window.BaseFrame}, count {window.Count})");
                if (frame > highestFrameSeen) highestFrameSeen = frame;
                checkedFrames++;
            }
        }

        // Guard against the assertions above being vacuous: the run has to have actually reached
        // past the wrap, and each frame has to have been sent redundantly as the design intends.
        Assert.True(highestFrameSeen > 240, $"never reached the wrap (highest frame sent {highestFrameSeen})");
        Assert.True(checkedFrames > PastTheWrap, $"only {checkedFrames} frame-payloads were sent");
    }

    [Fact]
    public void GapRequestIsServedWithTheRightFrames_AcrossTheRingWrap()
    {
        var (pa, ea, da, _) = RunPastWrap();
        var codec = ReaderCodec(da);

        // Ask for a run that starts before the wrap point and continues past it, which is exactly
        // the case the two-copy path in SendFrames exists for.
        const int RequestFrom = 236;
        pa.Sent.Clear();
        pa.Inject(codec.EncodeRequest(0, RequestFrom));
        if (da.OnPreFrame() == FrameStep.Ran) { ea.AdvanceAppliedFrame(); da.OnPostFrame(); }

        InputPacketCodec.InputWindow served = default;
        byte[]? servedDatagram = null;
        foreach (var datagram in pa.Sent)
        {
            if (!codec.TryDecodeInputWindow(datagram, out var window)) continue;
            if (window.BaseFrame != RequestFrom) continue;
            served = window;
            servedDatagram = datagram;
            break;
        }

        Assert.True(servedDatagram != null, $"no retransmission starting at frame {RequestFrom} was sent");
        Assert.True(served.Count > 1, $"served only {served.Count} frame(s); the run should span the wrap");
        for (int i = 0; i < served.Count; i++)
        {
            int frame = RequestFrom + i;
            Assert.True(Expected(frame) == servedDatagram![served.OffsetOf(i)],
                $"retransmitted frame {frame} carried {servedDatagram[served.OffsetOf(i)]} " +
                $"instead of {Expected(frame)}");
        }
    }

    /// <summary>
    /// The serve budget must REOPEN. It is metered — a request is small and its answer is a full
    /// window, so a peer stuck in a request loop could otherwise be turned into an amplifier — but
    /// the meter is a rate, not a lifetime quota, and it was behaving as a quota.
    ///
    /// The window-reset test compared against a <c>long.MinValue</c> sentinel, so
    /// <c>now - _serveWindowStartMs</c> overflowed to a negative number and the reset never fired.
    /// After the eighth serve of a session, this peer refused every gap request for the rest of it.
    /// The cost is not a slow recovery, it is a permanent one: a peer whose loss burst outran its
    /// redundant window can only be repaired by these retransmissions, so both sides sit at their
    /// prediction caps forever — one asking, one refusing. That freeze reproduced end-to-end, but
    /// only at a realistic tick-to-wall-clock ratio, which is why it hid.
    ///
    /// Deliberately asks for more than the per-window budget, spread across several windows.
    /// </summary>
    [Fact]
    public void TheServeBudgetReopensAfterItsWindow()
    {
        var (pa, ea, da, _) = RunPastWrap();
        var codec = ReaderCodec(da);

        const int RequestFrom = 300;
        const int Rounds = 24;              // 3x the per-window budget of 8
        int served = 0;
        for (int round = 0; round < Rounds; round++)
        {
            pa.Sent.Clear();
            pa.Inject(codec.EncodeRequest(0, RequestFrom));
            if (da.OnPreFrame() == FrameStep.Ran) { ea.AdvanceAppliedFrame(); da.OnPostFrame(); }
            foreach (var datagram in pa.Sent)
                if (codec.TryDecodeInputWindow(datagram, out var w) && w.BaseFrame == RequestFrom)
                { served++; break; }
            // Cross a window boundary, so each round is entitled to its own budget rather than
            // sharing one. The metering is deliberately kept — this asks that it recover, not
            // that it be absent.
            System.Threading.Thread.Sleep(60);
        }

        Assert.True(served > 8,
            $"only {served} of {Rounds} requests were answered — the serve budget never reopened, " +
            "so a peer needing more retransmission than one window's worth can never recover");
    }

    [Fact]
    public void FramesOlderThanTheRingAreNotServed()
    {
        var (pa, ea, da, _) = RunPastWrap();
        var codec = ReaderCodec(da);

        // Frame 1 is long gone: the ring holds 240 frames and the session is past 320. Answering
        // from a slot that has since been overwritten would hand the peer another frame's input
        // under frame 1's number — a silent desync, which is worse than not answering at all.
        pa.Sent.Clear();
        pa.Inject(codec.EncodeRequest(0, 1));
        if (da.OnPreFrame() == FrameStep.Ran) { ea.AdvanceAppliedFrame(); da.OnPostFrame(); }

        foreach (var datagram in pa.Sent)
        {
            if (!codec.TryDecodeInputWindow(datagram, out var window)) continue;
            Assert.True(window.BaseFrame != 1,
                "a frame that has aged out of the retransmit ring was served anyway");
        }
    }
}
