using System;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// End-to-end lockstep over two fake cores wired by a loopback transport — no sockets, no
/// EmuHawk. Exercises the whole M1 sync path: delay-stamping, redundant sends, drain, frontier
/// gating, and merged-input application.
///
/// The bedrock invariant is <see cref="AssertNoSilentDesync"/>: across every frame both
/// instances have advanced, they must have applied byte-identical inputs. Lockstep never
/// applies an unconfirmed input (it stalls), so this holds under any amount of loss — the
/// worst loss can do is cost liveness, never correctness.
/// </summary>
public class LockstepIntegrationTests
{
    private const int Delay = 2;
    private const int Redundancy = 8; // >= 2*Delay+1, so the window covers the worst-case gap

    private static PortInput Btn(bool pressed)
    {
        var b = new bool[8];
        b[0] = pressed;
        return new PortInput(b, []);
    }

    private sealed class Instance
    {
        public FakeEmuAdapter Emu = null!;
        public FrameDriver Driver = null!;
        public int Stalls;

        public void Step()
        {
            if (Driver.OnPreFrame() == FrameStep.Ran)
            {
                Emu.AdvanceAppliedFrame();
                Driver.OnPostFrame();
            }
            else Stalls++;
        }
    }

    private static (Instance a, Instance b) BuildSession(
        Func<byte[], bool>? dropA = null, Func<byte[], bool>? dropB = null)
    {
        var (ta, tb) = LoopbackTransport.CreatePair(dropA, dropB);

        var a = new Instance { Emu = new FakeEmuAdapter(portCount: 2) };
        a.Emu.LocalInputScript = frame => Btn((frame % 3) == 0);
        a.Driver = new FrameDriver(a.Emu, ta, p => new LockstepStrategy(p),
            localPort: 0, delay: Delay, redundancy: Redundancy);

        var b = new Instance { Emu = new FakeEmuAdapter(portCount: 2) };
        b.Emu.LocalInputScript = frame => Btn((frame % 2) == 0);
        b.Driver = new FrameDriver(b.Emu, tb, p => new LockstepStrategy(p),
            localPort: 1, delay: Delay, redundancy: Redundancy);

        a.Driver.Start();
        b.Driver.Start();
        return (a, b);
    }

    private static void Run(Instance a, Instance b, int iterations)
    {
        for (int i = 0; i < iterations; i++) { a.Step(); b.Step(); }
    }

    /// <summary>Both instances must agree on every frame they have both advanced through.</summary>
    private static void AssertNoSilentDesync(Instance a, Instance b)
    {
        int common = Math.Min(a.Emu.AppliedInputs.Count, b.Emu.AppliedInputs.Count);
        Assert.True(common > 0, "no shared progress to compare");
        for (int f = 0; f < common; f++)
        {
            var ia = a.Emu.AppliedInputs[f];
            var ib = b.Emu.AppliedInputs[f];
            Assert.Equal(ia.Frame, ib.Frame);
            for (int p = 0; p < 2; p++)
                Assert.True(ia.Ports[p].ValueEquals(ib.Ports[p]),
                    $"input desync at frame {f} port {p}");
        }
    }

    [Fact]
    public void CleanNetwork_PerfectLockstep()
    {
        var (a, b) = BuildSession();
        Run(a, b, 320);

        Assert.True(a.Driver.CurrentFrame >= 300, $"A reached {a.Driver.CurrentFrame}");
        Assert.Equal(a.Driver.CurrentFrame, b.Driver.CurrentFrame);
        Assert.Equal(0, a.Stalls);
        Assert.Equal(0, b.Stalls);

        AssertNoSilentDesync(a, b);
        Assert.Equal(a.Emu.HashMainMemory(), b.Emu.HashMainMemory());
    }

    [Fact]
    public void SparseIsolatedLoss_FullyHiddenByRedundancy()
    {
        // One isolated drop every 5th datagram (never consecutive), no startup loss. Each
        // frame rides 8 windows, so a lone drop is invisible: perfect lockstep persists.
        int ca = 0, cb = 0;
        var (a, b) = BuildSession(
            dropA: _ => { ca++; return ca > 8 && ca % 5 == 0; },
            dropB: _ => { cb++; return cb > 8 && cb % 5 == 0; });

        Run(a, b, 320);

        Assert.Equal(a.Driver.CurrentFrame, b.Driver.CurrentFrame);
        Assert.Equal(0, a.Stalls);
        Assert.Equal(0, b.Stalls);
        AssertNoSilentDesync(a, b);
        Assert.Equal(a.Emu.HashMainMemory(), b.Emu.HashMainMemory());
    }

    [Fact]
    public void HalfPacketLoss_StaysLiveAndCorrect()
    {
        // Drop every other datagram both ways, from the very first send. Redundancy keeps the
        // session live (both reach the target) and correct, though warm-up may cost a stall or
        // two before the seed inputs get through.
        int ca = 0, cb = 0;
        var (a, b) = BuildSession(
            dropA: _ => (ca++ & 1) == 0,
            dropB: _ => (cb++ & 1) == 0);

        Run(a, b, 420);

        Assert.True(a.Driver.CurrentFrame >= 300 && b.Driver.CurrentFrame >= 300,
            $"progress A={a.Driver.CurrentFrame} B={b.Driver.CurrentFrame}");
        AssertNoSilentDesync(a, b);
    }

    [Fact]
    public void SustainedHeavyLoss_MayStall_ButNeverSilentlyDesyncs()
    {
        // Let the session establish, then hammer A->B with bursts far longer than the window.
        // Pure redundancy cannot always recover (retransmission is M2), so liveness may fail —
        // but the frames both sims advanced through must still agree, always.
        int c = 0;
        var (a, b) = BuildSession(dropA: _ => (++c > 120) && (c % 24) < 16);

        Run(a, b, 2000);

        AssertNoSilentDesync(a, b);
    }
}
