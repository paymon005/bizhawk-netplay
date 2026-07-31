using System;
using System.Drawing;
using BizHawk.Client.Common;
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

    // --- savestates: not claimed here; handled by events instead ---------------------------------
    //
    // WantsToControlSavestates is a single switch over saving AND loading, and saving is worth
    // keeping. Claiming it would also stop saves dead: MainForm calls the tool and RETURNS, doing no
    // work at all for a save ("assume success by the tool", MainForm.cs:4167 and 4238), and
    // IMainFormForTools exposes no save method a tool could call to do it instead.
    //
    // BizHawk splits what this interface does not. BeforeQuickLoad is cancellable and fires BEFORE
    // MainForm touches the slot, the file, or the controlling tool (MainForm.cs:4137-4143), and
    // BeforeQuickSave is a separate event we never touch — so Quick Load refuses while Quick Save
    // stays completely normal. StateLoaded then catches every load that did happen, by any route.
    // See SubscribeHostCommandEvents.
    //
    // That matters beyond tidiness: the frame-drift check in FrameTick can only see a load that
    // MOVED the frame counter. A state captured at the frame we are already on, with different
    // contents, passes it — and would then wait up to a full checksum interval to be caught as a
    // desync. StateLoaded fires on the load itself, so the frame number is irrelevant. The drift
    // check stays as the second line, for anything that moves the counter without an event.
    //
    // Every member below is unreachable while the property is false. They exist because the
    // interface requires them.

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

    // --- savestate events ------------------------------------------------------------------------

    /// <summary>
    /// Hook Quick Load and state-loaded for as long as ownership is held.
    ///
    /// Subscribed on acquire and dropped on release rather than once for the tool's lifetime, which
    /// avoids two problems at once. The API container is not injected until AFTER construction, so
    /// there is nothing to subscribe to in the constructor; and a ROM load REPLACES it — ToolManager
    /// nulls its provider, re-injects, and only then calls Restart — so a subscription taken once
    /// would be left attached to a dead EmuClientApi with nothing hooked to the live one. Acquiring
    /// per session sidesteps both, because a session always starts after injection.
    ///
    /// The <c>-=</c> before each <c>+=</c> makes it idempotent regardless.
    /// </summary>
    private void SubscribeHostCommandEvents()
    {
        try
        {
            var client = APIs.EmuClient;
            client.BeforeQuickLoad -= OnBeforeQuickLoad;
            client.BeforeQuickLoad += OnBeforeQuickLoad;
            client.StateLoaded -= OnStateLoaded;
            client.StateLoaded += OnStateLoaded;
        }
        catch (Exception ex) { Log("(note) could not hook savestate events: " + ex.Message); }
    }

    private void UnsubscribeHostCommandEvents()
    {
        try
        {
            var client = APIs.EmuClient;
            client.BeforeQuickLoad -= OnBeforeQuickLoad;
            client.StateLoaded -= OnStateLoaded;
        }
        catch { /* container already gone (ROM swap, shutdown) — the old one is garbage anyway */ }
    }

    /// <summary>Refuse the load before MainForm acts on it. Quick SAVE is a different event we do
    /// not subscribe to, so it is unaffected.</summary>
    private void OnBeforeQuickLoad(object sender, BeforeQuickLoadEventArgs e)
    {
        if (!_hostOwnershipHeld) return;
        e.Handled = true;
        RefuseHostCommand($"Quick Load ({e.Name})");
    }

    /// <summary>
    /// A state was loaded by some route we do not intercept — the menu, Load State As, a drag-drop.
    /// End the session on the load itself rather than waiting for a symptom: this machine is now
    /// playing a different game from everyone else, and unlike the frame-drift check this does not
    /// depend on the frame number having changed.
    ///
    /// Our own state work never reaches here: the probe and the adapter go through
    /// IMemorySaveStateApi and IStatable, straight to the core, never through MainForm.LoadState.
    /// </summary>
    private void OnStateLoaded(object sender, StateLoadedEventArgs e)
    {
        if (!_hostOwnershipHeld) return;
        EndSession($"a savestate was loaded ('{e.Name}') — this machine's timeline no longer matches " +
                   "the other players'. Saving during a session is fine; loading is not.");
    }
}
