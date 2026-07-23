namespace BizHawkNetplay.Core.Input
{
    /// <summary>
    /// One port's packed input for one simulation frame — the atomic unit that crosses the
    /// wire. Payload is serialized per the negotiated <see cref="ControllerLayout"/>; no
    /// per-game knowledge is encoded here.
    /// </summary>
    public readonly struct InputFrame
    {
        public InputFrame(int frame, byte port, byte[] payload)
        {
            Frame = frame;
            Port = port;
            Payload = payload;
        }

        /// <summary>Simulation frame this input applies to (already delay-stamped: N + D).</summary>
        public int Frame { get; }

        /// <summary>Controller port index.</summary>
        public byte Port { get; }

        /// <summary>Buttons+axes packed per the negotiated layout.</summary>
        public byte[] Payload { get; }
    }
}
