# Changelog

Per-release notes are on the [Releases](../../releases) page. This file records what matters across
releases: **which versions can play with each other**, and what each protocol bump changed.

## Protocol compatibility

The handshake refuses a protocol mismatch. Releases sharing a protocol number mix freely; releases
that do not will refuse each other with a clear message, which is intended behaviour rather than a
fault — the alternative is a session that appears to work and silently loses input.

| Releases | Protocol | Why it changed |
|---|---|---|
| v0.24.0 | **14** | WELCOME carries per-seat mesh tokens and peers announce themselves with them over UDP. An older build sends no token, so its packets stay unroutable to anyone whose NAT rewrote the source port — silent one-way input loss rather than a refusal, hence the bump. |
| v0.21.0 – v0.23.0 | 13 | Resync and reconnect states are deflated on the wire. |
| v0.20.0 | 12 | The lobby measures every UDP mesh edge and the host publishes the settled delay in its own control frame (`MeshRtt` / `InputDelay`), which an older build neither sends nor expects. |
| — | 11 | Changed what the advertised rollback depth *means* — measured against the model the session actually runs, with snapshots elided on confirmed frames — and the threshold peers compare it against. Both ends must agree on both, or one could negotiate rollback while the other negotiated lockstep. |
| — | 10 | Main memory hashed with FNV-1a over 32-bit words, sampled with a rotating stride on domains too large to read whole. That value crosses the wire, so a bump turns a build mismatch into a clean refusal instead of a phantom desync every interval. |
| v0.10.0 | 6 | Every session and timeline carries a `(session ID, epoch)` generation stamped on input, checksums, pacing, READY/GO and resync. |
| v0.8.0 | 4 | Session passwords. |

## Notable releases

### v0.24.0 — symmetric NAT, named mismatches, sendable logs

- **A peer can be recognised at the address it really arrives from.** Every seat gets a 16-byte token,
  delivered in WELCOME over the already-authenticated control channel and announced over UDP
  alongside punch probes. A symmetric NAT hands out a different public port per destination, so the
  address such a peer learned from STUN is valid for the STUN server and nobody else; the token lets
  the receiver bind that seat to wherever its packets genuinely come from. Tokens are keyed by seat
  and outlive whoever is in it, so a rejoin on a new address is recognisable with no redistribution.
  **Not yet proven against a real symmetric NAT** — see `KNOWN-ISSUES.md` KI-12.
- **The lobby names the edges** it could not reach, and names any peer that could only be reached at
  an address it never advertised, instead of reporting a count.
- **A sync-settings mismatch names the settings** and both sides' values, rather than saying only that
  one exists. The hash still decides; the field list only explains.
- **The log is timestamped and written to a file** under `%APPDATA%\BizHawkNetplay\logs` from the
  moment you host or join, so a session can be sent to whoever is helping. An idle launch writes
  nothing.
- The session no longer re-draws a frame EmuHawk is about to draw anyway.

### v0.23.0 — audio ownership and mesh relay

- **Opening Config → Sound no longer kills audio** for the rest of the session. The dialog re-attaches
  EmuHawk's own audio provider and may replace the `Sound` object outright; ownership is now re-taken
  before every pump. Master mute works too, which it never had.
- Where a **joiner↔joiner UDP leg fails to open**, the host relays that leg rather than the pair never
  hearing each other. No external server — the host is already the rendezvous everyone reached.

### v0.22.0 — input and host commands

- **The tool window no longer steals your controller.** BizHawk refuses host input while an external
  tool has focus, so clicking this window mid-game stopped your pad. Fixed conditionally, the way
  TAStudio does it, so typing an IP still goes only to the box.
- Input capture reads `Joypad.Get`, the end of EmuHawk's own controller chain, instead of re-deriving
  the bind arithmetic.
- **A host loading a savestate takes every player with it** — the same resync a desync recovery uses.
  A joiner's load is refused.
- Adds **Watch Analog**, which reports every distinct value a stick actually delivers to the core.

### v0.21.0 — host integration and compression

- The session owns the emulator through BizHawk's own seams, from the moment the lobby opens rather
  than from GO: `BlockFrameAdvance` stops EmuHawk's run loop stepping the core, `IControlMainform`
  refuses Rewind and Reboot, and `BeforeQuickLoad` refuses Quick Load while leaving **Quick Save
  working normally**. Pause, rewind and run-in-background are snapshotted and restored exactly as
  found.
- Axes rest at each axis's own neutral rather than 0.
- The "My controls" remap compares control *names* rather than counts.
- Audio honours the volume slider and mute.
- Every savestate transfer is deflated on the wire.
- CI builds the shipping DLL against a hash-pinned BizHawk 2.11.1.

### v0.10.0 — generations and auto-delay

- Every session and timeline carries a **(session ID, epoch) generation** stamped on input, checksums,
  pacing, READY/GO and resync. Stale-generation packets are rejected on every ingress path, so a
  rebuilt timeline cannot be poisoned by the old one.
- The UDP mesh groups each peer's LAN and public endpoints as **routes**: all candidates probed, input
  rides the best live path, a silently-dead path fails over in ~2.5s.
- State transfers declare their size, with bounded size-scaled deadlines.
- The host **auto-selects input delay from lobby RTT**, capped, never lowering a manual ask.
- Rollback gains **gap retransmission**, so a loss burst that outruns the redundant window no longer
  freezes both players.

### v0.11.0 — punch admission into normal lobbies

Hosting and UDP punch became one flow. The host clicks **Start Hosting**; a joiner who cannot reach it
enters the host's IP, clicks **UDP Punch**, sends the code it gets, and the host pastes it to admit
them — into the same 2–4 player lobby as TCP joiners, over a reliable control stream on the session's
own UDP socket. One code per NAT'd joiner, no port-forwarding on their side.

### v0.9.0 — latency and liveness

- Audio cushion halved: a permanent 80 ms video→audio offset became 40 ms.
- RTT measured on the UDP path input actually rides, rather than on the TCP control link.
- Rollback time-syncs on a real **frame advantage** exchange, so the peer genuinely ahead yields
  instead of both guessing from a symmetric RTT.
- Per-link drop detection no longer goes blind during a resync.

### v0.8.0 — session passwords

Nonce challenge-response with a slow KDF: the password never crosses the wire and a captured proof
cannot be replayed. A refused joiner loses only its own connection; the host keeps hosting.

## Milestones

Feature milestones, for context on what was built when.

| Milestone | State |
|---|---|
| **M0 — Probe harness** | Done. Runs the rollback-feasibility probe and three API experiments. Validated on Genesis/GPGX. |
| **M1 — 2-player lockstep** | Verified on hardware (two EmuHawk instances, Genesis/GPGX + N64): real-time pacing, working audio, desync detection, configurable delay and packet redundancy. |
| **M2 — Hardening** | Live ping/RTT and delay hints, desync auto-recovery, alt-tab audio resilience. |
| **2–4 players** | Host picks the player count. Direct peer-to-peer **full mesh**: every peer sends straight to every other, so input is normally one hop from its author. Where a joiner↔joiner leg fails, the host relays that leg. 2P and 4P lockstep verified on hardware in July 2025; 3P verified on three separate machines over the internet in July 2026. |
| **M3 — Rollback** | Code-complete. GGPO-style `RollbackStrategy` behind `ISyncStrategy`, probe-gated and handshake-negotiated. Verified in real 3-player internet play on SNES and N64. |
| **M4 — NAT punch-through** | Code-complete. STUN + UPnP; **UDP Punch** carries a whole 2-player session over a reliable-over-UDP control channel; host-as-rendezvous auto-punches the 3–4P mesh legs. Cone NAT, plus symmetric NAT via per-seat tokens as of v0.24.0 — the latter not yet proven on a real path. |
