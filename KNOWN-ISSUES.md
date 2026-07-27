# Known issues

Open findings carried out of the 2026-07-26 review of the AI rewrite (full context in
`REVIEW-HANDOFF.md`). Fixed items move to the bottom rather than being deleted, so the history of
what was addressed — and how — stays greppable.

## Open

**KI-8 (validation) — lobby auto-delay is untested in real play.**
`LobbyDelayPolicy` + host RTT probing are wired and unit-tested but have never been exercised in a
real two-player internet session. Worth one session before trusting the automatic selection.

**KI-9 (design note) — UDP liveness watchdog cannot see a "flowing but useless" input path.**
`FrameDriver` stamps `_lastRemoteInputStamp` for every well-decoded frame, including redundant
resends that can never advance the frontier, so `CheckUdpInputProgress` measures datagram arrival,
not progress. The F4 gap-retransmission fix removes the known permanent-freeze scenario, but if a
future change reintroduces an unfillable gap the watchdog will again sleep through it. If it ever
matters: track frontier progress per port instead of decode activity.

## Fixed (2026-07-26)

**F3 / KI-4 (low, pre-existing) — `OnPeerLinkLost` leaked the writer thread.**
The reconnect-wait drop path now clears `link.WriterRunning` and pulses `OutboundSignal` before
removing the link from `_peers`, mirroring `TeardownNetwork` — no more one leaked background
thread + event handle per dropped peer.

**F1/F2 / KI-5 (low) — two weak tests strengthened.**
`UsesActualConsoleFrameDuration` now uses RTT 150 ms, where 50 Hz (5) and 60 Hz (6) genuinely
differ; the forced-rollback test gained the `wantRollback: true, depth: 2` scenario that isolates
the depth conjunct.

**KI-6 (low, pre-existing) — punch-path bring-up had unbounded waits.**
`ReliableUdpStream` now supports `ReadTimeout` (NetworkStream semantics: one window per Read for
the first byte; `IOException` on expiry). The punch handshake runs bounded — the host scaled by the
state it is about to send, the joiner at the handshake window — and after GO the punch channel
reverts to unbounded idle with the same started-frames-must-finish body bound as the TCP path. A
peer that keeps the reliable layer ACKed while withholding application bytes can no longer hold
the handshake thread forever.

**KI-7 (cosmetic, pre-existing) — first FPS sample after a freeze read ~0.**
`RebaseFrameSchedule` — the common resume point for resync, reconnect, and release paths — now
restarts the FPS sample clock, so the status line no longer flashes "CPU-bound" after a pause.

**P2 / KI-2 (medium) — initial join-state transfer was unbounded.**
`ControlChannel` gained an optional per-frame progress bound (`BodyReadTimeoutMs`): the wait for a
frame's first byte stays unbounded (a joiner legitimately idles for minutes while the host's lobby
fills), but once a frame's header has arrived, its body reads run under a size-scaled timeout. The
joiner arms it right where it used to set `ReceiveTimeout = 0`, so a host that dies mid-WELCOME/
state now fails the join with an error instead of hanging until a manual Disconnect. The punch
path's `ReliableUdpStream` doesn't support read timeouts, so the hook is inert there (KI-6 remains
the tracking item for that path).

**F9 / KI-1 (low) — joiner pacing RTT collapsed to host-only TCP ping in 3+ player games.**
`TryGetWorstRttMs` now falls back to a route's last stored (stale) RTT when its candidates have
all gone quiet, instead of failing the whole aggregate; only a route that has *never* been
measured still returns false (the caller then correctly uses its complete TCP sample set). The
initial-measurement conservatism is preserved; the mid-session collapse during a peer's
path-recovery window is gone.

**F7 / KI-3 (low, pre-existing) — cancelled host attempt could leave a stale `_listener`.**
`HostThread`'s `finally` now CAS-nulls `_listener` (only if it still points at this attempt's
listener) on every stale-attempt exit, closing the microseconds-wide race with `TeardownNetwork`
that caused one spurious full teardown — including an `Unpause()` — on the next `EndSession`.

**F4 (high, pre-existing) — rollback froze both sides permanently after a one-way UDP loss burst.**
Fixed in `FrameDriver` + `InputPacketCodec`: (1) new gap-request datagram (type 2, generation-
stamped) — when a remote port's confirmed frontier falls a redundancy-window behind, the starved
peer asks the port's owner to re-send from the first missing frame, served from a new ~240-frame
local-input retransmit history; (2) the too-old drop floor gets one frame of grace below the
rollback window, because a hard-cap stall at frame N waits for exactly frame N − window − 1, which
the old rule rejected. Regression test: `RollbackIntegrationTests.
OneWayLossBurst_RecoversViaGapRetransmission_InsteadOfFreezingForever` (verified to fail without
the fix). Old builds ignore the new datagram type cleanly.

**F5 (medium) — survivor resync receive deadline was one transfer phase shorter than the host's
own healthy pipeline.** `StateReceiveDeadlineTicks` now budgets three phases (WELCOME/state send to
rejoiner → rejoiner import/READY → survivor transfer), matching the host's sequential socket
deadlines.

**F6 (medium) — input stayed pinned to a dead candidate for the full 8 s alive window.**
`MeshUdpTransport` send selection now prefers candidates heard from within a 2.5 s fresh window
(keepalive cadence raised 3 s → 1 s to support it), so a silently-dead path fails over in ~2.5 s
instead of racing the 8 s UDP-lost session watchdog. Test:
`InputFailsOverToFreshSibling_WhenSelectedPathGoesQuiet`.

**F8 (low) — `RequestRepunch` rerouted healthy outbound input to the unreachable first-advertised
candidate.** Selection now remembers the last candidate input was actually sent through per peer
and falls back to it while the liveness table is cleared. Test:
`RepunchFallsBackToLastKnownGoodCandidate_NotFirstAdvertised`.
