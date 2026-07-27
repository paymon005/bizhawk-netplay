# Known issues

The living issue list for work not tracked anywhere else. Every finding from the 2026-07-26
adversarial review of the v0.10.0 rewrite (F1–F9, P1–P2, KI-1–KI-7) has been fixed — the details
live in the git history (`git log --grep KI-`, `--grep "review finding"`) and in the v0.10.0 /
v0.10.1 release notes.

## Open

**KI-8 (validation) — the v0.10.x recovery machinery is untested in real play.**
Session generations, rollback gap retransmission, mesh route failover, bounded state transfers,
and the lobby auto-delay are all unit-tested and loopback-tested but have never seen a real
internet path. Worth one deliberate two-player session (ideally with an induced mid-game drop and
rejoin) before trusting them; rollback with 3+ players and symmetric NAT remain untested generally.
*First attempt (2026-07-27, PC host + hotspot laptop, direct join, rollback @ 72 ms):* handshake,
punch, lobby RTT probe, and auto-delay (4) all worked; the session then hit the mutual soft-cap
freeze fixed in v0.10.2 (see Fixed below). Retest on v0.10.2.

**KI-10 (feature) — the connect-code punch session is 2-player only.**
3–4 players work over a normal TCP-hosted lobby (the host auto-punches the joiner↔joiner UDP mesh
legs), but that requires the HOST to be reachable (forwarded port or UPnP). A host who can't
forward at all can only run 2-player via codes. The N-player version of the RemotePlay-style flow
— host collects one code per joiner, each punched link carrying that joiner's control channel over
its own `ReliableUdpStream` on the host's one socket — is designed but not built.

**KI-9 (design note) — the UDP liveness watchdog measures datagram arrival, not progress.**
`FrameDriver` stamps `_lastRemoteInputStamp` for every well-decoded frame, including redundant
resends that can never advance the frontier, so `CheckUdpInputProgress` sees a "live" port even
when input is useless — confirmed in the wild by the 2026-07-27 session, whose 10+ second freeze
never tripped the 8s watchdog. The hole-evidence gap-request trigger (v0.10.2) removes the known
permanent-freeze scenarios; nothing to do unless that path ever regresses. If it matters again:
track per-port frontier progress instead of decode activity.

## Fixed (2026-07-27, v0.10.2)

**Mutual time-sync deadlock after early one-way loss** — found by the first real-internet session
(host frozen at "time-sync yield at frame 17" forever, joiner locked, no watchdog error). Chain:
(1) before the punch confirmed a path, input to the NAT'd joiner went to its unreachable pre-NAT
candidate, losing the session's opening frames; (2) the hole slid out of the sender's resend
window; (3) with time-sync active, both peers stall at their soft caps — far shallower than the
depth-based gap-request trigger — so the retransmit request never fired; (4) frozen-window resends
kept the liveness watchdog quiet (KI-9's blindness, confirmed in the wild). Fixes: the mesh
broadcasts input to every candidate until a path is first confirmed, and the gap request now also
fires on direct hole evidence (peer's newest frame more than a resend window past our frontier),
independent of stall depth. Reproduced by
`EarlyOneWayLoss_WithTimeSync_RecoversInsteadOfMutualSoftCapFreeze` (verified to fail pre-fix).
