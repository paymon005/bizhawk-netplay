using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Sync;

/// <summary>
/// What a strategy decides for one frame: either stall (inputs not yet available) or run
/// the core with a fully-resolved <see cref="InputSet"/>.
/// </summary>
public readonly struct FrameDecision
{
    private FrameDecision(bool stall, InputSet? inputs)
    {
        Stall = stall;
        Inputs = inputs;
    }

    /// <summary>When true, the FrameDriver pauses the core and retries next tick.</summary>
    public bool Stall { get; }

    /// <summary>Inputs to apply this frame; null iff <see cref="Stall"/>.</summary>
    public InputSet? Inputs { get; }

    public static FrameDecision Run(InputSet inputs) => new FrameDecision(false, inputs);
    public static readonly FrameDecision StallDecision = new FrameDecision(true, null);
}
