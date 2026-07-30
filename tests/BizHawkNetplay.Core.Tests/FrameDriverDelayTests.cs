using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Regression cover for two review findings: (1) at an input delay larger than the redundancy
/// window the earliest frames — including frame 0 — used to be evicted before they were ever sent,
/// stalling lockstep at frame 0 forever; and (2) a replaced/torn-down driver must release the
/// rollback savestate ring instead of leaking a ring's worth of state blobs.
/// </summary>
public class FrameDriverDelayTests
{
    /// <summary>Trivial in-memory paired transport: immediate, lossless delivery to the peer.</summary>
    private sealed class Pipe : ITransport
    {
        private readonly Queue<byte[]> _in = new();
        private Pipe _peer = null!;
        public static (Pipe a, Pipe b) Pair() { var a = new Pipe(); var b = new Pipe(); a._peer = b; b._peer = a; return (a, b); }
        public void Send(byte[] datagram) => _peer._in.Enqueue((byte[])datagram.Clone());
        public bool TryReceive(out byte[] datagram)
        {
            if (_in.Count > 0) { datagram = _in.Dequeue(); return true; }
            datagram = null!; return false;
        }
    }

    private sealed class CountingTransport : ITransport
    {
        private readonly Queue<byte[]> _in = new();
        public int Sends { get; private set; }
        public int Pending => _in.Count;
        public void Enqueue(byte[] datagram) => _in.Enqueue(datagram);
        public void Send(byte[] datagram) => Sends++;
        public bool TryReceive(out byte[] datagram)
        {
            if (_in.Count > 0) { datagram = _in.Dequeue(); return true; }
            datagram = null!; return false;
        }
    }

    private static PortInput Btn(bool pressed)
    {
        var arr = new bool[8];
        arr[0] = pressed;
        return new PortInput(arr, []);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(9)]   // > default redundancy 8: the regression case
    [InlineData(20)]  // the UI maximum
    public void Lockstep_AdvancesPastFrameZero_AtAnyDelay(int delay)
    {
        const int Target = 60;
        var (pa, pb) = Pipe.Pair();
        var ea = new FakeEmuAdapter(portCount: 2) { LocalInputScript = f => Btn(f % 2 == 0) };
        var eb = new FakeEmuAdapter(portCount: 2) { LocalInputScript = f => Btn(f % 3 == 0) };
        var da = new FrameDriver(ea, pa, p => new LockstepStrategy(p), localPort: 0, delay: delay);
        var db = new FrameDriver(eb, pb, p => new LockstepStrategy(p), localPort: 1, delay: delay);
        da.Start(); db.Start();

        for (int iter = 0; iter < Target * 20 && (da.CurrentFrame < Target || db.CurrentFrame < Target); iter++)
        {
            if (da.OnPreFrame() == FrameStep.Ran) { ea.AdvanceAppliedFrame(); da.OnPostFrame(); }
            if (db.OnPreFrame() == FrameStep.Ran) { eb.AdvanceAppliedFrame(); db.OnPostFrame(); }
        }

        Assert.True(da.CurrentFrame >= Target, $"A stalled at {da.CurrentFrame}/{Target} (delay {delay})");
        Assert.True(db.CurrentFrame >= Target, $"B stalled at {db.CurrentFrame}/{Target} (delay {delay})");
    }

    [Fact]
    public void FewerPlayersThanPorts_RunsInSync()
    {
        // A 4-controller core (e.g. N64) played 2-player: the driver networks only the 2 active
        // ports; the core's other ports are simply never set (read neutral). Both peers must still
        // advance and stay byte-identical.
        const int Target = 60, Ports = 4, Players = 2;
        var (pa, pb) = Pipe.Pair();
        var ea = new FakeEmuAdapter(portCount: Ports) { LocalInputScript = f => Btn(f % 2 == 0) };
        var eb = new FakeEmuAdapter(portCount: Ports) { LocalInputScript = f => Btn(f % 3 == 0) };
        var da = new FrameDriver(ea, pa, p => new LockstepStrategy(p), localPort: 0, delay: 2, portCount: Players);
        var db = new FrameDriver(eb, pb, p => new LockstepStrategy(p), localPort: 1, delay: 2, portCount: Players);
        da.Start(); db.Start();

        for (int iter = 0; iter < Target * 20 && (da.CurrentFrame < Target || db.CurrentFrame < Target); iter++)
        {
            if (da.OnPreFrame() == FrameStep.Ran) { ea.AdvanceAppliedFrame(); da.OnPostFrame(); }
            if (db.OnPreFrame() == FrameStep.Ran) { eb.AdvanceAppliedFrame(); db.OnPostFrame(); }
        }

        Assert.True(da.CurrentFrame >= Target && db.CurrentFrame >= Target, "a 2-of-4-player session stalled");
        Assert.Equal(Players, da.LastAppliedInputs!.Ports.Length); // only the active ports are networked
        Assert.Equal(ea.HashMainMemory(), eb.HashMainMemory());    // and both stayed in sync
    }

    [Fact]
    public void SplitPump_DoesNotDuplicateFreshInputAndBoundsReceiveWork()
    {
        var transport = new CountingTransport();
        var emu = new FakeEmuAdapter(portCount: 2);
        var driver = new FrameDriver(emu, transport, p => new LockstepStrategy(p), localPort: 0, delay: 2);

        driver.Start();
        Assert.Equal(1, transport.Sends); // neutral seed window
        driver.PumpNetwork();
        Assert.Equal(1, transport.Sends); // pump is drain-only
        driver.CaptureLocalInput();
        Assert.Equal(2, transport.Sends); // exactly one packet for the fresh stamp
        driver.PumpNetwork();
        driver.ResendLocalInputIfDue();
        Assert.Equal(2, transport.Sends); // immediate 2ms-style retry is rate-limited

        for (int i = 0; i < 200; i++) transport.Enqueue([0xFF]);
        driver.PumpNetwork();
        Assert.Equal(128, driver.LastPacketsDrained);
        Assert.Equal(72, transport.Pending);
    }

    [Fact]
    public void DisposingDriver_ReleasesRollbackRing()
    {
        const int MaxRollback = 16;
        var (pa, pb) = Pipe.Pair();
        var ea = new FakeEmuAdapter(portCount: 2) { LocalInputScript = f => Btn(f % 2 == 0) };
        var eb = new FakeEmuAdapter(portCount: 2) { LocalInputScript = f => Btn(f % 3 == 0) };
        var da = new FrameDriver(ea, pa, p => new RollbackStrategy(p, ea, 0, MaxRollback),
            localPort: 0, delay: 2, redundancy: 8, rollbackWindow: MaxRollback);
        var db = new FrameDriver(eb, pb, p => new RollbackStrategy(p, eb, 1, MaxRollback),
            localPort: 1, delay: 2, redundancy: 8, rollbackWindow: MaxRollback);
        da.Start(); db.Start();

        for (int i = 0; i < 60; i++)
        {
            if (da.OnPreFrame() == FrameStep.Ran) { ea.AdvanceAppliedFrame(); da.OnPostFrame(); }
            if (db.OnPreFrame() == FrameStep.Ran) { eb.AdvanceAppliedFrame(); db.OnPostFrame(); }
        }

        Assert.True(ea.LiveStates.Count > 0, "rollback should be holding states before dispose");
        da.Dispose();
        db.Dispose();
        Assert.Equal(0, ea.LiveStates.Count);
        Assert.Equal(0, eb.LiveStates.Count);
    }
}
