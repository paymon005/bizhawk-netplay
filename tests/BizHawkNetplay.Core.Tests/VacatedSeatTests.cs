using System;
using System.Collections.Concurrent;
using System.Linq;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// A vacated seat: its player left for good and the session continues around it. The port answers
/// neutral forever, nothing ever waits on it, and none of the per-port watchdogs treat its silence
/// as a fault — each of these was a way the session used to end the moment a seat emptied.
/// </summary>
public class VacatedSeatTests
{
    private static PortInput Btn(bool pressed)
    {
        var b = new bool[8];
        b[0] = pressed;
        return new PortInput(b, []);
    }

    private static PortInput Neutral() => new(new bool[8], []);

    // ---- pipeline ------------------------------------------------------------------

    [Fact]
    public void AVacatedPortConfirmsEveryFrameAndMergesAsNeutral()
    {
        var pipeline = new InputPipeline(3);
        pipeline.Add(0, 0, Btn(true));
        pipeline.Add(1, 0, Btn(false));

        // Port 2's player is gone; without the vacate this frame can never be confirmed.
        Assert.False(pipeline.AllConfirmed(0));
        pipeline.Vacate(2, Neutral());
        Assert.True(pipeline.AllConfirmed(0));
        Assert.True(pipeline.IsVacated(2));

        var merged = pipeline.Merge(0);
        Assert.True(merged.Ports[0].Buttons[0]);
        Assert.False(merged.Ports[2].Buttons[0]); // the empty seat plays neutral

        // And for any frame, however far ahead — the seat never becomes something to wait for.
        Assert.True(pipeline.TryGet(2, 12345, out var far));
        Assert.False(far.Buttons[0]);
    }

    [Fact]
    public void InputForAVacatedPortIsIgnoredRatherThanResurrectingIt()
    {
        var pipeline = new InputPipeline(2);
        pipeline.Vacate(1, Neutral());
        // A straggler datagram from the departed player must not pull the frontier back down off
        // its ceiling or overwrite the neutral answer.
        pipeline.Add(1, 0, Btn(true));
        Assert.True(pipeline.TryGet(1, 0, out var input));
        Assert.False(input.Buttons[0]);
        pipeline.Add(0, 0, Btn(false));
        Assert.True(pipeline.AllConfirmed(0)); // only the LIVE port's frontier gates the frame
    }

    [Fact]
    public void MinFrontierIgnoresAVacatedPort()
    {
        var pipeline = new InputPipeline(2);
        pipeline.Add(0, 0, Btn(false));
        pipeline.Add(0, 1, Btn(false));
        pipeline.Vacate(1, Neutral());
        // The checksum quantizes on MinFrontier; a vacated seat must not drag it to the ceiling
        // (that is what the min is for) nor hold it at -1 forever (the old behaviour).
        Assert.Equal(1, pipeline.MinFrontier());
    }

    // ---- driver --------------------------------------------------------------------

    /// <summary>Hub that also records every datagram, so a test can assert what was ASKED for —
    /// a gap request aimed at an empty seat is the bug, whether or not anything answers it.</summary>
    private sealed class RecordingHub : ITransport
    {
        private readonly ConcurrentQueue<byte[]> _inbound = new();
        private ConcurrentQueue<byte[]>[] _others = [];
        public readonly ConcurrentQueue<byte[]> Sent = new();

        public void Connect(RecordingHub[] all) =>
            _others = [.. all.Where(t => !ReferenceEquals(t, this)).Select(t => t._inbound)];

        public void Send(byte[] datagram)
        {
            Sent.Enqueue(datagram);
            foreach (var q in _others) q.Enqueue(datagram);
        }

        public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);
    }

    private static (FrameDriver driver, FakeEmuAdapter emu, RecordingHub hub)[] BuildTwoLiveOfThree(
        bool rollback)
    {
        // A 3-seat session where seat 2's player has left: only two instances exist, and both
        // vacate port 2 on their fresh drivers — exactly what the tool does after the rebuild.
        var hubs = new[] { new RecordingHub(), new RecordingHub() };
        foreach (var hub in hubs) hub.Connect(hubs);

        var result = new (FrameDriver, FakeEmuAdapter, RecordingHub)[2];
        for (int i = 0; i < 2; i++)
        {
            var emu = new FakeEmuAdapter(portCount: 3);
            int port = i;
            emu.LocalInputScript = frame => Btn((frame % (port + 2)) == 0);
            var driver = rollback
                ? new FrameDriver(emu, hubs[i],
                    p => new RollbackStrategy(p, emu, port, maxRollback: 4),
                    localPort: i, delay: 2, redundancy: 8, rollbackWindow: 4)
                : new FrameDriver(emu, hubs[i], p => new LockstepStrategy(p),
                    localPort: i, delay: 2, redundancy: 8);
            driver.VacatePort(2);
            driver.Start();
            result[i] = (driver, emu, hubs[i]);
        }
        return result;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoSurvivorsAdvanceInStepAroundAnEmptySeat(bool rollback)
    {
        var session = BuildTwoLiveOfThree(rollback);
        for (int step = 0; step < 400; step++)
            foreach (var (driver, emu, _) in session)
            {
                if (driver.OnPreFrame() == FrameStep.Ran)
                {
                    emu.AdvanceAppliedFrame();
                    driver.OnPostFrame();
                }
            }

        // Both made real progress and agreed on every frame both finished (under rollback,
        // LastInputByFrame holds the corrected final input — the correctness oracle).
        Assert.True(session[0].driver.CurrentFrame > 100,
            $"driver 0 only reached frame {session[0].driver.CurrentFrame}");
        Assert.True(session[1].driver.CurrentFrame > 100,
            $"driver 1 only reached frame {session[1].driver.CurrentFrame}");
        int common = Math.Min(session[0].driver.CurrentFrame, session[1].driver.CurrentFrame) - 8;
        Assert.True(common > 90);
        for (int f = 0; f < common; f++)
        {
            var a = session[0].emu.LastInputByFrame[f];
            var b = session[1].emu.LastInputByFrame[f];
            for (int p = 0; p < 3; p++)
                Assert.True(a.Ports[p].ValueEquals(b.Ports[p]),
                    $"frame {f} port {p} diverged between the survivors");
            Assert.False(a.Ports[2].Buttons[0], $"frame {f}: the empty seat pressed a button");
        }
    }

    [Fact]
    public void NoWatchdogEverFlagsTheEmptySeat()
    {
        var session = BuildTwoLiveOfThree(rollback: true);
        for (int step = 0; step < 300; step++)
            foreach (var (driver, emu, _) in session)
            {
                if (driver.OnPreFrame() == FrameStep.Ran)
                {
                    emu.AdvanceAppliedFrame();
                    driver.OnPostFrame();
                }
            }

        foreach (var (driver, _, hub) in session)
        {
            // The silence scan must never name the vacated port (its stamp is the never-heard
            // sentinel), and the KI-9 hole backstop must never see a hole in it — the vacated
            // frontier sits at int.MaxValue, where unguarded arithmetic wrapped negative and made
            // the hole test permanently true.
            if (driver.TryGetMostSilentRemotePort(out int silentPort, out _))
                Assert.NotEqual(2, silentPort);
            Assert.False(driver.TryGetUnrepairedHole(out int holePort, out _),
                $"an unrepairable hole was reported for port {holePort}");

            // And nothing ever ASKED the empty seat for input: no gap request names port 2.
            var codec = driver.Codec;
            foreach (var datagram in hub.Sent)
                if (codec.TryDecodeRequest(datagram, out byte target, out _))
                    Assert.NotEqual(2, target);
        }
    }

    [Fact]
    public void EverySilentEdgeIsVisible_NotOnlyTheWorst()
    {
        // Overlapping outages: reporting only the most-silent port serialized them, so a second
        // dead leg stayed invisible until the first recovered or killed the session — and its
        // relay was therefore never asked for. A 4-player session has three legs per peer and is
        // correspondingly likelier to lose two at once.
        var emu = new FakeEmuAdapter(portCount: 4);
        var hub = new RecordingHub();
        hub.Connect([hub]);
        var driver = new FrameDriver(emu, hub, p => new LockstepStrategy(p),
            localPort: 0, delay: 2, redundancy: 8, portCount: 4);
        driver.Start();
        driver.ResetRemoteInputLiveness(); // ports 1..3 all "just heard from"

        var silence = new double[driver.PortCount];
        driver.GetRemoteSilenceSeconds(silence);
        Assert.Equal(4, driver.PortCount);
        Assert.True(silence[0] < 0, "the local port is not an edge");
        for (int p = 1; p < 4; p++)
            Assert.True(silence[p] >= 0, $"port {p} should be tracked");

        // A vacated seat drops out of the scan entirely — its silence is by design, not an outage.
        driver.VacatePort(2);
        driver.GetRemoteSilenceSeconds(silence);
        Assert.True(silence[2] < 0, "a vacated seat must not read as a silent edge");
        Assert.True(silence[1] >= 0 && silence[3] >= 0, "the live edges are still tracked");
    }

    // ---- wire ----------------------------------------------------------------------

    [Fact]
    public void SeatVacatedRoundTripsAndRefusesGarbage()
    {
        var generation = new SessionGeneration(123456789UL, 7);
        var body = ControlMessageCodec.EncodeSeatVacated(generation, 3);

        Assert.True(ControlMessageCodec.TryDecodeSeatVacated(body, out var decodedGen, out int port));
        Assert.Equal(generation, decodedGen);
        Assert.Equal(3, port);

        Assert.False(ControlMessageCodec.TryDecodeSeatVacated(null!, out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeSeatVacated(new byte[5], out _, out _));
        body[12] = byte.MaxValue; // a port past MaxPlayers is a corrupt frame, not a seat
        Assert.False(ControlMessageCodec.TryDecodeSeatVacated(body, out _, out _));
    }

    [Fact]
    public void WelcomeCarriesVacatedSeatsForARejoiner()
    {
        var generation = new SessionGeneration(42UL, 3);
        var body = HandshakeCodec.EncodeWelcome(1, 4, 2, SyncMode.Rollback, generation,
            vacatedPorts: new[] { 3, 2 });

        var seats = HandshakeCodec.DecodeVacatedSeats(body);
        Assert.Equal(new[] { 3, 2 }, seats);

        // Absent means none — the ordinary lobby WELCOME never carries the line.
        var plain = HandshakeCodec.EncodeWelcome(1, 4, 2, SyncMode.Rollback, generation);
        Assert.Empty(HandshakeCodec.DecodeVacatedSeats(plain));
    }

    [Fact]
    public void ChecksumLedgerResolvesOnActivePlayersNotSeatCount()
    {
        var ledger = new ChecksumLedger();
        var generation = new SessionGeneration(9UL, 1);

        // 4 seats, one vacated: three reports must resolve the frame — waiting for the fourth
        // would leave every frame pending forever. Ports keep their original numbers, so the
        // range check must still admit port 3.
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(generation, 0, 300, 0xAA, 4, 3));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(generation, 1, 300, 0xAA, 4, 3));
        Assert.Equal(ChecksumOutcome.Agreement, ledger.Record(generation, 3, 300, 0xAA, 4, 3));

        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(generation, 0, 600, 0xAA, 4, 3));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(generation, 1, 600, 0xAA, 4, 3));
        Assert.Equal(ChecksumOutcome.Mismatch, ledger.Record(generation, 3, 600, 0xBB, 4, 3));
    }
}
