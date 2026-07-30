using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Sync
{
    /// <summary>
    /// The universal baseline (§3.5). Run frame N only when every port's input for N is
    /// confirmed; otherwise stall. Pacing is implicit — you cannot run ahead because you
    /// block. This is the ~85% of the system that rollback later reuses unchanged.
    /// </summary>
    public sealed class LockstepStrategy : ISyncStrategy
    {
        private readonly InputPipeline _pipeline;

        public LockstepStrategy(InputPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>True while the most recent BeginFrame could not proceed (for UI/status).</summary>
        public bool IsStalled { get; private set; }

        public FrameDecision BeginFrame(int frame)
        {
            if (_pipeline.AllConfirmed(frame))
            {
                IsStalled = false;
                return FrameDecision.Run(_pipeline.Merge(frame));
            }
            IsStalled = true;
            return FrameDecision.StallDecision;
        }

        public void EndFrame(int frame)
        {
            // Checksum cadence is owned by the SessionManager; nothing to do per frame here.
        }

        public void OnRemoteInput(int frame, int port)
        {
            // The pipeline advances the port frontier; the driver retries BeginFrame next tick,
            // which unstalls automatically once the gap is filled.
        }

        public void OnPacingReport(PacingInfo info)
        {
            // Lockstep needs no pacing valve — blocking is the pacing.
        }
    }
}
