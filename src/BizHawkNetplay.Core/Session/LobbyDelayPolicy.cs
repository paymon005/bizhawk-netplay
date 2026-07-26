using System;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>The host's pre-session input-delay choice and the details needed to explain it.</summary>
    public readonly struct LobbyDelayChoice
    {
        public LobbyDelayChoice(int frames, int automaticFrames, bool hasEstimate, bool wasCapped)
        {
            Frames = frames;
            AutomaticFrames = automaticFrames;
            HasEstimate = hasEstimate;
            WasCapped = wasCapped;
        }

        /// <summary>Final session delay after applying the manual floor and automatic maximum.</summary>
        public int Frames { get; }

        /// <summary>Uncapped delay suggested by the RTT estimate.</summary>
        public int AutomaticFrames { get; }

        public bool HasEstimate { get; }
        public bool WasCapped { get; }
    }

    /// <summary>
    /// Converts a settled lobby RTT into an input delay before the frame driver is constructed.
    /// The manual/negotiated value is a floor; <paramref name="automaticMaximum"/> limits only the
    /// automatic increase, so a peer explicitly asking for more delay is never silently overridden.
    /// </summary>
    public static class LobbyDelayPolicy
    {
        public static LobbyDelayChoice Choose(
            double roundTripMs, double frameMs, SyncMode mode, int manualFloor, int automaticMaximum)
        {
            manualFloor = Clamp(manualFloor, 1, HandshakeCodec.MaxInputDelay);
            automaticMaximum = Clamp(automaticMaximum, 1, HandshakeCodec.MaxInputDelay);

            if (double.IsNaN(roundTripMs) || double.IsInfinity(roundTripMs) || roundTripMs < 0
                || double.IsNaN(frameMs) || double.IsInfinity(frameMs) || frameMs <= 0)
                return new LobbyDelayChoice(manualFloor, manualFloor, hasEstimate: false, wasCapped: false);

            int oneWayFrames = (int)Math.Ceiling((roundTripMs / 2.0) / frameMs);
            // Rollback gets one frame of jitter/pump headroom so normal variance remains hidden.
            // Lockstep cannot predict, so retain the existing two-frame scheduling headroom.
            int headroom = mode == SyncMode.Rollback ? 1 : 2;
            int automatic = Clamp(oneWayFrames + headroom, 1, HandshakeCodec.MaxInputDelay);
            int cappedAutomatic = Math.Min(automatic, automaticMaximum);
            int final = Math.Max(manualFloor, cappedAutomatic);
            return new LobbyDelayChoice(final, automatic, hasEstimate: true,
                wasCapped: automatic > automaticMaximum && final < automatic);
        }

        private static int Clamp(int value, int minimum, int maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;
    }
}
