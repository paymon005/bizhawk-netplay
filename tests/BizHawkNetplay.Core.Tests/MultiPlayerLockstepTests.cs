using System;
using System.Collections.Concurrent;
using System.Linq;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Three-player lockstep over an in-memory hub — the same sync path M1 uses, exercised with
/// three ports to prove the pipeline/strategy are genuinely N-port. The hub mimics the host-relay
/// topology's effect (every instance's input reaches every other) without any sockets.
/// </summary>
public class MultiPlayerLockstepTests
{
    private const int Delay = 2;
    private const int Redundancy = 8;

    private static PortInput Btn(bool pressed)
    {
        var b = new bool[8];
        b[0] = pressed;
        return new PortInput(b, Array.Empty<int>());
    }

    /// <summary>In-memory full-delivery hub: what any instance sends, every other instance receives.</summary>
    private sealed class HubTransport : ITransport
    {
        private readonly ConcurrentQueue<byte[]> _inbound = new();
        private ConcurrentQueue<byte[]>[] _others = Array.Empty<ConcurrentQueue<byte[]>>();

        public void Connect(HubTransport[] all) =>
            _others = all.Where(t => !ReferenceEquals(t, this)).Select(t => t._inbound).ToArray();

        public void Send(byte[] datagram)
        {
            foreach (var q in _others) q.Enqueue(datagram);
        }

        public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);
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

    private static Instance[] BuildSession(int players)
    {
        var hubs = new HubTransport[players];
        for (int i = 0; i < players; i++) hubs[i] = new HubTransport();
        for (int i = 0; i < players; i++) hubs[i].Connect(hubs);

        var instances = new Instance[players];
        for (int i = 0; i < players; i++)
        {
            var emu = new FakeEmuAdapter(portCount: players);
            int port = i;
            emu.LocalInputScript = frame => Btn((frame % (port + 2)) == 0); // distinct pattern per port
            var driver = new FrameDriver(emu, hubs[i], p => new LockstepStrategy(p),
                localPort: i, delay: Delay, redundancy: Redundancy);
            instances[i] = new Instance { Emu = emu, Driver = driver };
            driver.Start();
        }
        return instances;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void CleanNetwork_AllPlayersStayInPerfectLockstep(int players)
    {
        var inst = BuildSession(players);
        for (int it = 0; it < 340; it++)
            for (int i = 0; i < players; i++) inst[i].Step();

        for (int i = 0; i < players; i++)
        {
            Assert.True(inst[i].Driver.CurrentFrame >= 300, $"player {i} reached {inst[i].Driver.CurrentFrame}");
            Assert.Equal(0, inst[i].Stalls);
        }

        // Every instance applied byte-identical inputs on every port for every shared frame.
        int common = inst.Min(x => x.Emu.AppliedInputs.Count);
        Assert.True(common > 0);
        for (int f = 0; f < common; f++)
            for (int p = 0; p < players; p++)
            {
                var reference = inst[0].Emu.AppliedInputs[f].Ports[p];
                for (int i = 1; i < players; i++)
                    Assert.True(reference.ValueEquals(inst[i].Emu.AppliedInputs[f].Ports[p]),
                        $"input desync at frame {f} port {p} on player {i}");
            }

        var h0 = inst[0].Emu.HashMainMemory();
        for (int i = 1; i < players; i++) Assert.Equal(h0, inst[i].Emu.HashMainMemory());
    }
}
