using System;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    public class LockstepAndPipelineTests
    {
        private static PortInput Btn(bool a) =>
            new PortInput(new[] { a }, Array.Empty<int>());

        [Fact]
        public void Frontier_StartsEmpty()
        {
            var p = new InputPipeline(2);
            Assert.Equal(-1, p.ConfirmedFrontier(0));
            Assert.Equal(-1, p.MinFrontier());
        }

        [Fact]
        public void Frontier_AdvancesOnlyContiguously()
        {
            var p = new InputPipeline(1);
            p.Add(0, 0, Btn(false));
            p.Add(0, 1, Btn(false));
            Assert.Equal(1, p.ConfirmedFrontier(0));

            // A gap: frame 3 arrives before 2 — frontier must not jump past the hole.
            p.Add(0, 3, Btn(false));
            Assert.Equal(1, p.ConfirmedFrontier(0));

            // Filling the hole advances across the whole contiguous run.
            p.Add(0, 2, Btn(false));
            Assert.Equal(3, p.ConfirmedFrontier(0));
        }

        [Fact]
        public void Add_IsIdempotentForRedundantPackets()
        {
            var p = new InputPipeline(1);
            p.Add(0, 0, Btn(true));
            p.Add(0, 0, Btn(false)); // redundant re-send; first value wins, no frontier corruption
            Assert.Equal(0, p.ConfirmedFrontier(0));
            Assert.True(p.TryGet(0, 0, out var got));
            Assert.True(got.Buttons[0]); // original retained
        }

        [Fact]
        public void MinFrontier_TracksLaggingPort()
        {
            var p = new InputPipeline(2);
            p.Add(0, 0, Btn(false));
            p.Add(0, 1, Btn(false));
            p.Add(1, 0, Btn(false));
            Assert.Equal(1, p.ConfirmedFrontier(0));
            Assert.Equal(0, p.ConfirmedFrontier(1));
            Assert.Equal(0, p.MinFrontier());
        }

        [Fact]
        public void Lockstep_RunsWhenAllPortsConfirmed()
        {
            var p = new InputPipeline(2);
            var s = new LockstepStrategy(p);

            p.Add(0, 0, Btn(true));
            p.Add(1, 0, Btn(false));

            var decision = s.BeginFrame(0);
            Assert.False(decision.Stall);
            Assert.False(s.IsStalled);
            Assert.NotNull(decision.Inputs);
            Assert.Equal(0, decision.Inputs!.Frame);
            Assert.True(decision.Inputs.Ports[0].Buttons[0]);
            Assert.False(decision.Inputs.Ports[1].Buttons[0]);
        }

        [Fact]
        public void Lockstep_StallsWhenAPortMissing()
        {
            var p = new InputPipeline(2);
            var s = new LockstepStrategy(p);

            p.Add(0, 0, Btn(true)); // port 1 has nothing yet

            var decision = s.BeginFrame(0);
            Assert.True(decision.Stall);
            Assert.True(s.IsStalled);
            Assert.Null(decision.Inputs);
        }

        [Fact]
        public void Lockstep_UnstallsAfterMissingInputArrives()
        {
            var p = new InputPipeline(2);
            var s = new LockstepStrategy(p);
            p.Add(0, 0, Btn(true));

            Assert.True(s.BeginFrame(0).Stall);

            // Remote input for the lagging port arrives.
            p.Add(1, 0, Btn(false));

            var again = s.BeginFrame(0);
            Assert.False(again.Stall);
            Assert.False(s.IsStalled);
        }

        [Fact]
        public void Merge_ThrowsIfPortMissing()
        {
            var p = new InputPipeline(2);
            p.Add(0, 5, Btn(false));
            Assert.Throws<InvalidOperationException>(() => p.Merge(5));
        }

        [Fact]
        public void PruneBefore_DropsOldFramesKeepsFrontier()
        {
            var p = new InputPipeline(1);
            for (int f = 0; f <= 10; f++) p.Add(0, f, Btn(false));
            Assert.Equal(10, p.ConfirmedFrontier(0));

            p.PruneBefore(8);
            Assert.False(p.TryGet(0, 5, out _));
            Assert.True(p.TryGet(0, 9, out _));
            Assert.Equal(10, p.ConfirmedFrontier(0)); // frontier is a watermark, unaffected by prune
        }
    }
}
