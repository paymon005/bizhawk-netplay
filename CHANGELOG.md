# Changelog

Per-release notes are on the [Releases](../../releases) page. This file records what matters across
releases: **which versions can play with each other**, and what each protocol bump changed.

## Protocol compatibility

The handshake refuses a protocol mismatch. Releases sharing a protocol number mix freely; releases
that do not will refuse each other with a clear message, which is intended behaviour rather than a
fault — the alternative is a session that appears to work and silently loses input.

| Releases | Protocol | Why it changed |
|---|---|---|
| v0.28.0 – v0.28.1 | **17** | The joiner's opening HELLO is now the mirror of the host's challenge — protocol version, nonce, UDP port and public candidate, nothing else — with its identity following only once the host's password proof has verified. v0.27.0 closed this leak on the host side only, so both directions of the opening sequence have now changed and the two builds disagree about the message shape rather than about a value. |
| v0.27.0 | 16 | The host's opening HELLO is now a challenge — protocol version and nonce, nothing else — and its identity follows only once the joiner's password proof has verified. A v0.26.0 peer sends its whole identity up front and expects the same back, so the two disagree about the message sequence rather than about a value, which is what the version check catches first. |
| v0.26.0 | 15 | The desync checksum reads memory differently on some cores: waterbox domains moved from a 1/16 stride sample to the whole domain, and the Hawk cores' byte-array domains are hashed directly. The value crosses the wire, so a mixed pair would report a phantom desync every interval — same rule as v10. |
| v0.24.0 – v0.25.0 | 14 | WELCOME carries per-seat mesh tokens and peers announce themselves with them over UDP. An older build sends no token, so its packets stay unroutable to anyone whose NAT rewrote the source port — silent one-way input loss rather than a refusal, hence the bump. |
| v0.21.0 – v0.23.0 | 13 | Resync and reconnect states are deflated on the wire. |
| v0.20.0 | 12 | The lobby measures every UDP mesh edge and the host publishes the settled delay in its own control frame (`MeshRtt` / `InputDelay`), which an older build neither sends nor expects. |
| — | 11 | Changed what the advertised rollback depth *means* — measured against the model the session actually runs, with snapshots elided on confirmed frames — and the threshold peers compare it against. Both ends must agree on both, or one could negotiate rollback while the other negotiated lockstep. |
| — | 10 | Main memory hashed with FNV-1a over 32-bit words, sampled with a rotating stride on domains too large to read whole. That value crosses the wire, so a bump turns a build mismatch into a clean refusal instead of a phantom desync every interval. |
| v0.10.0 | 6 | Every session and timeline carries a `(session ID, epoch)` generation stamped on input, checksums, pacing, READY/GO and resync. |
| v0.8.0 | 4 | Session passwords. |

## Notable releases

### v0.28.1 — the ninth gap request

**Wire-compatible with v0.28.0** (still protocol 17), and everyone on v0.27.0 or v0.28.0 should
take it: it fixes a permanent session freeze that both of those releases shipped.

When a loss burst is wider than the redundant window, the frame a peer is missing has slid out of
the sender's live window and can only come back through a gap request. Answering one is metered —
eight per 50ms — because a small request produces a full window in reply, and a peer stuck in a
request loop would otherwise become an amplifier. The meter is meant to be a rate. It was behaving
as a lifetime quota: the window-reset comparison ran against a `long.MinValue` sentinel, and since
the clock starts near zero, `now - long.MinValue` overflows *negative*, so the reset never fired.
After the eighth serve of a session, that peer refused every gap request for the rest of it.

The result is not a slow recovery but a permanent one. Both peers sit at their prediction caps
forever — one asking every 50ms, one refusing — which is exactly the freeze the gap-request path
exists to prevent.

It was caught by CI and not by local runs, for a reason worth recording: the integration test drove
one simulated tick per 1ms sleep, compressing the tick-to-wall-clock ratio sixteenfold against a
real 60fps session, so the whole recovery finished inside the first budget window and the ninth
request never mattered. The test now sleeps a frame period, which is the honest ratio and what CI's
coarser timer was effectively already doing. A direct regression test asks that twenty-four
requests spread over several windows be answered more than eight times.

### v0.28.0 — recovery paths that recover, and the heavy-core costs behind them

**Protocol 17 — everyone must update.** A v0.27.0 peer and a v0.28.0 peer refuse each other at the
handshake. v0.27.0 stopped the *host* from handing its identity to anyone who opened a socket; the
joiner was still sending its own — ROM hash, core, sync settings, and on N64 a filesystem path
containing the Windows username — as its very first frame, to whatever address it was told to dial.
Addresses get swapped over chat, so that is a real way to collect them. The joiner now opens with
the mirror of the host's challenge and its identity waits for the host's proof.

Comes out of a model review of the networking and the frame path. Three groups:

**Recovery paths that did not recover.** For a peer behind a symmetric NAT the only working address
is the one we learned by observing it — never among the addresses it advertised. Two separate places
forgot that. The last-known-good send path required its anchor to be an advertised candidate, so
eight seconds of ack loss stopped input to that peer *entirely* until a probe re-proved the path;
and the per-peer re-punch cleared liveness for every advertised candidate except the one address
that had actually gone quiet, while logging that it was recovering. Separately, the reliable-UDP
stream killed its own retransmit thread the moment it was closed, so a lost final message or a lost
FIN was never resent and the peer misreported a clean refusal as a network timeout.

**Handshake edges.** STUN discovery ran an unbounded DNS lookup on the host's lobby thread, so a
blackholed resolver could hold the lobby past every joiner's deadline while the host was fine. Two
of the three greet paths stored a peer's claimed public address without the credibility check the
others enforce — enough for one authenticated peer to aim the whole session's UDP at a third party.
The punched greets had no deadline bounding authentication as a whole, so one peer dribbling a byte
before every timeout could hold the single lobby thread indefinitely.

**Heavy cores.** A rollback correction that reached across a checksum boundary used to throw away
the cached hash and re-fetch the state — save, load, hash, load back, 18.4ms on N64 — most often on
exactly the high-latency links where corrections are deepest; it now re-hashes in place for free.
The catch-up burst that repays a hitch required the next frame to be fully confirmed, which under
rollback at any competitive input delay is never true, so the sessions that generate the debt were
the ones that could never repay it. The snapshot spacing is now solved per core from its own
measured save and frame costs instead of one constant taken from N64. And the cost cap that bounds
prediction is seeded from the capability probe, so the first deep repair of a session is no longer
the thing that discovers what a repair costs.

Two things this release does **not** change. The ~66 ticks/s ceiling under a paused EmuHawk is now
*documented as unreachable* rather than merely unexplained: the sleep is hard-coded, its gates are
recomputed every loop iteration, and every flag that would open them also makes EmuHawk step the
core itself — double-stepping every frame, which is a guaranteed desync. And on a heavy core with
input delay below the link's latency, snapshot elision still cannot fire; the session now says so,
in one line, naming the trade rather than making it for you.

### v0.27.0 — what a stranger can reach before they have proved anything

**Protocol 16 — everyone must update.** A v0.26.0 peer and a v0.27.0 peer refuse each other at the
handshake. The host's opening greeting is now a challenge rather than its whole identity, so the
two builds disagree about the message sequence; the version check catches that first and says so.

Comes out of a security review of what an open port exposes. The headline is a second, separate way
to point this machine at a stranger — v0.26.0 closed one, and missed this one. A single 22-byte UDP
datagram naming any address turned on the full input stream toward it, about 72 KB/s, from someone
who had sent 22 bytes and nothing else, re-aimable at will. Alongside it, the host's greeting no longer
hands its ROM, core, sync settings and — on N64 — a path containing the Windows username to anyone
who opens a socket; a peer's claim about its own public address is checked against where it actually
reaches you from; and several places where a peer chose how much this tool wrote to your disk are
now bounded.

Two things this release does **not** change, both stated plainly in the notes: sessions with no
password remain open by design, which makes the identity split a speed bump rather than a gate on
them; and a joiner still imports a savestate from its host, which on waterbox cores reaches
BizHawk's own state reader. That one is upstream and is recorded as KI-13.

### v0.26.0 — a crash, an amplifier, and a relay that talked to itself

**Protocol 15 — everyone must update.** A v0.25.0 peer and a v0.26.0 peer refuse each other at the
handshake, which is intended rather than a fault: the desync checksum now reads whole memory
domains on cores where it previously sampled one word in sixteen, and that value crosses the wire.

- **EmuHawk could be crashed by loading a ROM mid-session.** The hook that lets a tool tear down
  first is skipped entirely when "Supress 'Ask Save Changes'" is ticked, so nothing stopped the
  frame clock and any modal on the way into the next ROM — missing firmware, the archive chooser —
  pumped messages that stepped a core EmuHawk had already disposed. On a waterbox core that is a
  native access violation, not an exception. Both clock entry points now check.
- **One joiner's malformed UDP port took down the whole lobby**, because the value reached an
  IPEndPoint constructor outside the per-joiner handler whose entire purpose is that a bad greet
  costs its author a seat and nobody else.
- **The relay sent symmetric-NAT peers their own input back**, and never delivered their gap
  requests to the peer that could answer them — which surfaced as "retransmission could not repair"
  and ended sessions blaming the network. It reads the datagram's own addressing now instead of
  guessing from source addresses.
- **A peer could talk your machine into flooding a third party.** Nothing capped the candidate list
  a route could carry, and the punch loop probes every candidate four times a second. Routes are now
  bounded and refuse addresses the socket could never send to anyway; control frames get a per-type
  size ceiling so only savestates may be large.
- **A lost segment in a state transfer repairs in about a round trip** instead of waiting out a
  retransmit timer that doubles to 1.5s. On a lossy link that was the difference between a resync
  that completes and one that fails.
- **Sticky autofire and Virtual Pad clicks work during a session again** — both froze, and a stuck
  virtual-pad button looks exactly like a desync. An A/V capture now refuses a session rather than
  silently recording nothing.
- **The advisory that says your machine cannot afford rollback is now reachable** without ticking a
  debug checkbox, and the per-second telemetry stops accumulating session-long when it is off.
- Whole-domain checksums on waterbox cores: a divergence narrower than the old sampling stride is
  now caught at the next checksum rather than up to sixteen intervals later.

### v0.25.0 — a relay that was never installed, and a lighter frame

**Plays with v0.24.0.** Protocol 14 is unchanged: no wire format moved, and the two mix freely.

- **The host relay never actually carried anyone.** It was decided in the lobby and resolved against
  the peer list, which is not filled until later — so it was always handed nothing, while the log
  said it was relaying for N players. The peer it exists to rescue stalled instead, and the session
  died eight seconds later blaming the UDP path: the exact failure the relay was written to prevent.
  The decision still happens in the lobby; resolving it now waits for the peer list, and the log says
  how many routes actually resulted so a zero cannot hide again.
- **Pasting a connect code no longer undoes the joiner already in the lobby.** Admitting a punch
  target replaced the whole route table, discarding the routes the lobby had just installed and
  every endpoint's liveness and round-trip history with them. It merges now.
- **One bad packet could end a healthy session.** A corrupted frame number was recorded before the
  check that would have rejected it, and nothing ever lowered it — so a single datagram could leave
  the session asking forever for a frame that was never missing, and give up eight seconds later.
- **A throw on the UDP receive path no longer takes the network with it silently.** It ended the one
  thread that delivers input while everything else still reported healthy, so a fault in this tool
  arrived looking exactly like a fault in the network. It is now caught, counted, and named in the
  log next to the packet-refusal note — a full backlog, a refused packet, a thrown handler and a
  genuinely quiet peer are four different things and now read as four different things.
- **The socket keeps a real receive buffer** (256 KiB rather than Windows' 8 KiB) and stops treating
  an ICMP port-unreachable — which routine hole-punching provokes — as a receive error.
- **A savestate the core refuses to reload is now fatal and says so**, instead of leaving the
  emulator standing on the wrong frame under a generic session error.
- **Less work per frame.** The pad is read straight off the controller BizHawk resolves rather than
  from a dictionary rebuilt for it sixty times a second; sent input lives in one ring instead of five
  objects a frame; the controller only writes the buttons that changed; and the layout stopped
  recomputing constants. The audio ring copies in blocks rather than one sample at a time.
- **Changing netcode or delay no longer freezes EmuHawk** while the state is compressed — only the
  capture needs the emulator thread, and on a heavy core that was several hundred milliseconds.
- Removed a good deal of code nothing called, and a diagnostic checkbox whose question had been
  answered and acted on.

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
