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

**KI-9 (design note) — the UDP liveness watchdog measures datagram arrival, not progress.**
`FrameDriver` stamps `_lastRemoteInputStamp` for every well-decoded frame, including redundant
resends that can never advance the frontier, so `CheckUdpInputProgress` sees a "live" port even
when input is useless. The gap-retransmission path (v0.10.0) removes the known permanent-freeze
scenario this enabled; nothing to do unless that path ever regresses. If it matters again: track
per-port frontier progress instead of decode activity.
