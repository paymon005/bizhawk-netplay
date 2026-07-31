using System.Drawing;
using BizHawk.Client.EmuHawk;

namespace BizHawkNetplay.Tool;

/// <summary>
/// The frontend commands a session has to refuse, via BizHawk's own <see cref="IControlMainform"/>.
///
/// <c>BlockFrameAdvance</c> stops the run loop STEPPING the core. It does nothing about the commands
/// that MUTATE it: quick-load, load-state, and reboot all replace the machine underneath a running
/// timeline, and every peer would carry on from a state this one no longer has. That is a desync
/// with no cause visible in any log — the frame counter does not even jump.
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
/// both would claim savestates and rewind. That is already covered by the standing "don't run
/// TAStudio during netplay" warning the session start puts up.
/// </summary>
public sealed partial class NetplayToolForm : IControlMainform
{
    /// <summary>Refusals are driven by hotkeys, which repeat. Say it once, not once per press.</summary>
    private const double HostCommandRefusalIntervalMs = 3000;
    private double _lastHostCommandRefusalMs = double.NegativeInfinity;

    private void RefuseHostCommand(string what)
    {
        double now = MonotonicNow();
        if (now - _lastHostCommandRefusalMs < HostCommandRefusalIntervalMs) return;
        _lastHostCommandRefusalMs = now;
        ConnLog($"{what} is disabled while netplay owns the timeline — it would replace the emulator " +
                "state under every other player. Disconnect first.", Color.DarkOrange);
    }

    // --- savestates ------------------------------------------------------------------------------
    // Loads return false ("did not load"), saves simply do not happen. Saving is refused as well as
    // loading: a quick-save taken mid-session is a state whose frame means nothing outside it, and
    // silently writing one over a slot the user cares about is its own small betrayal.

    public bool WantsToControlSavestates => _hostOwnershipHeld;

    public void SaveState() => RefuseHostCommand("Save State");

    public bool LoadState() { RefuseHostCommand("Load State"); return false; }

    public void SaveStateAs() => RefuseHostCommand("Save State As");

    public bool LoadStateAs() { RefuseHostCommand("Load State As"); return false; }

    public void SaveQuickSave(int slot) => RefuseHostCommand($"Quick Save (slot {slot})");

    public bool LoadQuickSave(int slot) { RefuseHostCommand($"Quick Load (slot {slot})"); return false; }

    // Slot selection changes which slot is selected, not the machine. Returning false means "not
    // handled — carry on", so the normal behaviour continues and the user can still move the
    // selection around while connected.
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
