using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// The only abstraction that knows about BizHawk. Wraps ApiHawk so the rest of the
/// system — transport, session, input pipeline, sync strategies, checksums — is
/// exercised entirely without EmuHawk running (see the fake used in tests).
///
/// Two hard rules the real implementation must honour:
///   1. Input interception is absolute. The physical controller never reaches the core
///      directly — every port's input for a frame comes from the merged
///      <see cref="InputSet"/> the session decided. Any leak is an instant, silent desync.
///   2. The tool verifies configuration, it does not trust it. Deterministic construction
///      and matching sync settings/core versions are checked at handshake and the session
///      is refused on mismatch.
/// </summary>
public interface IEmuAdapter
{
    // --- Identity & determinism ---------------------------------------------------

    /// <summary>Hash of the loaded ROM, compared across peers at handshake.</summary>
    string RomHash { get; }

    /// <summary>Core name (e.g. "NesHawk"), compared across peers.</summary>
    string CoreName { get; }

    /// <summary>Core version string, compared across peers.</summary>
    string CoreVersion { get; }

    /// <summary>Hash of the core's sync-settings blob, compared across peers.</summary>
    string SyncSettingsDigest { get; }

    /// <summary>The same settings as sorted <c>name → value</c> pairs, so a mismatch on
    /// <see cref="SyncSettingsDigest"/> can be named. Explanatory only, and may be empty.</summary>
    IReadOnlyList<KeyValuePair<string, string>> SyncSettingsFields { get; }

    /// <summary>False if the core is not running in a deterministic configuration; fails the session.</summary>
    bool VerifyDeterministicMode();

    // --- Input --------------------------------------------------------------------

    /// <summary>Controller layout derived from the core's ControllerDefinition, per port.</summary>
    ControllerLayout GetControllerLayout(int port);

    /// <summary>Number of controller ports the loaded core exposes.</summary>
    int PortCount { get; }

    /// <summary>Read the local player's current physical input for one port (source feeding the pipeline).</summary>
    PortInput ReadLocalInput(int port);

    /// <summary>
    /// Hand the merged inputs to the adapter ahead of a frame it will advance itself. This is the
    /// simple driving model, used by <see cref="Sync.FrameDriver.OnPreFrame"/> and by the test
    /// harness: set the inputs, then step.
    ///
    /// EmuHawk cannot be driven that way — a core reads its input from the
    /// <c>IController</c> passed INTO <c>FrameAdvance</c>, so the real adapter carries the merged
    /// set across in <see cref="AdvanceFrame"/> instead and implements this as a no-op. Neither
    /// model is dead: production uses <c>AdvanceFrame</c>, the suite drives
    /// <c>OnPreFrame</c>/<c>OnPostFrame</c>. What would be wrong is believing this method is how
    /// input reaches a real core.
    /// </summary>
    void SetInputs(InputSet inputs);

    // --- State (rollback + session sync) ------------------------------------------

    /// <summary>Capture the whole core state to memory (rollback ring buffer, capability probe).</summary>
    StateHandle SaveStateToMemory();

    /// <summary>Restore a previously captured in-memory state.</summary>
    void LoadStateFromMemory(StateHandle handle);

    /// <summary>
    /// Return an in-memory state the rollback ring no longer needs. Each
    /// <see cref="SaveStateToMemory"/> holds a whole-core buffer (~hundreds of KiB to tens of MiB)
    /// and the rollback strategy takes one per frame, so releasing evicted ring entries is
    /// mandatory to avoid unbounded growth. The BizHawk adapter pools the returned buffers rather
    /// than freeing them, which is what keeps a 60Hz ring off the large object heap — so a handle
    /// read AFTER release is reading someone else's state. Idempotent / safe on unknown handles.
    /// </summary>
    void ReleaseState(StateHandle handle);

    /// <summary>Serialize full state for the initial session sync transfer.</summary>
    byte[] ExportState();

    /// <summary>Load a full state received during initial session sync.</summary>
    void ImportState(byte[] state);

    // --- Frame control ------------------------------------------------------------

    void SetPaused(bool paused);

    /// <summary>
    /// Advance <paramref name="count"/> frames without presenting video, sourcing each
    /// frame's inputs from <paramref name="inputsFor"/> (called with the relative index).
    /// Used for synchronous rollback repair where the callback permits reentrant frame
    /// advance; the catch-up path is used otherwise.
    /// </summary>
    void RunFramesInvisible(int count, Func<int, InputSet> inputsFor);

    /// <summary>
    /// Advance one frame with video rendered, for measuring what a frame the player actually sees
    /// costs.
    ///
    /// <see cref="RunFramesInvisible"/> is the right figure for repair, which genuinely does not
    /// render — but the capability probe was charging that same figure for the live frame too, and
    /// nothing had ever established that the two were the same number.
    ///
    /// On Mupen64Plus with Rice they are: twelve probes across four resolutions put the rendered
    /// and unrendered costs within ~5% of each other, the rendered one sometimes cheaper, so the
    /// render flag saves nothing there and the old single figure was right by luck. That is a
    /// property of the core, not of the model — a software rasteriser has no reason to honour it —
    /// and the point of measuring both is that the answer is now read rather than assumed.
    /// </summary>
    void AdvanceRenderedFrame(InputSet inputs);

    void SetAudioMuted(bool muted);

    // --- Integrity ----------------------------------------------------------------

    /// <summary>
    /// Cheap rolling checksum over main memory for periodic desync detection.
    ///
    /// <paramref name="salt"/> is the frame the checksum describes. An adapter that cannot read
    /// the whole domain cheaply may sample it, and uses this to rotate which slice it reads so
    /// consecutive checksums cover different ground. Peers derive it from the same frame number,
    /// so they always sample identically and stay comparable; an adapter that reads everything
    /// ignores it.
    /// </summary>
    uint HashMainMemory(int salt = 0);
}
