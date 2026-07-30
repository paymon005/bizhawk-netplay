# Known issues

The living issue list for work not tracked anywhere else. Every finding from the 2026-07-26
adversarial review of the v0.10.0 rewrite (F1–F9, P1–P2, KI-1–KI-7) has been fixed — the details
live in the git history (`git log --grep KI-`, `--grep "review finding"`) and in the v0.10.0 /
v0.10.1 release notes.

## Open

**KI-8 (validation) — CLOSED 2026-07-30. Both recovery paths have now been watched in real play at
four players over the internet: resync after a desync, and drop-and-rejoin after an ungraceful break.**

*Superseded evidence (2026-07-30):* three players, three separate machines, over the open internet,
across several sessions of 20–30 minutes each:

| game | core / plugin | netcode | delay | outcome |
|---|---|---|---|---|
| Gauntlet II (SNES) | Snes9x | **rollback** | low (exact not recorded) | played fine |
| Mario Golf (N64) | various video plugins, incl. Rice | **lockstep** | — | played fine |
| Pokémon Stadium (N64) | Rice | **rollback** | ~5 | worked well |

That settles, on real hardware over a real internet path, at three players: the handshake, NAT
traversal, the full mesh, auto-delay, pacing, and **rollback on a heavy core** — the last of which
the To Do list had been pessimistic about, on the arithmetic that N64 sustains only ~3 frames of
depth. At delay 5 over the internet it was reported as working well. Note the register: this is a
player's judgement, not telemetry. No logs were captured from these sessions, so there are no
numbers here and none should be inferred.

**Desync recovery is now proven in real play (2026-07-30, 4 players, logs kept).** Host on home
broadband, three joiners behind mobile carrier-grade NAT, SNES/Snes9x, rollback delay 4:

- `mesh measured: all 12 direct path(s) answered` — the full four-player mesh over a real path.
- **Six deliberate desyncs** injected from a joiner via *Force desync (diag)*. Every one: the host
  detected the mismatch, shipped an authoritative state, all three peers applied it, and both sides
  logged `back in sync — recovery confirmed`. Checksums agreed continuously between injections.
- **Seven live settings changes** — input delay 4→2→4→6→2→1 and netcode both directions — each
  rebuilding the timeline with no disconnection.
- **Protocol 13 compression measured on the wire:** 421KiB captured, 85–93KiB transferred, a
  consistent 20–22%. Every transfer arrived intact; the post-resync `checksum frame 0` agreed each
  time, so decompression is byte-exact on a real link.

That settles resync: session generations, the authoritative state transfer, the bounded deadlines
and the generation gating all work at four players over the internet.

**Drop and rejoin is proven too (2026-07-30, same setup, logs kept).** The network was pulled from a
joiner mid-session — an ungraceful break, which is the only thing that reaches this path: a
*graceful* leave (Disconnect, or closing the tool) is handled at `Peers.cs` by ending the session
outright, deliberately, because the peer meant to go. What the host logged, in order:

```
P4 dropped (An existing connection was forcibly closed…) — holding the session; waiting up to 60s…
P3 applied resync epoch 2
P2 applied resync epoch 2
P4 reconnected — epoch 2, 421KiB baseline synchronized; resuming
checksum frame 0: all 4 agree
```

Survivors froze at the epoch boundary instead of timing out their UDP input, the returning player
re-joined over TCP into its held seat, and play continued with all four agreeing at frames 0, 300
and 600. The rejoiner's own log shows it as a normal Join — the seat is what the host holds, so the
player just clicks Join again.

*The limitation that surfaced while testing this, worth stating plainly:* **the hold covers exactly
one missing player.** A second drop while a reconnect is pending ends the session
(`RecoveryPolicy.OnPeerLost` → `EndSessionSecondDropDuringReconnect`), because advancing the epoch
again would make the reconnect boundary skip an epoch for survivors still on the prior one. That is
deliberate, but it means **peers sharing one connection cannot be recovered** — pull the link behind
two joiners and both go, so the host is freezing a survivor that is already gone. The message for
that case now names the peer rather than reporting the step that failed.

**The Rice plugin renders some games incorrectly** (visible on the N64 titles above). That is a
BizHawk video-plugin issue, not a netplay one — but it is the first real entry in the N64
settings profile 1.0 needs, and worth knowing before blaming the netcode for something on screen.
Peers must run the same plugin regardless: the handshake compares the core's sync-settings blob and
refuses a mismatch up front.

**KI-11 (validation) — FOUR-player is what's left; three-player is done on real hardware.**
See KI-8 for the 3-player internet sessions (SNES rollback, N64 lockstep and rollback, separate
machines). The single-machine caveat below applies to the **4-player** measurements only.
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
loopback mesh with no NAT); a **heavy core** *at four players*, where a repair costs 6-9ms per frame
instead of 0.6 and the same depths would be an order of magnitude dearer — though N64 rollback has
since been played at three players over the internet at delay 5 without complaint (KI-8), which is
the first evidence against the pessimistic reading; or four players over a **real internet
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
