# Changelog

Per-release notes are on the [Releases](../../releases) page. This file records what matters across
releases: **which versions can play with each other**, and what each protocol bump changed.

## Protocol compatibility

The handshake refuses a protocol mismatch. Releases sharing a protocol number mix freely; releases
that do not will refuse each other with a clear message, which is intended behaviour rather than a
fault — the alternative is a session that appears to work and silently loses input.

| Releases | Protocol | Why it changed |
|---|---|---|
| v0.32.0 | **21** | The checksum's exclusions are measured instead of guessed, and its cadence is sized from what a hash costs. Peers exchange per-bucket memory hashes over the first boundaries of every generation (DivergenceReport, 25) and the host publishes the machine-dependent ranges as an exclusion mask (ExclusionMask, 26); WELCOME carries the session's checksum interval (`ckint=`); and the hash seed changed shape (a range list plus the mask identity). A v20 peer computes different values for identical states, so a mixed pair would report a desync that is not there. |
| v0.31.0 | 20 | A session outlives its players, a dead leg gets relayed live, and every post-auth control frame is authenticated. SeatVacated (23) and InputOutage (24) are new control types, WELCOME carries a `vacated=` line, and every frame after AUTH bears a truncated HMAC bound to its direction and position. A v19 peer sends none of it and would fail every integrity check, so the version refusal is doing exactly its job. |
| v0.30.0 | 19 | The desync checksum changed which bytes it reads, twice over. A memory domain that wraps a raw pointer in per-byte delegates — N64's RDRAM, and the reason its checksum used to sample a quarter of RAM by word — is now copied and hashed whole, so a v18 peer hashes a quarter of what this one hashes all of. And the span the video hardware is scanning out is skipped on every path, which is what lets N64 run above native resolution without disagreeing at every checksum. Either alone would make a mixed pair report a desync that is not there. |
| v0.29.0 | 18 | Three wire contracts moved at once. The mesh report names its silent edges, so the host relays exactly the broken joiner-to-joiner pairs instead of everything; port 0's input payload carries the console controls (Reset/Select/Pause/FDS, appended after the host pad's own); and the strided checksum's sampling offset is bit-mixed, so a v17 peer hashes a different slice of the same RAM. Any one of the three would desync or misparse a mixed pair. |
| v0.28.0 – v0.28.2 | 17 | The joiner's opening HELLO is now the mirror of the host's challenge — protocol version, nonce, UDP port and public candidate, nothing else — with its identity following only once the host's password proof has verified. v0.27.0 closed this leak on the host side only, so both directions of the opening sequence have now changed and the two builds disagree about the message shape rather than about a value. |
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

### v0.32.1 — corrections from an external review (protocol 21, mixes with v0.32.0)

An independent review of v0.31.0 found several things the internal one missed. No wire change, so
v0.32.1 and v0.32.0 still play together — but v0.32.0 masks memory automatically where this build
asks first, so run the same build on both machines for N64.

- **On a link core the checksum watched one Game Boy of four.** `MemoryDomainList.MainMemory` falls
  back to the first registered domain, and GBHawkLink/3x/4x/GGHawkLink register `Main RAM A/B/C/D`
  nominating none — so divergence on any machine but the first was invisible. Those cores are now
  refused until the checksum can cover every machine. This is the counterexample to the previous
  release's "no incorrect assumption found" verdict, which is retracted in KNOWN-ISSUES.
- **The integrity tag sat outside the deadline meant to stop hangs.** A peer that sent a complete
  body and stopped held the reader forever. Body and tag are now one timed read; queue accounting
  counts the tag too.
- **Ownership failed open.** The session claimed to own the timeline before proving frame advance
  was blocked and the savestate hooks were installed, logging and carrying on if either failed.
  Both are now proven first (BlockFrameAdvance is read back), a failure undoes its partial
  acquisition, and the session refuses with the reason.
- **Two of BizHawk's four per-frame input ticks were missing** — one-frame button overrides never
  expired and ordinary autofire phase never advanced. All four now run in upstream's order.
- **A punched drop ended 3-4 player sessions**; it now vacates the seat like any other leave. And
  outage reporting looked only at the worst edge, which serialized overlapping failures — every
  silent leg is now reported independently.
- **A desync now says who disagreed**, including when the host is the outlier about to overwrite
  three agreeing players with its own state. The authority policy is unchanged; the case is no
  longer invisible.
- **The N64 exclusion mask became opt-in.** Excluding memory is only sound while the excluded bytes
  are never read back, and Rice copies rendered data into RDRAM when emulated code reads
  framebuffer memory. Masking now requires both an explicit experimental opt-in and above-native
  rendering; measurement and address-level logging stay unconditional.

### v0.32.0 — protocol 21

**Protocol 21 — everyone must update.** The N64/heavy-core release: the desync checksum now
measures what it must not read, instead of guessing.

**Divergence learning (closes KI-15, supersedes KI-14's guess).** Right after every authoritative
rebuild all peers are byte-identical, so over the next three checksum boundaries each peer hashes
main memory in 256 buckets and ships the vector beside its checksum. Any bucket that disagrees
between peers standing on identical states can only hold machine-produced bytes — on N64 above
native resolution, the framebuffer the video plugin resolves back into RDRAM from the GPU. The host
takes the union across boundaries (which is what catches double-buffering — the two framebuffers
alternate roles, so any single look sees one of them) and publishes it as an exclusion mask every
checksum thereafter skips. Three properties keep it honest: it self-disables at native resolution
(nothing diverges, nothing masked, nothing changes); a real desync spreads through memory, blows
the 25% share cap and is refused rather than masked; and during the learn window a mismatch is
treated as the measurement rather than the emergency — which also breaks the resync loop
above-native N64 used to live in. Every desync report now names the disagreeing address ranges,
because every resync starts a learn round. The VI-register span from v0.30 remains only as the
pre-learn default.

**The checksum runs 5× more often where it is cheap.** The 300-frame interval dated from when a
hash was a 7-38ms hitch; the fast paths made it ~0.1-2ms and the interval never noticed. The host
now measures one hash at session start and sizes the interval to an amortized half-percent of the
frame period, clamped to [60, 600] — once a second on every fast-path core, so a desync is caught
seconds earlier with seconds less divergence to recover. The interval is a session agreement
(mismatched intervals would silently stop detection completing), so the host publishes one figure
in WELCOME and every peer quantizes to it.

*Still to validate on hardware:* a two-machine N64 session above native resolution, watching the
learn round conclude and checksums agree thereafter — see KI-14 in KNOWN-ISSUES.

### v0.31.0 — protocol 20

**Protocol 20 — everyone must update.** The 3-4 player robustness release: the three ways a
multiplayer session died that a 2-player session never would are gone.

**A session outlives its players.** A graceful leave used to end a 4-player session for everyone,
while an ungraceful drop politely held the seat for a minute — inverted severity. Now a joiner
leaving cleanly vacates its seat: the survivors are rebuilt onto one baseline with the seat empty
from frame 0 (the same authoritative-rebuild flow a settings change uses, spending no desync
budget) and play continues. The expired rejoin wait takes the same exit instead of killing a
session it held open for 60 seconds. A session now ends only when the host leaves, at 2 players,
or when a leave lands mid-recovery. The rule everything hangs off: a vacate applies only to a
fresh timeline — mid-frame it is a desync by construction, since peers hear about the leave at
different frames and the one player who could reconcile the difference is gone.

**A leg that dies mid-session gets relayed instead of ending the session.** The lobby has always
relayed joiner-to-joiner legs that never opened; a leg dying mid-game still killed the session at
the 8-second watchdog, with the rescue machinery sitting right there. Now the starving joiner
reports the silent seat at 3 seconds (InputOutage), and the host — after checking it has a proven
two-way path to both ends — carries the pair from then on. Installed once, never flapped: input is
keyed by (port, frame), so a revived direct leg makes the relay redundant, never harmful. The
watchdog stays armed for the one leg no relay can reach (the host's own). Decision rule in Core
(`RelayFailover`), fully unit-tested.

**Control frames are authenticated (KI-13's network half).** The password proofs already derived a
32-byte key and threw it away; it is now kept, and every frame after AUTH carries a truncated
HMAC-SHA256 bound to its direction and its position in the stream. An on-path party without the
password can no longer inject a Resync, tamper, replay, reorder or reflect — each fails loudly
into the ordinary link-loss path. With an empty password the key derives from public nonces, so
integrity then holds only against off-path injection; frames are authenticated, not encrypted,
either way. The local half of KI-13 (a savestate is a trusted format upstream) is unchanged.

### v0.30.0 — protocol 19

**Protocol 19 — everyone must update.** A v0.29.0 peer and a v0.30.0 peer refuse each other at the
handshake. Comes out of a review pass over the open findings, the N64 resolution problem and the
heavy-core hot paths, with BizHawk 2.11.1's own source (commit `bdddf4a`) read alongside to check
what was being assumed about it.

**The desync checksum is ~3× faster on N64 and covers four times as much.** BizHawk's N64 builds
every memory domain by asking mupen for a pointer and then wrapping it in per-byte peek/poke
lambdas that apply the core's `addr ^ 3` swizzle. The pointer is right there in the closure, but
invisible to a `Data`-property probe — so the checksum had been reading 8MiB of RDRAM one delegate
call per word, which is why it had to *sample*: ~7ms for a quarter of RAM, once every five seconds,
on the UI thread. Reaching that pointer puts the domain on the same memcpy the plain native cores
use: ~2ms for all of it. A hitch removed every checksum interval, and a narrow divergence is now
caught at the next checksum rather than whenever the sampling rotation happens to land on it.

The pointer is found by shape — exactly one `IntPtr` field plus an integer equal to the domain's
size — never by the compiler-generated closure name, which a recompile may renumber. Acceptance
depends only on the domain's type and size, never on what memory contains, because two peers must
take the same path or they would hash unlike byte sets. The copied block is spot-checked against
the domain on every hash, and a disagreement drops back to the old path permanently.

**A first move on N64 above native resolution — and an honest one.** The checksum now excludes the
span the video interface is scanning out, which is where the GPU-produced bytes that desync every
checksum above native were assumed to land. The machinery is right and tested. The span is not
expected to be sufficient: `VI_ORIGIN` names the buffer being *scanned out*, while the plugin writes
back to the one it just *rendered*, which in a double-buffered game is the other one. **Keep running
native.** See KI-14, which now states this plainly, and KI-15 for the measurement that replaces the
guess.

**Also in this release.** The wire format lost a second, unused encoder: `EncodeInput` was
production-dead and the tests were validating it instead of `BeginInputDatagram`, the one that
actually ships — a header change in the live encoder alone would not have been caught. It is now a
test helper built *on* the shipping encoder. `CapabilityProbe.SolveMaxDepth` collapsed from four
overloads to one. The framebuffer arithmetic lives in `Core` (`VideoFramebuffer`) rather than in the
adapter no test can reach, with eleven tests covering both directions it can be wrong in — a span
too small leaves GPU bytes in the hash, one too large silently blanks desync detection.

**KI-13 correction.** The finding still holds in full, but the fix is cheaper than it read:
`SessionAuth.ProofPair` already derives a 32-byte key from the password and both nonces and then
discards it, so a MAC over control frames needs the key kept and the frames framed — no new
exchange, no second KDF pass, no extra round trip.

### v0.29.0 — protocol 18

**Protocol 18 — everyone must update.** A v0.28.x peer and a v0.29.0 peer refuse each other at the
handshake. Comes out of two review passes — one over everything that changed for 3+ players since
v0.18.0, one external — plus the first real reports from untested cores (NESHawk with a FourScore,
Atari 7800, Lynx).

**Input coverage — two whole classes of controls were unreachable.**

- **The console buttons get a seat.** The per-port layouts define every name a session can inject,
  and the unprefixed player-0 controls were in none of them — so NOBODY in any session could press
  Reset, Select, Pause, a 7800 difficulty switch, an FDS disk swap, or a disc tray. On the 7800,
  Select is how most carts pick 1P/2P mode: sessions sat in the wrong mode and read as "the players
  share controls". Console controls now ride the tail of port 0's layout — the host presses them
  for everyone, and they apply from the synced stream on the same frame on every machine.
- **NESHawk + FourScore seats follow the game's pad numbers.** NESHawk numbers FourScore slots by
  plug (left = P1+P2), but the hardware serial — and every game — reads $4016 as pads 1 and 3 and
  $4017 as 2 and 4, which is how QuickNES names them. Seat 2 used to inject a pad no 2-player game
  polls, so that player's controls did nothing under NESHawk while QuickNES worked. Seats now map
  onto the game's numbering, derived from the same sync settings the handshake already compares.
- A 7800 unplugged port (one button literally named "P2 ") no longer counts as a usable seat, and
  single-player hardware (Lynx, GB) is refused with the real reason — there is no second port —
  instead of advice to configure one.

**The desync checksum got honest about its coverage.**

- **The stride rotation never rotated.** The sampling offset was `frame % stride`, checksum frames
  are multiples of 300, and 300 shares every factor of the strides in use — so the offset was 0
  forever and only 25% of N64 RDRAM was ever hashed, on every build since the stride existed. The
  offset is now bit-mixed and actually rotates (hence the protocol bump: a v17 peer hashes a
  different slice).
- **A catch-up burst crossing a 300-frame boundary silently skipped that comparison** — lockstep
  hashes the live state, and deferring past the boundary lost it. It now hashes mid-burst, while
  the state still stands on the boundary.

**The mesh learned which legs are actually broken.**

- **The relay carries named pairs, not players.** A joiner short even one measured edge used to get
  EVERYTHING relayed to it, doubling traffic on legs that worked and inflating the delay by a hop
  those legs never took. The mesh report now names its silent edges and the relay carries exactly
  the broken joiner-to-joiner pairs.
- **A renumbered seat gets a fresh token.** A pre-GO casualty renumbers survivors into freed seats,
  but the seat's token — and the endpoint peers had learned under it — outlived the move, so the
  seat's next occupant could never rebind it and their direct traffic went to the wrong machine for
  the whole session. Rotated on renumber; applying a changed token retires the stale binding.
- **The reachability gate demands a round trip.** Any inbound datagram used to mark an advertised
  endpoint alive, so a one-way path (joiner reaches host, host's replies lost — observed between
  two instances behind one hotspot NAT) passed the v0.28.2 gate and started a session whose relay
  ran over the broken leg. The gate now requires an acknowledged punch.
- **Jitter is kept as a per-edge pair.** The aggregate took max(median) and max(high) from
  different edges and subtracted them — systematically understating jitter, to zero in the worst
  case — and the relay fold summed medians with no hop jitter at all. Both now carry the pairing
  through, so a jittery third player is actually covered by the delay.
- **The runtime remembers the relay.** The relayed-route figure used to die in the lobby;
  WorstPingMs — feeding the rollback soft cap, the delay hint, and the mode-change floor — saw
  only direct paths, so every relayed session was advised to lower its delay below what its own
  route needed ("only needs delay 4" against a ~117ms relayed route, in every relayed session of
  the night that reported it). The relayed route now competes for worst at runtime, on host and
  joiner both.

### v0.28.2 — the lobby stops treating reachability as advice

**Wire-compatible with v0.28.0 and v0.28.1** (still protocol 17). Four fixes from an external
review of the previous release, all in how the lobby decides whether players can actually reach
each other.

**The mesh round was gated on a latency checkbox.** `MeasureLobbyMesh` is the only thing that
installs the mesh relay, and it ran only when "Auto from ping" was ticked. Turning that off — a
*delay* preference — silently skipped the punch burst, the edge measurement and the relay, so a
session with an unopenable joiner-to-joiner edge started with no viable route for it and nothing
said so. The round now always runs; only the delay arithmetic is still a preference.

**A player with no UDP path to the host was let into the session anyway.** The code detected it,
wrote "their input will not arrive at all" to the log, and then requested READY and sent GO —
starting a session it had just proved could not work. Relaying cannot rescue that case either,
because the relay runs over the very leg that is missing. It is now a casualty like any other: the
seat reopens and the lobby waits. The two-player case was the worst of it, since the old code
returned before the check even ran and told the player that "the host's own link is the only one
that matters" while that link was the dead one.

**The delay ignored the hop the relay adds.** The lobby measures direct edges, and an edge that
never opened — the reason the relay exists — contributed nothing to the worst-RTT figure. So the
delay was sized from the worst direct path while the relayed players were not using one. A relayed
route's equivalent round-trip is its two host legs added together, which on a 40ms/60ms pair is
100ms against a 60ms worst direct edge: a real frame of latency, paid as stalls by the seats
already having the worst time. The worst relayed route is now folded in before the delay is chosen.

**Live RTT ignored learned endpoints.** The send path ranks the learned endpoint — the address a
peer's packets actually arrive from — above every advertised candidate, and the lobby's per-edge
report includes it, but the live RTT aggregation enumerated advertised candidates alone. For a
symmetric-NAT peer that set is entirely dead, so the worst-RTT reading found no measurement and
discarded the whole figure, falling back to control-channel RTT: it measured TCP while input rode a
UDP path it refused to look at, sizing both the delay advice and the rollback soft cap off the
wrong number. There is now one definition of the candidate set, used by all three measurement
paths.

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
