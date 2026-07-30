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

**KI-11 (validation) — 4-player rollback is measured, but on one machine.**
4-player **lockstep** was verified on hardware in July 2025. As of 2026-07-30 v0.20.0, 4-player
**rollback** has been run and measured too — four EmuHawk instances on one machine, Snes9x, PAL
(20ms frame period), delay 2, with simulated one-way UDP latency raised across five runs. Numbers
from the joiner side, since sim latency only delays what the instance it is set on *receives*:

| sim one-way (RTT) | max rollback depth | worst repair | repairs >5ms | stall | fps (target 50) |
|---|---|---|---|---|---|
| 50ms (100ms) | 3 | 2.1–4.5ms | 0 | 0% | 48–52 |
| 100ms (200ms) | 6 | 2.4–5.6ms | 0–2 | 0% | 48–52 |
| 150ms (300ms) | 10 | 4.5–6.4ms | 1–7 | 0–4% | 47–51 |
| 200ms (400ms) | 11 | 4.7–7.3ms | 0–10 | 0% | 49–51 |
| 300ms (600ms) | 17 | 8.4–10.1ms | 2–9 | 74–90%, then 0–7% | 24–51 |

**Rollback absorbed 400ms round-trip at input delay 2** — worst single repair ~7ms against a 20ms
budget, no stalling, full framerate, while re-simulating up to ~110 frames a second. It was
described as feeling fine throughout. At 600ms it stops absorbing: depth reaches the 16-frame ring
cap, `stalling — waiting for remote input` appears, and the session runs on the hard cap. That is
the designed behaviour and the log names it; delay 2 is simply not viable at 600ms (the tool's own
advice asked for 17). Checksums agreed across all runs.

*What that does and does not settle.* It settles that the sync layer, the repair loop and the
savestate pool hold up at four players on a light core, well past any plausible internet link. It
does not settle: four **separate machines** (this was one CPU, one GPU, one scheduler, and a
loopback mesh with no NAT); a **heavy core**, where a repair costs 6-9ms per frame instead of 0.6
and the same depths would be an order of magnitude dearer; or four players over a **real internet
path**, where the joiner-to-joiner edges are the ones nothing else can measure.

*What to read off a four-machine session when one happens:*
- **`mesh measured: X of Y direct path(s) answered`** at start. Anything short of X = Y means the
  delay below it covers only part of the mesh — on a real path that is the joiner-to-joiner edges.
- **The `Auto delay` line** — whether it picked above what the host's own links alone would suggest.
  If not, the mesh round found nothing and the feature is a no-op on that network.
- **`stall N%`** per peer. High on one peer means that peer's worst edge; high on everyone means the
  delay is under-covering the link.
- **Rollback depth and gate cost** on the two heaviest machines. This is where a light core and a
  heavy one diverge most.

## Fixed (2026-07-27, v0.11.3)

**KI-9 — the arrival-based watchdog could sleep through an unrepairable freeze.**
Redundant resends kept `CheckUdpInputProgress`'s silence measure near zero even when input could
never advance (confirmed in the wild by the first hotspot session). Rather than the false-positive
trap of watching frontier progress (a healthy peer stalled on a third player would be shot),
`FrameDriver` now tracks how long a *beyond-window hole* — the precise condition that only gap
retransmission can repair, which a healthy stall never produces — has persisted despite requests,
and `TryGetUnrepairedHole` surfaces it. The tool ends the session with a clear error at the same
8s the silence watchdog uses. Test drops the request datagrams so repair is impossible and asserts
the hole is reported (and clears when requests flow again).

## Fixed (2026-07-27, v0.11.0)

**KI-10 — punch admission into normal hosted lobbies (N-player, RemotePlay-style).**
`MeshUdpTransport` gained per-endpoint reliable control streams (`OpenControl`) carried on the
session's own socket, demuxed by a segment type and accepted only for explicitly opened endpoints.
The host just clicks Start Hosting; pasting a NAT'd joiner's connect code punches toward it from
the mesh socket and hands the confirmed stream to the lobby thread, which greets it exactly like a
TCP accept — same WELCOME/READY/GO, one code per joiner, TCP and punched joiners mixed freely. On
the joiner side, Join + UDP Punch targets the host's IP and produces the code; the session that
forms is a normal mesh session (input, resync, auto-delay — everything), with no TCP anywhere on
the punched link. A punched link that drops ends the session (no TCP rejoin path) rather than
holding the 60 s reconnect wait.

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
