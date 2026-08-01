using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // ------------------------------------------------------------------ probe

    /// <summary>
    /// M0 capability probe, folded in as a diagnostic: times save/load/frame-advance on the
    /// loaded core and reports whether it qualifies for rollback (§5). Saves and restores the
    /// current position so it doesn't disturb play. Only runs when idle.
    /// </summary>
    private void RunProbe()
    {
        if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
        if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
        if (_phase.IsActive) { Log("Can't probe during a session."); return; }

        Log("=== capability probe ===");
        string? restore = null;
        try
        {
            var adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
            // MemorySaveState is nullable on the container but guaranteed by the _statable gate above.
            restore = APIs.MemorySaveState!.SaveCoreStateToMemory();
            double budget = FrameMs();
            Log(DescribeProbe(RunCapabilityProbe(adapter), adapter));
        }
        catch (Exception ex) { Log("probe failed: " + ex.Message); }
        finally
        {
            if (restore != null)
            {
                try { APIs.MemorySaveState!.LoadCoreStateFromMemory(restore); APIs.MemorySaveState!.DeleteState(restore); }
                catch (Exception ex) { Log("(warning) could not restore pre-probe state: " + ex.Message); }
            }
            Log("=== done ===");
        }
    }

    /// <summary>
    /// Dumps what BizHawk sees for input: the controller/binding keys we resolve against, and
    /// the host inputs pressed right now vs how they map to P1. Hold a button and click.
    /// </summary>
    /// <summary>
    /// Watch the analog axes for a few seconds and report every distinct value the core was handed.
    ///
    /// The question this answers is "does the stick cover its range, or does it jump?" — and the
    /// distinct-value list answers it on sight. A healthy stick produces a spread of values across
    /// the range. A stick whose digital directions are also bound produces small values and then
    /// nothing until the extreme, because the core's GetStickValues discards the axis the moment the
    /// digital button fires. The gap in the middle is the whole complaint.
    ///
    /// Sampled on a timer rather than in a loop: blocking the UI thread here would stall the frame
    /// clock during a session, and this is most useful during one.
    /// </summary>
    private void StartAnalogWatch()
    {
        if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
        if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
        if (_analogWatchTimer != null) { Log("analog watch already running."); return; }

        EmuHawkAdapter adapter;
        try { adapter = _adapter ?? new EmuHawkAdapter(APIs, _emulator, _statable); }
        catch (Exception ex) { Log("analog watch failed: " + ex.Message); return; }

        var seen = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var rawLo = new Dictionary<string, int>(StringComparer.Ordinal);
        var rawHi = new Dictionary<string, int>(StringComparer.Ordinal);
        int ticksLeft = AnalogWatchSamples;

        Log($"=== analog watch: move the stick through its full travel for the next " +
            $"{AnalogWatchSamples * AnalogWatchIntervalMs / 1000} seconds ===");

        _analogWatchTimer = new System.Windows.Forms.Timer { Interval = AnalogWatchIntervalMs };
        _analogWatchTimer.Tick += (_, __) =>
        {
            try
            {
                foreach (var (name, raw, resolved) in adapter.SampleAnalog())
                {
                    if (!seen.TryGetValue(name, out var set)) seen[name] = set = new SortedSet<int>();
                    set.Add(resolved);
                    rawLo[name] = rawLo.TryGetValue(name, out int lo) ? Math.Min(lo, raw) : raw;
                    rawHi[name] = rawHi.TryGetValue(name, out int hi) ? Math.Max(hi, raw) : raw;
                }
            }
            catch { }

            if (--ticksLeft > 0) return;
            StopAnalogWatch();
            ReportAnalogWatch(seen, rawLo, rawHi);
        };
        _analogWatchTimer.Start();
    }

    private void StopAnalogWatch()
    {
        try { _analogWatchTimer?.Stop(); _analogWatchTimer?.Dispose(); } catch { }
        _analogWatchTimer = null;
    }

    private void ReportAnalogWatch(Dictionary<string, SortedSet<int>> seen,
        Dictionary<string, int> rawLo, Dictionary<string, int> rawHi)
    {
        var sb = new System.Text.StringBuilder("=== analog watch result ===\n");
        foreach (var kv in seen)
        {
            var values = kv.Value;
            sb.Append("  '").Append(kv.Key).Append("': ").Append(values.Count)
              .Append(" distinct value(s) reached the core, from ").Append(values.Min)
              .Append(" to ").Append(values.Max)
              .Append("   (raw host ").Append(rawLo[kv.Key]).Append("..").Append(rawHi[kv.Key]).Append(")\n");
            sb.Append("      ").Append(string.Join(", ", values)).Append('\n');

            // The tell for a digital override is a PLATEAU: a cluster of small values and then a jump
            // straight to the rail, with the middle of the range never appearing.
            //
            // The threshold has to be well clear of sampling noise. This samples ~100 times across a
            // 255-value range while you sweep, so ordinary gaps of 10-15 are expected and mean
            // nothing — an earlier version flagged those and sent a real investigation down a blind
            // alley. A genuine override leaves most of one half of the range empty, so require both
            // a large gap AND that it ends at the extreme, which is where the override pins it.
            int biggestGap = 0, gapFrom = 0, gapTo = 0, prev = int.MinValue;
            foreach (int v in values)
            {
                if (prev != int.MinValue && v - prev > biggestGap) { biggestGap = v - prev; gapFrom = prev; gapTo = v; }
                prev = v;
            }
            bool endsAtRail = gapTo >= values.Max - 2 || gapFrom <= values.Min + 2;
            if (biggestGap >= 40 && endsAtRail)
                sb.Append("      !! ").Append(gapFrom).Append(" -> ").Append(gapTo)
                  .Append(" is a ").Append(biggestGap).Append("-wide jump straight to the extreme, with ")
                  .Append("nothing in between — that is what a digital override looks like. If the four ")
                  .Append("A Up/Down/Left/Right binds are set, clear them and run this again.\n");
            else
                sb.Append("      the range is covered smoothly (largest gap ").Append(biggestGap)
                  .Append(", which at this sample rate is normal) — the stick is reaching the core intact.\n");
        }
        if (seen.Count == 0) sb.Append("  (no axes sampled)\n");
        Log(sb.ToString());
    }

    private void RunInputTest()
    {
        if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
        if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
        try
        {
            var adapter = _adapter ?? new EmuHawkAdapter(APIs, _emulator, _statable);
            Log("=== input test (hold a button while clicking) ===");
            Log(adapter.DescribeInputState());
        }
        catch (Exception ex) { Log("input test failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ helpers

    private PeerIdentity BuildIdentity(EmuHawkAdapter a, bool wantRollback)
    {
        var layouts = new string[a.PortCount];
        for (int p = 0; p < layouts.Length; p++)
            layouts[p] = a.GetControllerLayout(p).Digest;
        // Advertise the core's real rollback depth only when this peer wants rollback; otherwise 0
        // so the negotiator (which needs both peers to opt in) settles on lockstep for free.
        int depth = wantRollback ? MeasureRollbackDepth(a) : 0;

        // Determinism: a core reporting non-deterministic usually means "determinism was not
        // requested" rather than "this will diverge" — N64 with no movie loaded says it and syncs
        // fine in practice. That report is therefore not treated as a refusal; the session runs and
        // the periodic checksum catches a genuine divergence, which is the check that actually
        // proves anything. Formerly a Diagnostics opt-in that defaulted to on, so this is the same
        // behaviour with one less box to tick.
        const bool deterministic = true;
        return new PeerIdentity(Protocol, a.RomHash, a.CoreName, a.CoreVersion,
            a.SyncSettingsDigest, layouts, deterministic, maxRollbackDepth: depth,
            syncSettingsFields: a.SyncSettingsFields);
    }

    /// <summary>Map the "My controls" dropdown to an input-source port: P1..P4 (0..3) or -1 (assigned port).</summary>
    private int InputSourceFromCombo()
    {
        int idx = _inputSourceCombo.SelectedIndex;
        return idx >= 0 && idx <= 3 ? idx : -1; // index 4 ("Assigned port") or none => -1
    }

    /// <summary>
    /// Run the capability probe once (cached) to learn how deep a rollback this core can repair
    /// inside one frame budget. Saves and restores the core state so the pre-session position is
    /// untouched. Requires the emulator to already be paused (we advance frames invisibly here).
    /// </summary>
    /// <summary>
    /// How many samples the probe should take. This runs synchronously on the UI thread, so the
    /// count is the freeze length: 60 samples is nothing on a light core and most of a second on
    /// N64, where one save alone is 6ms. One timed save tells the two apart, and a median of 12 is
    /// ample for sizing a prediction horizon. Nothing used to reach here on a heavy core, because
    /// the old N64 override short-circuited the probe before it ran.
    /// </summary>
    private static int ProbeSamplesFor(EmuHawkAdapter a)
    {
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var handle = a.SaveStateToMemory();
            double saveMs = timer.Elapsed.TotalMilliseconds;
            a.ReleaseState(handle);
            // 24 rather than 12 on a heavy core: its frame cost swings enough between runs to move
            // the verdict (measured 1.86ms to 3.58ms across consecutive N64 probes, either side of
            // the ~3.44ms boundary), and a median over 12 noisy samples inherits that. Doubling it
            // costs a couple of hundred milliseconds at session start against a 16MiB state export.
            return saveMs > 1.0 ? 24 : 60;
        }
        catch { return 12; }
    }

    /// <summary>
    /// Run the probe against the model the SESSION actually uses.
    ///
    /// Both the Diagnostics button and the pre-session measurement come through here, because they
    /// drifted apart once and it mattered: the button still asked for the original cost model, so
    /// it reported a heavy core as steady 11.5ms / maxDepth 0 while a session — eliding snapshots
    /// on confirmed frames and allowing a repair two frame periods — computed steady 5.5ms and a
    /// workable depth for the very same machine. A diagnostic that answers a question nothing asks
    /// is worse than no diagnostic.
    /// </summary>
    private ProbeResult RunCapabilityProbe(EmuHawkAdapter a)
    {
        double budget = FrameMs();
        return new CapabilityProbe(a, new StopwatchClock(), samples: ProbeSamplesFor(a))
            .Run(budget, budget * 0.25,
                elideConfirmedSaves: true, repairBudgetMs: RepairBudgetFrames * budget,
                // Same constant the session's tuning uses. A depth solved against a different
                // snapshot spacing than the one the repair will run is a depth nothing checked.
                keyframeInterval: RepairKeyframeInterval);
    }

    /// <summary>
    /// The probe result with the video settings it was measured under.
    ///
    /// A probe number means nothing without them. The verdict is decided by the frame cost, the
    /// frame cost is the video plugin's to spend, and the resolution behind it is not a sync
    /// setting — so it appeared nowhere near the probe: the session's video line prints later, only
    /// once a peer has joined, and the Diagnostics button never printed it at all. Anyone comparing
    /// a run of probes across settings was left matching numbers to a setting from memory.
    ///
    /// The measured repair goes on a second line. It answers a different question from the verdict
    /// — whether the sum the verdict was solved from describes the thing it claims to — and this
    /// output exists to be read a dozen runs at a time.
    /// </summary>
    private static string DescribeProbe(ProbeResult result, EmuHawkAdapter a)
    {
        string? video = a.VideoSettingsDiagnostic();
        string head = video == null ? result.Summary : $"{result.Summary} — video: {video}";
        return result.RepairDiagnostic.Length == 0 ? head : $"{head}{Environment.NewLine}    {result.RepairDiagnostic}";
    }

    private int MeasureRollbackDepth(EmuHawkAdapter a)
    {
        if (_probeDepth >= 0) return _probeDepth;
        string? restore = null;
        try
        {
            // MemorySaveState is nullable on the container but this only runs for statable cores.
            restore = APIs.MemorySaveState!.SaveCoreStateToMemory();
            double budget = FrameMs();
            // Probe the model the session will actually run: snapshots elided on confirmed frames,
            // and a repair allowed more than one frame period. Measuring the old model and then
            // running a different one is how you get a depth that has nothing to do with the cost.
            var result = RunCapabilityProbe(a);
            _replayDeterministic = result.ReplayDeterministic;
            // A core that does not reproduce from a savestate has no usable depth at all: the work
            // the depth budgets FOR is replaying. Reporting 0 keeps every peer's negotiation honest
            // without needing a second field on the wire.
            _probeDepth = result.ReplayDeterministic ? result.MaxRollbackDepth : 0;
            // What a repaired frame costs, from the two terms just measured: the frame itself,
            // plus its share of a snapshot at the keyframe spacing the session will run. Seeds
            // the strategy's cost cap so the first deep repair of a session is not the thing that
            // discovers it — see RollbackTuning.SeedRepairPerFrameMs.
            _probeRepairPerFrameMs = result.MedianFrameMs
                + result.MedianSaveMs / Math.Max(1, RepairKeyframeInterval);
            Log($"rollback probe — {DescribeProbe(result, a)}");
            if (!result.ReplayDeterministic)
                ConnLog("this core did not reproduce the same memory when the probe replayed the " +
                    "same inputs from the same savestate. Rollback repair is exactly that operation, " +
                    "so it would desync whenever the link made it predict — and stay perfectly in " +
                    "sync on a connection fast enough that it never had to, which is why this only " +
                    "shows up against a distant opponent. Using lockstep, which never reloads state.",
                    Color.Firebrick);
        }
        catch (Exception ex) { _probeDepth = 0; Log("rollback probe failed, will use lockstep: " + ex.Message); }
        finally
        {
            if (restore != null)
            {
                try { APIs.MemorySaveState!.LoadCoreStateFromMemory(restore); APIs.MemorySaveState!.DeleteState(restore); }
                catch (Exception ex) { Log("(warning) could not restore pre-probe state: " + ex.Message); }
            }
        }
        return _probeDepth;
    }
}
