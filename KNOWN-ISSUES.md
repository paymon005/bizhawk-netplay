# Known issues

The living issue list for work not tracked anywhere else. Every finding from the 2026-07-26
adversarial review of the v0.10.0 rewrite (F1–F9, P1–P2, KI-1–KI-7) has been fixed — the details
live in the git history (`git log --grep KI-`, `--grep "review finding"`) and in the v0.10.0 /
v0.10.1 release notes.

## 2026-08-04 review: every BizHawk-facing assumption verified against source

A full line-by-line review of the codebase, with every claim about BizHawk checked against the
exact installed revision (2.11.1, commit `bdddf4a` — the local sparse clone at
`..\bizhawk-src`, reflection over the installed DLLs, and GitHub raw at the pinned commit). All
confirmed word-for-word, so nobody has to re-verify these:

- **AllowInput**: `FormBase { BlocksInputWhenFocused: false }` matches BEFORE
  `IExternalToolForm => AllowInput.None` — the focus-override mechanism is sound as documented.
- **Run loop**: `Tools.GeneralUpdateActiveExtTools(); StepRunLoop_Core(); Render();` are
  consecutive — the fine clock's placement claim is exact.
- **Throttle.Step**: the paused branch is an unconditional `Thread.Sleep(15)` — the ~66Hz tick
  ceiling is real and unreachable from this side of the seam.
- **`Joypad.Get`** is exactly `_movieSession.MovieIn.ToDictionary(n)` — capture reads what local
  play feeds the core.
- **`IEmulator.FrameAdvance`** docs require `GetSamples()` after every advance "even when
  renderSound = false" — the blip_buf discipline in `RunFramesInvisible` is required, not caution.
- **Memory domains** (all 19 subclasses in the installed DLLs audited): the checksum's five paths
  classify every one correctly. The only types that land on the slow sampled word path are
  `MemoryDomainUshortArray` and `MAMEMemoryDomain` — neither is MainMemory on the consoles this
  tool targets (MAME/Arcade is, and stays sampled; divergence learning degrades to absent there,
  by design).

**That verdict was too strong, and an external review found the counterexample.** Verifying that
each domain TYPE lands on the right hash path is not the same question as whether `MainMemory` is
the whole machine — and on the link cores it is not. `MemoryDomainList.MainMemory` falls back to
the first registered domain, and GBHawkLink/3x/4x/GGHawkLink register `Main RAM A/B/C/D` (or
`L`/`R`) nominating none, so the checksum read one Game Boy of up to four and divergence on any
other machine was invisible. Those cores were refused in v0.32.1 and are played again in v0.33.0,
which folds every emulated machine's main memory into the checksum (`MainMemoryCoverage`,
`SiblingMachineDomains`). The lesson worth keeping: an audit answers only the question it asked,
and "the paths are right" was never the same claim as "the coverage is right".

## Findings from the 2026-08-04 external review

Most of these are closed in v0.33.0, which is the release they were the substance of. What remains
open is recorded with what it would actually take.

- **KI-16 — CLOSED in v0.33.0.** "Same BizHawk build" is verified now. `VersionInfo` gives the
  release, the git branch and commit, the developer-build flag and any `dll/custombuild.txt`
  string; the process architecture is added because an x86 and an x64 build of one commit are
  different programs. `BuildIdentity` assembles and compares them, and names which of the three
  kinds of mismatch it found. `CoreVersion` is still compared, and still could not have done this:
  it is `Assembly.GetName().Version`, which is one string for every build of a release.
- **KI-17 — PARTLY CLOSED in v0.33.0.** Firmware is compared: `GameInfo.FirmwareHash` was there
  the whole time and the handshake never asked, so two players on different PSX or Saturn BIOS
  revisions ran different code before the game started and diverged for reasons nothing in the game
  explained.
  **Still open:** `RomHash` is `GameInfo.Hash`, which is whichever digest matched the gamedb —
  read against 2.11.1's `Database.GetGameInfo`, the lookup tries SHA-1, then MD5, then CRC32, and a
  miss falls back to SHA-1. So a DB-hit ROM can be identified by a 32-bit checksum. Both peers on
  the same file still agree, which is why this has never misfired; what it cannot do is resist a
  deliberately-crafted collision, and it is a short answer where a long one was available. The
  disc half is untouched: the PSX quick identifier hashes the TOC and the first 26 sectors, and a
  multi-disc set identifies from disc one, so two players on different disc 2s pass. Treat PSX
  multi-disc as unverified.
- **KI-18 — CLOSED in v0.33.0.** Determinism is read from the core instead of hardcoded `true`,
  with Mupen64Plus the one named exception. The exception is by name rather than by tolerance for
  a false flag, and the reason is in `DeterminismPolicy`: reading the 2.11.1 cores shows nearly
  every one that computes the flag seeds its clock from `DateTime.Now` when it is false, while
  Mupen declares it a constant and reads it back nowhere in the whole N64 tree. MAME goes further
  and registers a different set of memory domains when false.
- **KI-19 — CLOSED in v0.33.0.** "No sync settings" and "could not read them" are different
  answers on the wire now, and the second refuses. The old behaviour inverted the check exactly
  when it mattered: both peers failing produced the same empty blob, the same digest, and a pass.
  N64's `VideoSizeX/Y` are carried too — as a named warning rather than a refusal, since whether a
  resolution difference matters depends on whether the game reads its own framebuffer.
- **KI-20 (open) — recovery always assumes the host is correct.** The host's state is distributed
  on every resync, so a lone-diverged host can overwrite three agreeing joiners. The partition is
  recorded and the case is named in the log (`DesyncPartition`), but the policy is unchanged;
  choosing a different authority needs majority reconstruction and a wire change. Deliberately
  waiting on real logs — deciding the authority policy from a session that actually hit it beats
  deciding it from reasoning.
- **KI-21 — CLOSED in v0.33.0.** Input datagrams carry their author and a per-pair HMAC tag. The
  host mints one key per unordered pair of seats and hands each peer only the pairs it belongs to,
  so a peer holds nothing it could sign as another seat with; the payload's own port byte is
  checked against the proven author. Membership tokens could not have done this — every peer holds
  every seat's token, which is what makes a rejoin recognisable and what makes a token useless for
  proving authorship. See `MeshPairKeyring`, including what it deliberately does not do (replay).
- **KI-22 — CLOSED in v0.34.0.** The product said 2-4 players and the runtime permitted 8, and a
  host crossing between the two was told nothing. Capping was the wrong fix: every array, mesh
  route, pair key and partition description in the code is written for N and holds at 8, so a cap
  would have removed a capability on suspicion. Instead the documented range is what has been run,
  the permitted range is what the code supports, and crossing between them says so once — naming
  the mesh edge count, the per-frame send count and the host relay load rather than waving at
  "untested". `PlayerCountPolicy`.

The same review produced the v0.31.0/v0.32.0 work and the v0.32.1 hotfixes: vacated seats, live
relay failover, the control-frame MAC, divergence learning, the measured checksum cadence, and then
the corrections above.

## Status

Entries below are a mix: some are open work (KI-11), some are validation records kept because the
evidence is worth having. Each says which it is in its own opening line — this heading used to say
"Open", directly above an entry whose first word was CLOSED.

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
joiner mid-session — an ungraceful break, which at the time was the only thing that reached this
path: a *graceful* leave used to end the session outright. (Since v0.31.0 a graceful leave at 3+
players vacates the seat and the survivors play on; the held-seat flow below remains the ungraceful
path, and its timeout now also vacates instead of ending.) What the host logged, in order:

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

**Host-command ownership is verified by hand (2026-07-30, GPGX/Genesis, 2 players).** The frontend
commands that could move the timeline out from under a session were each tried, in the lobby and in
play:

| command | key | result |
|---|---|---|
| Frame Advance, while the lobby waited for a joiner | `F` | nothing happened |
| Frame Advance, mid-session | `F` | nothing happened |
| Rewind, mid-session | `Shift+R` | nothing happened |
| Reboot Core | menu | refused, message shown, session continued |
| Save state | — | **works** — deliberately not blocked |
| Quick Load | `P` | session ends: `the core's frame count jumped back 15522 — a rewind/load-state hotkey fired?` |

Both restore paths were checked too, which is where this had already broken twice: the emulator was
**paused before hosting and still paused after disconnecting**; rewind **enabled** worked again after
disconnecting; rewind **disabled** stayed disabled across a whole session. Frame Advance is the
self-reporting one — had it not been blocked, the drift check would have ended the session with
`EmuHawk advanced N extra frame(s)` rather than doing nothing.

Quick Load ending the session is the designed trade, not a defect: savestates are deliberately not
claimed so that *saving* works, which leaves loading detected rather than prevented. See the To Do
note about making a host-side load resync everyone instead.

**The N64 "analog stick" problem was never netplay's — CLOSED 2026-07-30.** A game whose throw
distance is chosen by how far the stick is flicked would give light throws and far throws but never
a medium one. It was investigated as a netplay fault across several sessions. It is not one: it
reproduces identically **with the tool not loaded at all**.

What the investigation did establish, and what is worth keeping:

- **Capture is clean.** `Diagnostics > Watch Analog` measured 66 distinct values spanning the full
  −127..127 per axis, raw host ±10000, linear, no plateau. The stick reaches the core intact.
- **Not the analog math.** That was a hand-copy of BizHawk's bind arithmetic and had genuinely
  drifted, but the dumps show it agreeing with BizHawk on every reading. That copy is now deleted.
  Capture reads the end of EmuHawk's own controller chain — the controller behind `Joypad.Get`,
  asked directly rather than via the dictionary it builds — so there is nothing left to drift.
- **Not the N64 digital-direction override.** `N64Input.GetStickValues` really does let a pressed
  `A Left`/`A Right` win over the axis and force full deflection, and BizHawk's default XInput
  layout really does bind those to the stick. It was diagnosed confidently as the cause. It is not:
  those binds do not fire here, and the "gap" cited as evidence was sampling noise from a detector
  whose threshold was far too low.
- **Not the circular constraint**, which BizHawk applies upstream of anything we read.

The one thing netplay *was* doing wrong to input is fixed and unrelated: this window had focus,
so BizHawk swallowed all pad input (`IExternalToolForm => AllowInput.None`). See below.

**Focusing the netplay window used to stop your controller — fixed 2026-07-30.** BizHawk decides
whether to accept host input from the active form's type, and the rule for external tools was
unconditional. With this window focused, pad axis values froze at whatever they last held. Fixed by
overriding `BlocksInputWhenFocused`, the same escape TAStudio uses, conditionally so that typing in
an editable field still goes only to that field.

**The Rice plugin renders some games incorrectly** (visible on the N64 titles above). That is a
BizHawk video-plugin issue, not a netplay one — but it is the first real entry in the N64
settings profile 1.0 needs, and worth knowing before blaming the netcode for something on screen.
Peers must run the same plugin regardless: the handshake compares the core's sync-settings blob and
refuses a mismatch up front.

**KI-11 (validation) — what's left is four SEPARATE MACHINES, and a heavy core at four players.**
Four players over a real internet path is **done** — see KI-8, which has the logs: all 12 mesh paths
answered, six desync recoveries, a drop and rejoin. What that session did *not* cover is four
distinct machines: the three joiners ran on one laptop, so the joiner-to-joiner mesh legs were
loopback and only the host's legs met the network. The single-machine caveat below is about the
**latency measurements**, which are a separate exercise again.
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
loopback mesh with no NAT), or a **heavy core** *at four players*, where a repair costs 6-9ms per
frame instead of 0.6 and the same depths would be an order of magnitude dearer — though N64 rollback
has since been played at three players over the internet at delay 5 without complaint (KI-8), which
is the first evidence against the pessimistic reading.

Four players over a real internet path is no longer on this list; KI-8 settled it. The
joiner-to-joiner edges, however, are still unmeasured on a real network, because that session's
joiners shared a machine — which is exactly what four separate machines would finally exercise.

*What to read off a four-machine session when one happens:*
- **`mesh measured: X of Y direct path(s) answered`** at start. Anything short of X = Y means the
  delay below it covers only part of the mesh — on a real path that is the joiner-to-joiner edges.
- **The `Auto delay` line** — whether it picked above what the host's own links alone would suggest.
  If not, the mesh round found nothing and the feature is a no-op on that network.
- **`stall N%`** per peer. High on one peer means that peer's worst edge; high on everyone means the
  delay is under-covering the link.
- **Rollback depth and gate cost** on the two heaviest machines. This is where a light core and a
  heavy one diverge most.
- **The named-edge lines** (v0.24.0). Each joiner now logs `no direct path answered to: …` and
  `reached at a learned address: …` by player. The first names who to look at; the second says a
  symmetric NAT was worked around rather than merely survived.

**KI-12 (validation) — the symmetric-NAT path has never met a symmetric NAT.**
v0.24.0 (protocol 14) gives every seat a token so a peer can be recognised at the address it really
arrives from, which is the only way such a peer's packets can be placed. It is unit-tested against a
transport deliberately pointed at the wrong port — the honest simulation of "advertised one address,
arrives from another" — and the learned address is probed, kept warm, and survives route refreshes.
None of that is a real router. Until someone behind a symmetric NAT joins a session, treat it as
built-and-reasoned rather than working.

*What to read off such a session:*
- **`reached at a learned address: P<n>`** on any peer's log — that is the mechanism firing. Its
  absence when a STUN symmetric verdict was logged is the interesting failure.
- **Whether the host's relay warning fires anyway.** It now says a symmetric NAT alone should no
  longer cause it; if it does fire, the token never got through and that assumption is wrong.

**KI-13 (open, upstream) — a joiner imports a savestate, and BizHawk's savestate reader trusts it.**
This is the one finding from the 2026-07-31 security review that is not fixed here, because it is
not ours to fix. A joiner accepts a whole-core savestate from its host, twice: once at the start,
and again on every resync. That state goes to `IStatable.LoadStateBinary`, and on a waterbox core —
Snes9x, bsnes, melonDS, Ares64, the Nyma cores, most of the modern set — that reaches
`wbx_load_state`, which restores guest memory contents, guest page protection bits and the guest
stack pointer from the stream. Those are the inputs to native execution, and the sender chooses all
of them. It is not a memory-safety accident to be patched; it is what loading a state means. The
Rust side even notes a hash mismatch and carries on.

Upstream is not wrong to work that way — a savestate is a trusted-input format, and every decision
in that code is correct for a file off your own disk. What this tool does is turn it into a *remote*
input. Worth reporting upstream; nothing here can make it safe.

**What follows from it:**
- A host is not exposed. A host never imports a peer's state — every state-bearing handler is gated
  on `!_isHost`, and all three `ImportState` call sites are joiner-side or restore our own
  pre-join state. No path was found by which a host runs a peer's bytes.
- A joiner is exposed to its host, and to anyone who can inject into the control stream. There is
  no encryption and no per-frame integrity after the handshake (the PBKDF2 output is compared and
  discarded rather than kept as a key), so on a hostile network — public wifi, a compromised router
  — someone on the path can send a `Resync` and reach the same parser without knowing the password.
  Joining people you know is the mitigation; a MAC over control frames is the fix, and it is not
  written.

*Re-verified 2026-08-04 against v0.29.0, and one thing is worth correcting: the fix is cheaper than
the sentence above implies.* All three `ImportState` call sites are still what the finding says —
`Recovery.cs` is gated on `!_isHost`, `Session.cs` is the join path, `Reconnect.cs` restores this
machine's own pre-join state — so the host is still not exposed. But `SessionAuth.ProofPair`
**already derives a 32-byte key** from the password and both nonces, uses it for the proofs and
drops it. A MAC over control frames therefore needs no new exchange, no second KDF pass and no
extra handshake round trip: it needs the key kept and the frames framed. The cost is a protocol
bump, not a design.

**The network half is FIXED in v0.31.0 (protocol 20).** The key is kept, and every control frame
after AUTH carries a truncated HMAC-SHA256 bound to its direction and stream position — injection,
tampering, replay, reordering and reflection each fail loudly into the ordinary link-loss path. So
"someone on the path can send a `Resync`" is no longer true of any session with a password; with
an EMPTY password the key derives from the public nonces, so integrity holds only against off-path
(blind) injection, and joining strangers without a password remains exactly as trusting as it
sounds. What remains open is the upstream half, which no wire change here can touch: a joiner
still imports its host's savestate, and a savestate is a trusted-input format all the way down
into the cores. Join people you know, or set a password.

**KI-14 (validation) — divergence learning replaced the VI-register guess; two machines above
native is what remains.** The v0.30 exclusion read `VI_ORIGIN` and skipped the buffer being
scanned out — structurally insufficient, since the plugin writes back to the buffer it just
*rendered* (the other one, in any double-buffered game), and the render target's address lives
inside the plugin where no register exposes it. v0.32.0 measures instead of guessing: right after
every rebuild the peers are byte-identical, so buckets of memory that disagree over the next three
checksum boundaries can only be machine-produced, and the host publishes their union as the mask
(see `DivergenceLearner`; the resync loop is broken by treating learn-window mismatches as the
measurement). The VI span survives only as the pre-learn default.

None of that is yet a two-machine N64 session at 800×600. *What to read off the first one:*
- **The learn round's verdict line** — `measured which memory is machine-produced: N% …` is the
  mechanism firing; `all 256 buckets agreed` above native would mean the write-back never happened
  (plugin setting); `refused a mask: N%` means something far beyond a framebuffer diverged.
- **Whether checksums agree after the mask's switch-over frame.** They never did before, at any
  point, above native.
- **The checksum line's `-maskNr/NKiB` tag**, which names how much is being skipped — a framebuffer
  should read as a few ranges totalling ~2-15% of RDRAM.

**KI-15 — CLOSED in v0.32.0.** The bucketed divergence map shipped exactly as designed here:
256-bucket vectors exchanged over the first three boundaries of every generation, the learned mask
capped at 25% of RAM (a real desync spreads through memory, blows the cap, and is refused rather
than masked), self-disabling at native resolution, and re-learned from a fresh identical baseline
on every rebuild — which also means every desync report now names the disagreeing address ranges,
since every resync starts a learn round. See `DivergenceLearner` and its tests; validation on real
hardware is KI-14 above.

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
