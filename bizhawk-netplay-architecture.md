# BizHawk Netplay External Tool — Architecture

**Goal:** A C# external tool (ApiHawk) that adds online multiplayer to any local-multiplayer game BizHawk can run, using delayed lockstep as the universal baseline, with GGPO-style rollback as a drop-in strategy for cores that can afford it.

**Design principle:** Lockstep and rollback are the *same system* with one swappable component. Everything else — transport, session, input serialization, determinism enforcement, desync detection — is shared and built once. Rollback is an optimization of lockstep, not a fork of it.

---

## 1. Requirements

### Functional
- 2+ players, each running their own BizHawk instance with the same ROM and core, playing as if on one console.
- Works generically across cores: no per-game memory maps, no per-game code. The tool only touches inputs and whole-core savestates.
- Lockstep mode available for every deterministic core. Rollback mode selectable only where a runtime capability probe says the core can afford it.
- Session setup over direct IP (same model as RemotePlay); host/join, port (controller slot) assignment.

### Non-functional
- Input-to-network path adds no measurable latency beyond the configured input delay.
- Lockstep: playable at input delay ≈ ceil(RTT/2 ÷ frame_time) + 1 frames.
- Rollback: repair of an 8-frame misprediction must complete within one frame budget (16.6 ms at 60 fps) on target cores, or the core doesn't qualify for rollback.
- Desyncs detected within ~1 second of occurrence, never discovered minutes later.

### Constraints
- Guest in EmuHawk's process: all emulator API calls happen on the UI thread via external-tool callbacks. No forking BizHawk.
- Determinism is a precondition the tool *verifies and enforces*, not something it creates. Cores that can't run deterministically are unsupported, full stop.

---

## 2. High-level design

```
┌─────────────────────────────────────────────────────────────┐
│ EmuHawk process (UI thread)                                 │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │ NetplayToolForm (IExternalToolForm)                   │  │
│  │  UI: host/join, slot assignment, delay, mode, status  │  │
│  └──────────────┬────────────────────────────────────────┘  │
│                 │ pre/post-frame callbacks                  │
│  ┌──────────────▼────────────────────────────────────────┐  │
│  │ FrameDriver                                           │  │
│  │  the only component that touches ISyncStrategy        │  │
│  └───┬──────────────────────┬───────────────────┬────────┘  │
│      │                      │                   │           │
│  ┌───▼──────────┐   ┌───────▼────────┐   ┌──────▼────────┐  │
│  │ EmuAdapter   │   │ ISyncStrategy  │   │ InputPipeline │  │
│  │ (ApiHawk     │   │  • Lockstep    │   │  capture,     │  │
│  │  wrapper)    │   │  • Rollback    │   │  delay queue, │  │
│  └──────────────┘   └───────┬────────┘   │  frontier     │  │
│                             │            └──────┬────────┘  │
│  ┌──────────────────────────▼───────────────────▼────────┐  │
│  │ SessionManager  (handshake, state xfer, checksums)    │  │
│  └──────────────────────────┬────────────────────────────┘  │
└─────────────────────────────┼───────────────────────────────┘
                              │ thread-safe queues
                ┌─────────────▼──────────────┐
                │ Transport (background thd) │
                │  UDP input channel +       │
                │  reliable control channel  │
                └────────────────────────────┘
```

Threading rule: **exactly one boundary.** The transport thread does socket I/O and timestamping only; everything it receives goes into a lock-free queue that the FrameDriver drains at the top of each frame callback. No emulator API call ever happens off the UI thread. All strategy logic runs single-threaded on the UI thread, which keeps both strategies trivially race-free.

---

## 3. Components

### 3.1 EmuAdapter — the only file that knows about BizHawk

Wraps ApiHawk behind an interface so the rest of the system is testable without EmuHawk running:

```csharp
interface IEmuAdapter
{
    // Identity & determinism
    string RomHash { get; }
    string CoreName { get; }
    string SyncSettingsDigest { get; }     // hash of core sync-settings blob
    bool   VerifyDeterministicMode();       // fail session if false

    // Input
    ControllerLayout GetControllerLayout(); // from core's ControllerDefinition
    void SetInputs(InputSet inputs);        // joypad override, ALL ports, every frame

    // State (rollback + session sync)
    StateHandle SaveStateToMemory();
    void LoadStateFromMemory(StateHandle h);
    byte[] ExportState();                   // initial session sync
    void   ImportState(byte[] state);

    // Frame control
    void SetPaused(bool paused);
    void RunFramesInvisible(int count, Func<int, InputSet> inputsFor); // rollback repair
    void SetAudioMuted(bool muted);

    // Integrity
    uint HashMainMemory();                  // cheap rolling checksum
}
```

Two hard rules enforced here:

1. **Input interception is absolute.** The user's physical controller must never reach the core directly. Every frame, `SetInputs` overrides *all* ports — local ports get the delayed synchronized value, remote ports get the remote value. Local raw input is read only as the *source* feeding the pipeline. Any leak here is an instant, silent desync.
2. **The tool doesn't trust configuration; it verifies it.** Deterministic core construction, matching sync settings, matching core versions — checked at handshake, session refused on mismatch. This converts Dolphin's decade of "why did it desync" mystery-debugging into an upfront error message.

### 3.2 InputPipeline — generic over any core

The "any game" property lives here. BizHawk cores expose a `ControllerDefinition` (buttons + axes per port). At session start, both peers exchange the layout and derive an identical compact serialization: buttons packed into a bitfield, axes as their native integer width. No per-game knowledge anywhere.

```csharp
struct InputFrame
{
    int      Frame;       // simulation frame this applies to
    byte     Port;
    byte[]   Payload;     // packed per negotiated layout
}
```

Flow per frame N:
1. Read local physical input.
2. Stamp it for frame `N + D` (D = input delay, negotiated at session start; D ≥ 1 even for rollback — a small delay shrinks average rollback depth for free).
3. Enqueue locally *and* hand to Transport.
4. Maintain per-port `ConfirmedFrontier` = highest frame for which real remote input is known contiguously.

**Packet redundancy:** every input packet carries the last R inputs (R ≈ 8), not just the newest. A lost packet then costs nothing unless R in a row are lost. This is the single cheapest robustness win in the whole design — input payloads are a few bytes, so redundancy is nearly free, and it means lockstep stalls happen on *latency*, not on ordinary packet loss.

### 3.3 Transport

Reused/adapted from RemotePlay's UDP stack, minus the entire media path:
- **Input channel:** unreliable UDP, redundant payloads as above, sequence-numbered.
- **Control channel:** lightweight reliability layer (ack + retransmit) over the same socket for handshake, state transfer, checksums, pacing reports, chat.
- **Clock/quality:** periodic ping + "frame advantage" reports (how far ahead of the remote's confirmed frontier am I running). Consumed by the pacing logic in both strategies.

Topology: direct P2P for 2 players (halves latency versus any relay). For 3+ players, host-relay star — the host rebroadcasts inputs — because P2P full-mesh input agreement complicates the confirmed-frontier logic for marginal benefit at retro-game player counts. The InputPipeline doesn't know which topology is in use; it just sees confirmed inputs per port.

### 3.4 SessionManager

Handshake sequence (control channel, reliable):
1. Protocol version check.
2. Exchange + compare: ROM hash, core name/version, sync-settings digest, controller layouts.
3. Verify deterministic mode on both ends.
4. Capability probe results exchanged (see §5) → negotiate mode: lockstep (always available) or rollback (both peers qualified + user opted in).
5. Port assignment (slot UI — same concept as RemotePlay's controller slot assignment).
6. Host exports full savestate → transfers → all peers import. Both sims now bit-identical at frame 0.
7. Synchronized start.

**Desync detection:** every K frames (K ≈ 60), each peer computes `HashMainMemory()` for frame N and sends `(N, hash)` on the control channel. Compare when both sides' hashes for the same frame are available. On mismatch: freeze, report the divergence frame, offer host-state resync or abort. This runs in *both* modes — lockstep desyncs are rarer but catastrophic precisely because nothing else would notice them.

### 3.5 FrameDriver and ISyncStrategy — the swap point

The FrameDriver owns the per-frame sequence and is strategy-agnostic:

```csharp
// Runs on UI thread, top of every frame callback
void OnPreFrame()
{
    DrainNetworkQueue();                  // feeds strategy.OnRemoteInput(...)
    var decision = strategy.BeginFrame(currentFrame);
    if (decision.Stall) { emu.SetPaused(true); return; }   // retry next tick
    emu.SetPaused(false);
    emu.SetInputs(decision.Inputs);
}

void OnPostFrame()
{
    strategy.EndFrame(currentFrame);
    currentFrame++;
}
```

```csharp
interface ISyncStrategy
{
    FrameDecision BeginFrame(int frame);   // inputs to apply, or Stall
    void EndFrame(int frame);
    void OnRemoteInput(InputFrame input);  // called during queue drain, UI thread
    void OnPacingReport(PacingInfo info);
}
```

**LockstepStrategy** (~150 lines of logic):
- `BeginFrame(N)`: if `ConfirmedFrontier(port) ≥ N` for all remote ports → return merged inputs. Else → `Stall`.
- `EndFrame`: nothing (checksum cadence handled by SessionManager).
- `OnRemoteInput`: advance frontier; if currently stalled and frontier now covers N, unpause.
- Pacing is implicit: you can't run ahead, because you block.

**RollbackStrategy** (the drop-in, ~everything below is additive):
- Ring buffer of `StateHandle` for the last W frames (W = max rollback window, from the capability probe). BBN3 used W = 90 (1.5 s) on a GBA core via the same memorysavestate API — validated scale.
- `OnRemoteInput(f, input)`: if `input ≠ predicted[f]` → flag misprediction; keep the *deepest* divergence frame if multiple arrive in one drain (BBN3 does exactly this with its rollbackflag max-write).
- Repair model — **catch-up mode**, not a synchronous burst. When `BeginFrame(N)` sees a flagged divergence at M < N:
  1. `LoadStateFromMemory(buffer[M-1])`; evict ring-buffer states for frames M..N-1 — they're stale and will be recreated during catch-up (BBN3 marks this eviction "very important"; it is — loading a stale state for the repaired range would desync).
  2. Enter CatchUp mode: hide display + throttle sound (`DispSpeedupFeatures = 0` / `SoundThrottle`, per BBN3 — see risk #3), uncap emulation speed (`limitframerate(false)`, `speedmode` high).
  3. Over the next real iterations of EmuHawk's frame loop, the FrameDriver feeds inputs from the corrected history (confirmed where known, predicted beyond), re-saving states each frame, until the sim frame catches back up to the pre-repair frontier.
  4. Exit CatchUp: restore display/sound/speed, resume normal prediction.
  Rationale: BBN3's Lua could not reentrantly frame-advance from inside an event callback and had to structure repair this way; a C# external tool callback plausibly has the same constraint. Catch-up mode works either way. If M0 proves reentrant frame advance IS allowed from a tool callback, a synchronous `RunFramesInvisible(N-M)` repair becomes a drop-in optimization inside step 3 — same interface, lower repair latency.
- `BeginFrame(N)` otherwise never stalls for input — it returns confirmed-or-predicted inputs (prediction = repeat-last-confirmed, and nothing smarter: BBN3 attempted input-type-aware prediction decay and abandoned it as desync-prone).
- Pacing valve: if frame advantage exceeds threshold, bleed it off via speedmode modulation (see §3.6), hard-stalling only as a last resort. This replaces lockstep's implicit blocking.
- `EndFrame(N)`: `SaveStateToMemory` → ring buffer; evict oldest.
- Hard-stall condition: if `ConfirmedFrontier` falls more than W behind (can't roll back that far), stall until it recovers — degrades to lockstep behavior under terrible network conditions rather than desyncing.

Everything above the strategy — transport, session, input pipeline, EmuAdapter, checksums, UI — is byte-identical between modes. Shipping lockstep first therefore builds and battle-tests ~85% of the rollback system.

### 3.6 Pacing & clock model (shared by both strategies)

Adopted from BBN3's working implementation, which is smoother than pause-based stalling:

- **Session-start clock sync:** repeated ping exchanges computing offset via the NTP formula `((t1−t0)+(t2−t3))/2`; reject samples beyond 1 SD from the mean; take the median of survivors as the shared clock offset, median RTT/2 as baseline ping. (Directly liftable from BBN3's `ClockSync()`.)
- **Continuous speed modulation:** each frame, accumulate wall-clock drift = actual elapsed − target frame time; when |drift| exceeds a small threshold, set `client.speedmode` to a corrective percentage until drift bleeds off, then return to 100. This absorbs stutter, repair time, and frame-advantage imbalance without binary pause/unpause. Use the console's *exact* frame period as the target (BBN3 uses 16.743 ms for GBA, not 16.67 — query the core's clock rate rather than assuming 60.000 Hz, or drift accumulates by construction).
- **Synchronized start:** rather than a bare countdown, hold both peers in a stallable "starting" screen whose duration the client stretches until both sides begin the same sim frame (BBN3 does this with a variable-length intro animation).
- Hard pause is reserved for lockstep's genuine missing-input stall and rollback's frontier-exceeded stall; everything else is speed nudges.

---

## 4. Per-frame data flow (lockstep, steady state)

```
frame N tick:
  transport thread ──▶ queue ──▶ drain: frontier advances to ≥ N
  local pad read ──▶ stamp N+D ──▶ send (with last R redundant) ──▶ also self-enqueue
  strategy: all ports confirmed @ N?  yes ──▶ SetInputs(merged @ N)
  core runs frame N
  every K frames: HashMainMemory ──▶ control channel
```

Stall path: not confirmed → pause client → OnRemoteInput unpauses when the gap fills. With redundancy R=8, a stall requires either genuine latency spike > D·frame_time or 8 consecutive lost packets.

---

## 5. Capability probe (gates rollback, sizes lockstep expectations)

At ROM load, before the session starts, run automatically:
1. `SaveStateToMemory` × 100 → median cost, state size.
2. `LoadStateFromMemory` × 100 → median cost.
3. Invisible frame advance × 100 → median frame sim cost.
4. Compute: `max_rollback_depth = floor((frame_budget − normal_frame_cost − headroom) / (load + depth·(sim + save)))` solved for depth.

Publish in handshake. Rollback is offered only if `max_rollback_depth ≥ 6` on **both** peers (worst peer wins). Expected outcome: NES/GB/GBC/SMS comfortably qualify, SNES/Genesis likely qualify, GBA depends on core, N64/PSX/Saturn fail and run lockstep-only. The probe makes this an empirical per-machine fact instead of a hardcoded core list.

---

## 6. Known integration risks (attack these first)

1. **Stall mechanics.** Largely de-risked by BBN3's model: pacing is handled by continuous `speedmode` modulation (§3.6), leaving hard pause only for true missing-input stalls. Remaining validation: pause/unpause latency granularity for that one case. Fallback if pause proves too coarse: speedmode ≈ 0 spin with message pumping.
2. **Reentrant frame advance from a tool callback — the deciding experiment.** BBN3's Lua could not frame-advance from inside an event callback; their whole repair architecture exists because of it. Determine in M0 whether a C# external tool can. Outcome selects synchronous repair (optimization) vs pure catch-up mode (default, works regardless). The architecture assumes catch-up so this experiment can only improve things, never break them.
3. **Invisible emulation + audio during repair.** Downgraded from "validate" to "expect problems": BBN3 has `client.invisibleemulation()` commented out in two places, replaced with `DispSpeedupFeatures = 0` + `SoundThrottle` toggling. Treat the config-flags approach as primary, invisibleemulation as the thing to test, not trust. Audio remained their roughest edge — budget real time here.
4. **Frame callback timing guarantees.** Observed, not theoretical: BBN3 contains an explicit guard because their per-frame hook could "run multiple times in a single frame," and they note `event.onframestart` is unreliable around rollbacks. They escaped to game-opcode hooks — unavailable to a generic tool. Our mitigation: FrameDriver dedupes on the emulator frame counter (process each sim frame number exactly once per mode), and refuse to run alongside movies/TAStudio/Lua.
5. **Determinism validation per core.** Even "deterministic mode" cores can have RTC or initial-state leaks. The checksum cadence catches them; a soak test (two local instances, scripted random inputs, hours) qualifies each core before it goes on the supported list.

---

## 7. Milestones

- **M0 — Probe harness** (1–2 weekends): EmuAdapter + capability probe as a standalone external tool. Deliverable: the per-core feasibility table **plus** answers to the three API experiments: (a) can a tool callback reentrantly frame-advance? (b) does invisibleemulation work on current BizHawk, or is DispSpeedupFeatures the path? (c) speedmode modulation behavior under throttle. De-risks everything downstream and settles risks 1–4 empirically.
- **M1 — 2-player lockstep, one core** (2–3 weekends): NESHawk, direct IP, fixed delay, handshake + initial state transfer + checksum. Deliverable: two machines playing a NES co-op game.
- **M2 — Hardening + generality** (2–4 weekends): generic ControllerDefinition serialization across cores, configurable delay, packet redundancy, desync resync flow, 3–4 player host-relay, slot UI, soak-test qualification of the core list.
- **M3 — RollbackStrategy** (3–5 weekends): ring buffer, prediction, repair loop, pacing valve, capability gating. Only for cores M0 approved.
- **M4 — QoL**: spectators (input stream is already a perfect replay feed), session chat, NAT traversal if direct IP proves annoying (RemotePlay experience applies directly).

Checkpoints at each M-boundary: demo criteria above must pass before starting the next phase.

---

## 8. Decisions & trade-offs (explicit)

| Decision | Choice | Trade-off accepted |
|---|---|---|
| Baseline sync | Delayed lockstep | Input delay felt by all; in exchange: works on every deterministic core, simplest failure modes (Dolphin-proven) |
| Rollback scope | Per-core, probe-gated | No rollback on heavy cores ever via this path; avoids shipping a mode that stutters |
| Rollback input delay | D ≥ 1 even in rollback | 1 frame of felt delay buys materially shallower average rollbacks |
| Topology | P2P (2p), host-relay (3+) | Relay adds one hop for 3+; buys simple frontier logic |
| Input channel | Unreliable UDP + R-frame redundancy | ~8× input payload size (still trivial); buys loss-immunity without retransmit latency |
| State auth | No authority — full symmetry + checksums | Desync = detect and resync/abort, never silent divergence; no host-authoritative complexity |
| BizHawk coupling | External tool only, no fork | UI-thread residency and API-surface limits; buys zero maintenance burden tracking upstream |
| Movie/TAStudio interop | Refuse to coexist | Less flexible; avoids an entire class of frame-timing conflicts |

**Revisit as it grows:** if rollback on SNES-class cores proves popular and the probe shows savestate cost dominating, a core-side fast-path (partial state save) would be the next lever — but that's the fork/upstream-PR line this design deliberately stays behind.

---

## Appendix A — Lessons taken from BBN3-netcode (bbn3_netplay.lua, MIT-licensed)

Source reviewed: `ssbmars/BBN3-netcode`, `BizHawk-2.5.2/bbn3_netplay.lua` (2,788 lines). MIT license permits reuse with attribution.

**Adopted into this design:**
- Catch-up-mode repair (uncapped speed + hidden display across real loop iterations) instead of assuming synchronous multi-frame repair inside a callback (`StartResim` + `PreBattleLoop`).
- Ring-buffer savestate management incl. mandatory eviction of the repaired frame range (`save` table, `savecount = 90`).
- Keep-the-deepest-divergence bookkeeping when multiple mispredictions land in one frame (rollbackflag max-write in `ApplyRemoteInputs`).
- Repeat-last-confirmed prediction, deliberately nothing smarter (their smarter attempt is commented out as desync-prone).
- Continuous speedmode-based drift correction against the console's exact frame period (`fs_timerift` logic, TargetFrame = 16.743 ms for GBA).
- NTP-style session clock sync with 1-SD outlier rejection and median offset (`ClockSync`).
- Stretchable synchronized-start screen (`Battle_Vis`).
- `DispSpeedupFeatures`/`SoundThrottle` as the primary hide-repair mechanism; invisibleemulation demoted to "verify."
- Frame-hook dedup guarding (their multi-fire guard; our version keys on the emulator frame counter).
- Minimum input delay of 1 frame plus user-configurable extra (BufferVal/ExtraBuffer).

**Considered and rejected:**
- Their input transport (newest-input-only packets + app-level ack table + resend-unacked-every-other-frame + 600-entry prune). Our redundant-payload scheme achieves loss tolerance with less machinery and no retransmit latency on the hot path.
- Game-RAM-resident input stacks and opcode-execution hooks — the game-specific half of their co-design; incompatible with a generic tool by definition. Our generic equivalents: joypad-level injection and emulator-frame hooks with dedup.
- Wall-clock-timestamp packet IDs (`getframetime` + `tinywait` stalls to avoid ID collisions). Sim-frame-numbered inputs are simpler and collision-free.

**Warnings inherited:**
- Audio during repair was their persistent rough edge; never fully solved in Lua.
- Coroutine-pumped receive path exists only because Lua is single-threaded; our transport thread + queue drain supersedes it.
