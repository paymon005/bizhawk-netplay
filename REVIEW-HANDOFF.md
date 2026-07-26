# Review handoff — ChatGPT rewrite vs commit `7e15752`

**Date:** 2026-07-26
**Baseline:** `7e15752` ("Default input delay to 1") — last known-good commit
**Subject:** the entire uncommitted working tree (~4,900 diff lines, 18 modified + 6 new files)
**Status:** review COMPLETE — all 8 passes done. The final 6 passes ran 2026-07-26 (workflow
`wf_7659db05-161`): 12 findings filed, each adversarially verified → **6 confirmed / 6 refuted**.
See sections 3b (confirmed), 3c (refuted), and 7 (overall verdict).

---

## 1. Verified facts (done, no need to redo)

| Check | Result |
|---|---|
| `dotnet build src/BizHawkNetplay.Tool` | succeeds, 0 warnings, deploys to BizHawk ExternalTools |
| `dotnet test tests/BizHawkNetplay.Core.Tests` | **177/177 pass** (ChatGPT claimed 165 — stale, predates the lobby-delay work) |
| C# 9 constraint | no violations observed in reviewed files |

Regenerate the diff any time with:

```
git diff HEAD > rewrite.diff
```

---

## 2. Claims audit — COMPLETE

All 17 of ChatGPT's stated changes were checked against the code with file:line evidence.
**15 verdict "implemented", 2 verdict "partial", 0 missing.** The design is real, not narrated.

Spot-verified highlights: generation stamping + rejection on every ingress path (input, checksum,
pacing, READY/GO, resync); `beforeReady` ordering so joiners build the driver before replying;
per-peer pacing sample tracking; monotonic clocks at all watchdog sites; route grouping/failover;
attempt tokens rechecked before every effect; checksum map keyed `(generation → frame → sourcePort)`.

### The 2 partial claims — both worth acting on

**P1. Protocol is v6, not v5 (claim 2).**
[`NetplayToolForm.cs:31`](src/BizHawkNetplay.Tool/NetplayToolForm.cs#L31) — `private const int Protocol = 6;`
(HEAD was 4). v6 added lobby RTT probing. Mismatches are refused cleanly by
`SessionNegotiator.cs:67-69`, so this is **not a defect** — but ChatGPT's summary understated its own
wire break. Anyone documenting the release should say v6.

**P2. Initial join-state transfer is unbounded (claim 6).** ← *the one real robustness gap found*
Resync/reconnect transfers correctly declare size and carry both transfer and apply deadlines
(`StateReceiveDeadlineTicks` 2182-2189, `StateApplyDeadlineTicks` 2176-2180, size-scaled
`StateTransferTimeoutMs` 3850-3855). But the **first** join deliberately sets `ReceiveTimeout = 0`
after auth ([`NetplayToolForm.cs:1449-1457`](src/BizHawkNetplay.Tool/NetplayToolForm.cs#L1449-L1457)),
so a joiner waiting for WELCOME/state can hang **forever** if the host stalls mid-transfer — only
escapable by manually clicking Disconnect. The blanket claim "state transfers have bounded
deadlines" does not hold for the join path.

---

## 3. Findings filed so far

**F1 — `LobbyDelayPolicyTests.cs:58` — tautological frame-duration test (low).**
`UsesActualConsoleFrameDuration` picks two scenarios that both expect `4`:
`ceil(50/20)+1 = 4` and `ceil(50/16.67)+1 = 4`. If `Choose` regressed to hard-coding 60 Hz and
ignoring the `frameMs` parameter, the test still passes. Fix: use RTT 150 ms, where 50 Hz → 5 and
60 Hz → 6.

**F2 — `HandshakeTests.cs:461` — forced-rollback depth gate never isolated (low).**
`Handshake.cs:118-119` gates the client on **two** conjuncts: `clientPrefs.WantRollback` AND
`MaxRollbackDepth >= RollbackDepthThreshold` (6). The test's second scenario flips *both* at once,
so lockstep is produced by the `WantRollback` conjunct alone; the depth conjunct is untested. A
client with `wantRollback: true, depth: 2` is the missing case.

**F3 — INVESTIGATED AND REFUTED as a regression (low, pre-existing).**
A killed agent suspected a "writer-thread leak" in `OnPeerLinkLost`. Confirmed the mechanism is
real: `OnPeerLinkLost` does `_peers.Remove(link)` + `link.Tcp.Close()` but never sets
`link.WriterRunning = false`. The reader dies (blocked read throws on close) but the **writer** keeps
spinning on `OutboundSignal.WaitOne(250)` forever, and because the link left `_peers`,
`TeardownNetwork`'s join loop (3586-3595) never reaps it. Leaks one thread + one event handle per
dropped peer.
**However — HEAD does exactly the same thing** (`git show HEAD:...` → `_peers.Remove(link)` with no
`WriterRunning = false`). Pre-existing, not a rewrite regression. Threads are `IsBackground`, so
EmuHawk still exits. Low priority; fix by setting `link.WriterRunning = false` before removal.

---

## 3b. Confirmed findings from the final 6 passes (all adversarially verified)

**F4 — HIGH (pre-existing at HEAD) — `FrameDriver.cs:308` — rollback can freeze both sides
permanently after a ~8-datagram one-way UDP loss burst.**
Three mechanisms compose: (1) there is no retransmission of an aged input gap — the redundant send
window R = max(8, 2·delay+1) (`FrameDriver.cs:110`) keeps sliding on the unstarved sender, so after
~8 consecutive lost datagrams (~133 ms at 60 fps) the lost frame G is evicted and never sent again;
(2) once the starved peer hard-cap-stalls at N = G+cap+1, the drop rule at line 308 sets the
acceptance floor to N−cap = G+1, so G would be rejected even if resent; (3) the liveness stamp at
line 299 is refreshed by every well-decoded (even useless redundant) frame, so the 8 s
`UdpLostAfterSeconds` watchdog never fires, checksums stop advancing so no resync triggers, and TCP
pings keep the link "healthy". Both emulators sit at "stalling — waiting for remote input" until
manual disconnect. Lockstep is immune (the behind peer stalls at G itself, keeping G in the window).
**Verifier confirmed every step, but corrected attribution: all load-bearing lines are behaviorally
identical at HEAD** — pre-existing, left in place by the rewrite. Kept high because the shipped
config (rollback, delay 1, real internet) hits it under a plausible ~130 ms burst.
Fix directions: NAK/retransmit-on-gap; or floor acceptance at the peer's confirmed frontier instead
of `CurrentFrame − window`; or make the watchdog track *frontier progress*, not any-decoded-frame.

**F5 — MEDIUM (new in rewrite) — `NetplayToolForm.cs:2188` — survivor resync receive deadline is
one transfer phase shorter than the host's own healthy pipeline.**
Survivor budget = waitSeconds + 2·P + 5 s (P = 10 s + bytes/200 KiB/s), armed at ResyncBegin. But
after a late rejoin the host legitimately runs **three** sequential ~P stages: state send to
rejoiner (3306), rejoiner import + READY wait (3309), then the survivor's transfer queued only in
`FinishReconnect` (3369). With a large state (~4 MiB → P ≈ 30 s) and a rejoin near the end of the
60 s window, the survivor's `CheckLinkTimeouts` (2145) EndSessions the whole session seconds before
recovery completes. Nothing re-arms the deadline. HEAD had no receive-deadline machinery at all.
Fix: budget 3·P (+ slack), or re-arm the survivor deadline when its own transfer actually begins.

**F6 — MEDIUM (new in rewrite) — `MeshUdpTransport.cs:241` — single-candidate send can stay pinned
to a dead path for the full 8 s alive-window.**
The rewrite changed `Send()` from HEAD's send-to-every-candidate to one selected candidate. A dead
candidate stays "live" for `AliveWindowMs` = 8000 ms and its stale low RTT keeps winning
`SelectSendCandidate` over a confirmed-live fallback (LAN 1 ms beats public 20 ms). No
outbound-failure detector exists; repunch triggers only on *inbound* silence. Asymmetric LAN-path
death → outbound input black-holed for 8 s while the peer's `UdpLostAfterSeconds` (also 8 s) races
it — the session can die with a working failover path live the whole time.

**F7 — LOW (pre-existing, narrowed by rewrite) — `NetplayToolForm.cs:1220` — cancelled host attempt
can leave a stale `_listener` reference.**
Microseconds-wide race: if `TeardownNetwork` completes between the token check (1213) and the
assignment (1220), the stale-token exit stops the listener but never nulls the field. Next
`EndSession` (e.g. `Restart()` on ROM load) then fails the idle fast-path gate and runs one spurious
full teardown, including `Unpause()` on a possibly deliberately-paused emulator. Self-heals after
that pass. HEAD had the same window in worse form.

**F8 — LOW (new in rewrite) — `MeshUdpTransport.cs:287` — `RequestRepunch` reroutes healthy
outbound input to a dead candidate.**
Repunch (fired every ~1 s after 1.5 s of *inbound* input silence) clears `_alive` for **all**
candidates, so `SelectSendCandidate` falls back to `Candidates[0]` — the pre-NAT advertised
endpoint, typically unreachable behind a port-rewriting NAT. Result: recurring ~250 ms outbound
holes (> the ~133 ms redundancy window) every repunch cycle; in a true one-way joiner→host outage,
punch acks travel the dead direction so the host's outbound stays black-holed for the whole outage —
converting a one-sided stall into a mutual one. Harmless at HEAD (send-to-all ignored liveness).

**F9 — LOW (regression for 3+ players) — `MeshUdpTransport.cs:218` — joiner pacing RTT collapses to
host-only TCP ping when any route is unmeasured/stale.**
`TryGetWorstRttMs` now returns false unless *every* route has a live measured candidate; the TCP
fallback (`WorstPingMs`) only covers the host link on a joiner (star topology). During a peer's
path-recovery window (NAT rebind, repunch), a joiner's reported worst RTT drops from e.g. 180 ms to
15 ms and mis-sizes the rollback soft cap. Verifier corrections: the session-start window behaves
the same as HEAD (not a regression there), and the harm direction is *extra time-sync
stalls/stutter*, not deeper rollbacks. Transient, 3+ player joiners only.

## 3c. Refuted findings (verified NOT rewrite defects — do not re-file)

- **One failed rejoin post-greet kills the session** — real behavior, but the identical
  single-attempt policy exists at HEAD; rewrite only added guards around it.
- **Punch-path bring-up has no deadlines** (filed independently by two reviewers) — real unbounded
  waits, but `ReliableUdpStream` is unmodified and HEAD's punch path was identically unbounded; the
  new lobby RTT probe is bounded by the reliable-layer dead-link detector (~1 min). The remaining
  gap is "the new TCP-only `AbsoluteSocketDeadline` wasn't extended to the punch path" — a
  hardening opportunity on a pre-existing exposure, same family as known finding (a)/P2.
- **Host never drains UDP during the reconnect wait** — same structure at HEAD; backlog (~few
  hundred KB) drains in well under a second during resync's 128-datagram/2 ms pump.
- **Time-sync yield absorbed by wall-clock debt** — mechanism real but not a defect; the math gives
  exactly one frame of giveback per debt unit (the reviewer's proposed fix would double-charge).
- **FPS sample spans resync freezes → false "CPU-bound" flag** — identical machinery and gaps at
  HEAD; cosmetic only (≤500 ms wrong status string). Trivial fix if ever wanted: restart
  `_fpsClock`/`_fpsCount` in the resume paths.

## 4. Coverage map

| Area | Reviewer | State |
|---|---|---|
| 17-claim audit | claims-audit | ✅ complete |
| Test quality / LobbyDelayPolicy integration | tests-lobby | ✅ complete (F1, F2) |
| Session lifecycle: barriers, resync, reconnect, teardown | form-session | ✅ complete (F5; 1 refuted) |
| Networking glue, generation plumbing, thread safety | form-net | ✅ complete (F7; 1 refuted) |
| Pacing, frame scheduler, watchdogs, rendering | form-pacing | ✅ complete (2 refuted, 0 confirmed) |
| UDP mesh, routes, failover | mesh | ✅ complete (F6, F9) |
| Handshake codec, auth, control channel | handshake | ✅ complete (1 refuted, 0 confirmed) |
| FrameDriver, RollbackStrategy, codecs, generation semantics | sync | ✅ complete (F4, F8) |

Final 6 passes: workflow `wf_7659db05-161` (2026-07-26), 6 reviewers + 12 adversarial verifiers,
~496k subagent tokens. Per-agent results in that run's `journal.jsonl` under
`…/subagents/workflows/wf_7659db05-161/`.

---

## 5. Overall verdict

**The rewrite introduced no high-severity defects.** The only high-severity confirmed finding (F4,
the rollback loss-burst freeze) is pre-existing at HEAD — the rewrite neither caused nor fixed it.

Genuine rewrite regressions, all in one family plus one:

- **The mesh single-path family (F6, F8, F9):** moving `Send()` from send-to-every-candidate to
  single-best-candidate without adding any *outbound*-failure detection is the rewrite's one real
  design gap. F6 (8 s dead-path pinning, can race the session-killing watchdog) is the priority;
  F8 and F9 are smaller consequences of the same change.
- **F5:** the survivor resync deadline is one transfer phase too short vs the host's own pipeline —
  only bites with large states + slow links + late rejoins, but it kills the whole session when it
  does.

Recommendation: the rewrite is sound enough to commit once F5 and F6 are fixed (F8 likely falls out
of the same fix as F6 — don't fall back to `Candidates[0]` when liveness is cleared; keep the last
known-good candidate). F4 should be tracked as its own pre-existing issue (a NAK/retransmit path —
the "M2 feature" the code comment already anticipates). Also still open from the earlier passes:
P2 (unbounded initial-join state wait), F1/F2 (weak tests), F3 (writer-thread leak, pre-existing).
Real-play validation of the lobby auto-delay (section 6) remains untested.

**Update (2026-07-26, later the same day): F4, F5, F6 and F8 are FIXED** — gap-request retransmit
path + drop-floor grace in `FrameDriver`/`InputPacketCodec`, three-phase survivor deadline in
`NetplayToolForm`, freshness-based send selection + last-known-good fallback in
`MeshUdpTransport`. 181/181 tests pass (4 new, including a loss-burst regression test verified to
fail without the F4 fix). Everything still open is tracked in `KNOWN-ISSUES.md` at the repo root.

---

## 6. Separate thread: the RTT>100 ms "jumping"

Unrelated to defects — this is ordinary rollback correction, correctly diagnosed by ChatGPT. The
lobby auto-delay feature is **already built and wired**, contrary to it having been interrupted:

- `NetplaySettings.cs:23-24` — `AutoDelay = true`, `AutoDelayMax = 8`, both persisted
- `NetplayToolForm.cs:987-988, 1130-1137` — host measures lobby RTT and selects before GO
- `LobbyDelayPolicy.Choose` — `ceil((RTT/2)/frameMs) + headroom`, headroom 1 for rollback / 2 for
  lockstep, clamped to `automaticMaximum`, with `manualFloor` as a floor so a peer asking for more
  is never overridden

At 100 ms RTT / 60 Hz this yields `ceil(50/16.67)+1 = 4` frames. Sane. **Untested in real play** —
worth a two-player session before trusting it.
