# Known issues

Open findings carried out of the 2026-07-26 review of the AI rewrite (full context in
`REVIEW-HANDOFF.md`). Fixed items move to the bottom rather than being deleted, so the history of
what was addressed — and how — stays greppable.

## Open

**KI-1 (was F9, low) — joiner pacing RTT collapses to host-only TCP ping in 3+ player games.**
`MeshUdpTransport.TryGetWorstRttMs` returns false unless *every* route has a live measured
candidate, and the TCP fallback (`NetplayToolForm.WorstPingMs`) only covers the host link on a
joiner. During a peer's path-recovery window a joiner's worst-RTT can drop from e.g. 180 ms to
15 ms, shrinking the rollback soft cap and causing extra time-sync stalls. Transient; 3+ player
joiners only. Fix sketch: let `TryGetWorstRttMs` fall back to a route's last known (stale) RTT
instead of failing the whole aggregate, or have the joiner track per-peer UDP RTT history.

**KI-2 (was P2, medium) — initial join-state transfer is unbounded.**
After auth the joiner sets `ReceiveTimeout = 0` (`NetplayToolForm.cs` ~1449-1457), so a joiner
waiting for WELCOME/state hangs forever if the host stalls mid-transfer; only manual Disconnect
escapes. Resync/reconnect transfers are correctly bounded — extend the same size-scaled deadline
scheme to the first join.

**KI-3 (was F7, low, pre-existing) — cancelled host attempt can leave a stale `_listener`.**
Microseconds-wide race between `TeardownNetwork` and `HostThread`'s assignment; consequence is one
spurious full teardown (including an `Unpause()`) on the next `EndSession`. Fix: null `_listener`
on the stale-token exit path (~line 1221).

**KI-4 (was F3, low, pre-existing) — `OnPeerLinkLost` leaks the writer thread.**
`_peers.Remove(link)` + `Tcp.Close()` without `link.WriterRunning = false`: the writer spins on
`OutboundSignal.WaitOne(250)` forever and `TeardownNetwork` never reaps it (link left `_peers`).
One background thread + event handle per dropped peer. Fix: clear `WriterRunning` before removal.

**KI-5 (was F1/F2, low) — two weak tests.**
`LobbyDelayPolicyTests.UsesActualConsoleFrameDuration` picks scenarios where 50 Hz and 60 Hz both
expect 4 (use RTT 150 ms: 50 Hz → 5, 60 Hz → 6). `HandshakeTests` (~461) never isolates the
rollback-depth conjunct — add a client with `wantRollback: true, depth: 2`.

**KI-6 (hardening, low) — punch-path bring-up has unbounded waits (pre-existing at HEAD).**
The rewrite's `AbsoluteSocketDeadline` anti-slow-byte defense covers only the TcpClient paths. The
UDP-punch handshake runs over `ReliableUdpStream`, whose dead-link detector only fires on unacked
*outbound* data — a peer that acks everything but withholds application messages can hold the
handshake thread forever (recoverable via manual Disconnect). Extend the absolute deadline to the
punch path.

**KI-7 (cosmetic, pre-existing) — first FPS sample after a resync/reconnect freeze reads ~0.**
`_fpsClock`/`_fpsCount` are not reset on the resume paths, so the status line can flash
"CPU-bound" for ≤500 ms after a freeze. Fix: restart both (and `_actualFps = -1`) on resume.

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
