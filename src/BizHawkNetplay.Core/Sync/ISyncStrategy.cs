using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Sync
{
    /// <summary>
    /// The swap point. Lockstep and rollback implement the same interface; the FrameDriver is
    /// strategy-agnostic and everything above (transport, session, input pipeline, adapter,
    /// checksums, UI) is byte-identical between them. All methods run single-threaded on the
    /// UI thread.
    /// </summary>
    public interface ISyncStrategy
    {
        /// <summary>Decide inputs for <paramref name="frame"/>, or stall.</summary>
        FrameDecision BeginFrame(int frame);

        /// <summary>Called after the core has advanced <paramref name="frame"/>.</summary>
        void EndFrame(int frame);

        /// <summary>
        /// A remote input for (<paramref name="frame"/>, <paramref name="port"/>) arrived during the
        /// network-queue drain (UI thread) and has already been added to the pipeline. Deliberately
        /// not given the payload: the only implementation that cares reads the unpacked input back out
        /// of the pipeline anyway, and passing bytes would force the drain to copy every frame it
        /// receives rather than only the ones it keeps.
        /// </summary>
        void OnRemoteInput(int frame, int port);

        /// <summary>Clock/quality update from the transport (frame advantage, ping).</summary>
        void OnPacingReport(PacingInfo info);
    }
}
