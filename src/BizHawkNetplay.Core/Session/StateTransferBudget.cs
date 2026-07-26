using System;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// The time-budget policy for savestate transfers — pure math, extracted from the tool form so
    /// the deadline relationships are unit-testable. Everything is expressed in seconds; the caller
    /// converts to its own clock (the tool uses Stopwatch-tick deadlines).
    ///
    /// The load-bearing invariant (review finding F5): a surviving player's receive deadline must
    /// cover the host's WHOLE post-rejoin pipeline, which runs THREE strictly sequential stages —
    /// (1) WELCOME/state send to the rejoiner, (2) the rejoiner's import + READY, (3) the survivor's
    /// own Resync transfer — each individually allowed one full phase by the host's socket
    /// deadlines. A survivor budgeting fewer phases will end the whole session while the host is
    /// still inside its own healthy bounds.
    /// </summary>
    public static class StateTransferBudget
    {
        /// <summary>Fixed allowance for the receiver to read + import a state once the bytes land.</summary>
        public const double GraceBaseSeconds = 10.0;

        /// <summary>Pessimistic wire-rate floor used to scale budgets with the state size.</summary>
        public const double MinBytesPerSecond = 200 * 1024;

        /// <summary>Sequential transfer/import stages in the host's post-rejoin pipeline (see class doc).</summary>
        public const int HostPipelinePhases = 3;

        /// <summary>Extra slack on the survivor's receive deadline beyond the modeled phases.</summary>
        public const double ReceiveSlackSeconds = 5.0;

        /// <summary>One bounded transfer/import phase: fixed grace plus the size at the floor rate.</summary>
        public static double OnePhaseSeconds(int stateBytes) =>
            GraceBaseSeconds + Math.Max(0, stateBytes) / MinBytesPerSecond;

        /// <summary>How long a peer may take to apply a state we sent it (and how long it is excused
        /// from the ping watchdog while the frame is inbound). Never open-ended: a peer that dies
        /// mid-transfer is still caught, just later.</summary>
        public static double ApplyDeadlineSeconds(int stateBytes) => OnePhaseSeconds(stateBytes);

        /// <summary>
        /// How long a frozen survivor waits between a resync/reconnect BEGIN and the complete state
        /// frame: the reconnect wait window plus the host's full <see cref="HostPipelinePhases"/>-stage
        /// pipeline plus slack. Deliberately finite — BEGIN followed by a silent or partial TCP frame
        /// must never freeze the emulator forever.
        /// </summary>
        public static double SurvivorReceiveDeadlineSeconds(int stateBytes, int waitSeconds) =>
            waitSeconds + HostPipelinePhases * OnePhaseSeconds(stateBytes) + ReceiveSlackSeconds;

        /// <summary>Size-scaled socket send/receive timeout for one transfer phase, floored at the
        /// ordinary handshake timeout so tiny states keep the normal bound.</summary>
        public static int SocketTimeoutMs(int stateBytes, int floorMs) =>
            (int)Math.Min(int.MaxValue, Math.Max(floorMs, OnePhaseSeconds(stateBytes) * 1000.0));
    }
}
