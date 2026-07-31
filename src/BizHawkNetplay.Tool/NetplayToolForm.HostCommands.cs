using System.Drawing;
using BizHawk.Client.EmuHawk;

namespace BizHawkNetplay.Tool;

/// <summary>
/// The frontend commands a session has to refuse, via BizHawk's own <see cref="IControlMainform"/>.
///
/// <c>BlockFrameAdvance</c> stops the run loop STEPPING the core. It does nothing about the commands
/// that MUTATE it: rewind and reboot replace the machine underneath a running timeline, and every
/// peer would carry on from a state this one no longer has.
///
/// Savestates are deliberately left alone — saving is something worth being able to do mid-session,
/// and the interface cannot separate saving from loading. See the savestate section below.
///
/// BizHawk already has the seam for this. A tool that claims a command has MainForm call the tool
/// instead of doing the thing (<c>MainForm.cs:4094</c> and friends), so refusing is a supported
/// operation rather than a hook or a hack. External tools are eligible: ToolManager's list holds
/// them alongside built-ins and <c>FirstOrNull</c> checks only <c>IsActive</c>, which for a
/// ToolFormBase means its window exists.
///
/// Claimed only while <see cref="_hostOwnershipHeld"/> — the same flag that gates BlockFrameAdvance,
/// so a command is refused for exactly as long as the timeline is ours, lobby included. Everything
/// else stays false: the movie and read-only commands are not ours, and claiming them would take
/// them away from TAStudio for no benefit.
///
/// Known limit: BizHawk picks the FIRST claimant, so this does not make TAStudio interoperable —
/// both would claim rewind. That is already covered by the standing "don't run TAStudio during
/// netplay" warning the session start puts up.
/// </summary>
public sealed partial class NetplayToolForm : IControlMainform
{
    /// <summary>
    /// Refusals are driven by hotkeys, which repeat. Say it once, not once per press.
    ///
    /// In SECONDS, through the same helper everything else here uses. This first compared
    /// MonotonicNow() against 3000 as though it were milliseconds — it is a raw
    /// Stopwatch.GetTimestamp(), so against a 10MHz QPC the throttle was 0.3ms and every repeat got
    /// its own line. A zero stamp reads as PositiveInfinity, so the first refusal always speaks.
    /// </summary>
    private const double HostCommandRefusalIntervalSeconds = 3;
    private long _lastHostCommandRefusalStamp;

    private void RefuseHostCommand(string what)
    {
        if (MonotonicElapsedSeconds(_lastHostCommandRefusalStamp) < HostCommandRefusalIntervalSeconds) return;
        _lastHostCommandRefusalStamp = MonotonicNow();
        ConnLog($"{what} is disabled while netplay owns the timeline — it would replace the emulator " +
                "state under every other player. Disconnect first.", Color.DarkOrange);
    }

    // --- savestates: deliberately NOT claimed ----------------------------------------------------
    //
    // Saving a state is harmless — it reads the core out, it does not replace it — and it is worth
    // being able to do mid-session. Loading one is the hazard. But WantsToControlSavestates is a
    // single switch over both, and claiming it means MainForm calls the tool and then RETURNS: for
    // saves it does no work at all ("assume success by the tool", MainForm.cs:4167 and 4238), and
    // IMainFormForTools exposes no save method a tool could call to do the work itself. So the only
    // ways to permit saving are to fake it — which cannot honour the quick-slot the user actually
    // pressed — or to not claim. Not claiming is the honest one.
    //
    // What that gives up is prevention of load-state, not detection of it. A load moves the core's
    // frame counter, and FrameTick already compares that counter against the driver's every tick:
    // it ends the session naming the cause ("the core's frame count jumped back N — a rewind/
    // load-state hotkey fired?") rather than letting it become a mystery desync. Rewind and reboot
    // stay claimed below, since those are separate switches and neither is wanted mid-session.
    //
    // Every member here is therefore unreachable while the property is false. They exist because
    // the interface requires them.

    public bool WantsToControlSavestates => false;

    public void SaveState() { }

    public bool LoadState() => false;

    public void SaveStateAs() { }

    public bool LoadStateAs() => false;

    public void SaveQuickSave(int slot) { }

    public bool LoadQuickSave(int slot) => false;

    public bool SelectSlot(int slot) => false;

    public bool PreviousSlot() => false;

    public bool NextSlot() => false;

    // --- rewind ----------------------------------------------------------------------------------

    public bool WantsToControlRewind => _hostOwnershipHeld;

    /// <summary>
    /// Called once per frame while claimed, so it must stay completely silent — no logging, no
    /// throttled message, nothing. Capturing is exactly what we do not want: the ring would fill
    /// with states whose frame numbers belong to a timeline only this machine has.
    /// </summary>
    public void CaptureRewind() { }

    /// <summary>False = no frame advance required. Deliberately quiet rather than routed through
    /// <see cref="RefuseHostCommand"/>: holding the rewind key asks every frame, and the throttle
    /// would still be answering a question the user stopped asking seconds ago.</summary>
    public bool Rewind() => false;

    // --- reboot ----------------------------------------------------------------------------------

    public bool WantsToControlReboot => _hostOwnershipHeld;

    /// <summary>A reboot restarts the core at frame zero. MainForm treats our returning as "handled"
    /// and does nothing, which is the whole point.</summary>
    public void RebootCore() => RefuseHostCommand("Reboot Core");

    // --- not ours --------------------------------------------------------------------------------
    // Claiming a command means MainForm stops doing it and calls us instead, so anything we do not
    // genuinely need to intercept is left alone. These members exist because the interface requires
    // them; their bodies are unreachable while the properties above them stay false.

    public bool WantsToControlReadOnly => false;

    public void ToggleReadOnly() { }

    public bool WantsToControlStopMovie => false;

    public void StopMovie(bool suppressSave) { }

    public bool WantsToControlRestartMovie => false;

    public bool RestartMovie() => false;

    public bool WantsToBypassMovieEndAction => false;
}
