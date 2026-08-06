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
- **KI-17 — disc half CLOSED in v0.34.0; ROM-hash strength still open.**
  **Discs (fixed):** every mounted disc is hashed individually and in order, using both of
  BizHawk's own hashers per disc — `Calculate_PSX_BizIDHash` for the TOC and first 26 sectors
  (which is what covers an audio-only disc with no data track) and `OldHash` for MD5 over up to 512
  sectors of the first data track, which is far more discriminating than the CRC32 the first ends
  in. The per-disc list travels, so a refusal names the disc rather than saying "your discs differ"
  and leaving two people to compare three files by hand. Order is part of the identity on purpose:
  it is the order the core was handed them, so it is what a disc-swap indexes into. `DiscIdentity`.
  **Firmware (fixed in v0.33.0):** Firmware is compared: `GameInfo.FirmwareHash` was there
  the whole time and the handshake never asked, so two players on different PSX or Saturn BIOS
  revisions ran different code before the game started and diverged for reasons nothing in the game
  explained.
  **Still open — cartridge ROM hash strength.** `RomHash` is `GameInfo.Hash`, which is whichever
  digest matched the gamedb: read against 2.11.1's `Database.GetGameInfo`, the lookup tries SHA-1,
  then MD5, then CRC32, and a miss falls back to SHA-1. So a ROM that hits the database can be
  identified by a 32-bit checksum. Two peers on the same file always agree, which is why this has
  never misfired, and an accidental collision between two real ROMs is not a practical concern; what
  it cannot do is resist a deliberately-crafted one. Closing it needs the ROM bytes, and nothing in
  the ApiHawk surface hands them over — `IGameInfo` carries the digest, not the file. That is the
  blocker, not the effort.
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
- **KI-20 — CLOSED in v0.35.0; ON by default since v0.36.0. It did NOT work in v0.34.0, where
  this entry first claimed it did.** An outvoted host can ask the majority for its state and adopt it, rather than
  overwriting three correct machines with its own. The donor is the lowest port in the largest
  group — a rule both ends compute, so the host and the donor never disagree about who was asked —
  and a tie is still not a majority.

  **What shipped in v0.34.0 could not send a state at all.** `StateOffer` was missing from the
  predicate that decides which control messages may carry a savestate, so the donor's reply was
  capped at the small-frame limit and refused *by the sender's own channel* before it left the
  machine. The host then waited out its donor timeout and recovered from its own state — the exact
  outcome the feature exists to prevent — and the refusal was counted against the donor, so a peer
  that had answered correctly was named in the log as the one that failed. Fixed in v0.35.0.
  The codec had round-trip tests for the message and the channel had tests of its own; nothing had
  ever sent one *through* the other, which is the seam and the lesson.

  **It was off by default until v0.36.0, and what flipped it was the weighing, not the code.**
  Correctness requires the wrong machine to adopt the right state, so if the host is wrong, the host
  imports — and a savestate is a trusted-input format all the way into the core (KI-13). Before this
  existed, no path let a host run a peer's bytes; every state-bearing handler was joiner-side, and
  that was a real property. Deferring gives it up. It shipped opt-in on the argument that a trade
  belongs to whoever is running the session.

  Compare what each side actually requires and that reads differently. The exposure needs a
  colluding **majority** — two of three players, or three of four, all reporting a matching false
  checksum. The failure it prevents needs **nobody**: a host with a Lua script running, or one that
  brushed a savestate hotkey, overwrites every player who was right, and that is an ordinary
  Tuesday. So it is on by default from v0.36.0, with the checkbox kept for a host playing with
  strangers. Unticked, the behaviour is the old one, and the log still names the case and the
  setting. `MajorityRecovery`, `DesyncPartition.ChooseDonor`.

  *Mixed-version caveat.* Because v0.34.0 cannot send a state, a v0.36.0 host that asks a v0.34.0
  donor waits out the donor timeout before falling back — up to ~90 seconds on N64, running
  diverged, where the old default resynced immediately. Sessions where everyone is on v0.35.0 or
  later are unaffected.
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

## 2026-08-05 non-advocate review, and the test work that followed

A read of every line with comments treated as claims to verify rather than as evidence. **Eleven
defects, all fixed in v0.35.0** — five of them in code added by the two releases immediately before
it, which is the number worth keeping rather than the list.

**They clustered at one seam.** Three were a pure function with round-trip tests whose *integration*
had never been driven once: the codec could encode a `StateOffer`, the channel could carry a large
frame, and nobody had ever sent one through the other (KI-20 above). Two more were comments
asserting arithmetic the code did not perform — the FIN linger's "a burst per RTO plus a few
re-drives" out of a budget that allowed one, and a donor timeout of fifteen seconds against a
transfer the same codebase budgets at ninety-two.

Structural response rather than eleven patches:

- `ControlMessageRouting` — one table every message type must be registered in, giving direction and
  size class. A new type added without deciding both now fails the suite instead of shipping.
- Decision-carrying state moved out of the tool form into Core where it can be driven: the majority
  ask, the apply barrier, the resync budget, the desync decision, the whole host rebuild sequence,
  the mesh's link quality and endpoint learning, and the lobby's mesh verdict.
- A harness that runs a host and up to eight joiners through a real desync recovery — the end-to-end
  test the previous plan wanted and could not have while the sequence lived in the form.
- Seeded fuzzing over every decoder, and contract tests holding timing constants against each other
  rather than against a comment.

**The verdict that was too strong, again.** The plan this review replaced closed with "no incorrect
assumption found anywhere". Two passes later that had produced eleven. Same lesson as the memory
domains above, and worth stating twice because it was learned twice: a confident review verdict is
not evidence.

**DONE in v0.38.0 (protocol 24) — the password KDF moved from SHA-1 to SHA-256.** Kept here rather
than deleted because the measurement is the reason and it should stay findable. Measured
2026-08-06, 100,000 iterations, eight cores: on .NET Framework 4.8 (what the tool ships on) SHA-1
costs 1092ms against SHA-256's 478ms, and on .NET 10 it is 105ms against 46ms. SHA-1 is 2.3x
*slower* despite doing less work per block, because .NET Framework takes a slow legacy path for it.
So the swap was faster on both targets and retired a legacy primitive, for nothing but a changed
derived key — which is a wire break, hence the wait. It shipped at v0.38.0 alongside the checksum's
lane change, since neither justified a forced update alone.

**What shipped is the hand-rolled loop at 518ms, not the platform's 478ms, and the reason is not
the one this note originally gave.** The overload of `Rfc2898DeriveBytes` that selects SHA-256
exists on .NET Framework 4.7.2+ and on .NET 10, but *not in the netstandard2.0 reference surface*
Core compiles against — so it is unreachable without reflection, which is a poor thing to put in an
authentication path for 40ms once per join. The loop is checked against the platform's own
implementation on both frameworks and against a published PBKDF2-HMAC-SHA256 vector.

Nothing below ~478ms is reachable without cutting iterations at a count already under the 600,000
current guidance suggests; the raw-HMAC floor is 512ms for 100,000 bare calls. The figures live
beside the constant in `SessionAuth`.

*A framework difference found while writing those tests, recorded because it will surprise someone
eventually:* .NET Framework's `Rfc2898DeriveBytes` rejects a salt shorter than eight bytes and .NET
10 accepts it. The session's salt is two 16-byte nonces, so it never binds here — but it means the
platform call and the loop that replaced it are not quite interchangeable at the edges.

**What the same measurement fixed without a wire change (v0.35.0+).** Verifying a joiner's proof
costs the host one derivation, and the accept loop is serial — so a stranger who cannot pass the
password could hold the lobby door shut against players who can. Attempts are metered per address
before the greet, where a refusal costs microseconds (`PasswordAttemptLimiter`). It bounds what an
unauthenticated party can make the host spend; it does not protect the password, which is what the
stretch is for, and a per-address limiter cannot help against a distributed flood.

## 2026-08-06: the measurement that decides rollback was itself pessimistic (v0.37.0)

Two review passes over this codebase have now recorded N64's rollback depth as the tight case, and
the To Do list was pessimistic about it "on the arithmetic that N64 sustains only ~3 frames of
depth" (KI-8 below). **That arithmetic came from a probe measuring the wrong operation.**

The timed savestate pass held every sample until the whole probe had finished, which left the
adapter's buffer pool empty — so each timed save allocated a fresh 16.7 MiB whole-core buffer off
the large object heap, while the session it models reuses one the rollback ring just released. The
probe was timing allocate-plus-save and charging it to a model that only ever pays save. It was
therefore most wrong exactly where the verdict is closest, and it under-reported the depth a heavy
core can afford. Fixed in v0.37.0.

**What this does not settle.** The direction of the error is certain and the mechanism is tested,
but the size of it is not measured here — no figure in this file should be adjusted on the strength
of the fix. KI-8's real-play record stands as written: at three players over the internet, Pokémon
Stadium on Rice ran rollback at delay ~5 and was reported as working well, with no telemetry. What
the fix means is that the *arithmetic* the pessimism rested on was measuring a cost the session does
not pay, so the next N64 probe on real hardware is worth reading fresh rather than against the old
number.

Two smaller things from the same pass. The probe held ~400 MiB of states live across three
measurement passes, felt hardest with several EmuHawk instances on one machine — the four-player
case this tool exists for. And it had no cleanup boundary, so a core that threw mid-measurement left
whole-core buffers checked out for the rest of the process while the tool's own save/restore made
the game look recovered.

Separately, a diagnostic that pointed at the wrong culprit: the frame loop reported cost-cap stalls
as time-sync yields, so a machine too slow to repair inside its budget presented as a clock-skew
problem — the opposite fix. And stalls waiting at the hard prediction cap, the most common kind on a
lossy link, had no counter at all. Both corrected in v0.37.0; the scheduling was already right and
did not change.

## 2026-08-06: the checksum's inner loop, and what a dependency chain costs (v0.37.0, v0.38.0)

The desync checksum walks the whole of main memory — 8 MiB on N64 — and the loop doing it turned
out to be leaving most of the machine idle, in two separate ways.

**The read (v0.37.0, no wire change).** `BitConverter.ToUInt64` in a loop is bounds-checked and
poorly inlined on .NET Framework specifically. Reading through a fixed pointer produces a
bit-for-bit identical hash and measured 1.5–2.0ms against 4.8–6.0ms. On .NET 10 the same change is
worth almost nothing. Same asymmetry as the PBKDF2 finding above: **the framework the tool actually
ships on is repeatedly the one with the slow path**, and a measurement taken only on the modern
runtime would have shown nothing worth doing in either case. Worth remembering before dismissing a
micro-optimization on net10 numbers.

**The dependency chain (v0.38.0, protocol 24).** FNV-1a is `h = (h ^ word) * prime` — each step
needs the previous `h`, so the loop ran at the latency of one 64-bit multiply per eight bytes no
matter what else the CPU could have been doing. Eight independent lanes: **0.62–0.65ms**, faster
than a `memcpy` of the same buffer, and stable across runs in a way the single-chain figures are
not — which is itself the evidence that the chain rather than memory bandwidth was the ceiling.
Four lanes measure the same as eight, so the last of it is not there to take.

Two things worth keeping from doing this:

- **The lane seeding was wrong the first time and a test caught it.** Seeding lanes as `h ^ k` looks
  fine until you notice the combine pairs them with XOR, and `(h^0) ^ (h^1)` cancels `h` completely
  — so an empty span returned the same constant whatever seed it was given. One FNV step per lane
  fixes it, because multiplication does not distribute over XOR. The test that found it was the
  small dull one about empty spans, not any of the interesting ones.
- **The hash arithmetic was in the untestable half.** It touches no BizHawk type; it was in the
  adapter only because that is where the bytes were fetched. That put the one function whose output
  crosses the wire and must be bit-identical on every peer where no test could reach it — and a
  change there is a silent permanent desync between versions that no protocol check catches, since
  the protocol number would not have moved on its own. It is in Core now with its values pinned.

## Open question — where the N64 savestate's 6.1ms actually goes (v0.38.1 measures it)

**The savestate is the binding constraint on a heavy core, and it is not obvious why.** With
confirmed saves elided and keyframes auto-solved, the model gives N64 a steady budget of 12.48ms
(16.639 minus 25% headroom) and a repair budget of two frame periods. At depth 2 with no keyframe
spacing, the repair sum is ~7.1ms of frames against ~12.2ms of saves: **the snapshot is roughly two
thirds of the cost and the emulation a third.** Every lever this tool owns — elision, keyframe
spacing, the cost cap — is already pulled.

~6.1ms for ~16.7MiB is about 2.7GB/s, well under what moving those bytes costs. Measured on this
machine, 16.7MiB through a `BinaryWriter` over a `MemoryStream`:

| core's write pattern | net48 | net10 |
|---|---|---|
| 4-byte fields | 64.3ms | 42.6ms |
| 64-byte blocks | 8.0ms | 6.8ms |
| 4KiB+ blocks | 2.4ms | 2.4ms |
| raw `BlockCopy` (floor) | 1.6ms | — |

6.1ms sits between the 4KiB-block figure and the 64-byte one, so the answer depends entirely on how
mupen64plus writes — which is not knowable from here. **The Diagnostics tab's "Savestate Cost"
button settles it**: it times a real save, records the write-size histogram, replays that shape with
no core involved, and reports the block-copy floor. If ≥90% of bytes arrive in writes ≥4KiB the
stream is already at the floor and this avenue is closed; otherwise the recoverable milliseconds are
stated directly.

**Related and more urgent: the depth verdict is inside the noise.** It flips between 3 and 2 at a
frame cost of ~3.44ms, and N64 at 1400x1050 measures 3.55ms — three consecutive probes of that one
configuration returned depth 2, 3 and 3. Since the qualifying threshold is 3, whether N64 runs
rollback at all is currently decided by a 3% margin. The v0.37.0 probe fix removed a 16.7MiB
allocation from the timed save path, which was both pessimistic and a source of variance, so the
spread should have tightened — unverified. Pressing the Capability Probe button several times in a
row is the test.

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
- A host is not exposed BY DEFAULT. Every state-bearing handler is gated on `!_isHost`, and three
  of the four `ImportState` call sites are joiner-side or restore our own pre-join state. The
  fourth arrived in v0.34.0 with majority-aware recovery and is off unless the host turns it on;
  see the note below the block.
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

*Re-verified 2026-08-04 against v0.34.0, and one thing HAS changed.* Three of the four `ImportState`
call sites are still the two joiner-side ones and the restore of this machine's own pre-join state.
The fourth is new: majority-aware recovery (KI-20) has an outvoted host adopt a peer's state, which
is the first path by which a host runs a peer's bytes.

*One correction, 2026-08-06, and it cuts in the reassuring direction.* That fourth path did not
actually function in v0.34.0 — the donor's state was refused by the channel before it was ever sent
(see KI-20). So a v0.34.0 host, opted in or not, never ran a peer's bytes: the exposure described
here begins with v0.35.0, where the path works. Recorded because "the feature was broken" and "the
host was not exposed" are the same fact here, and a reader auditing which versions can load a peer's
savestate should not have to derive that from a bug report.

*And a second change, v0.36.0, cutting the other way: deferring is now ON by default,* so a host
that never touches the Diagnostics tab does have this path. The reasoning is under KI-20 — briefly,
the exposure needs a colluding majority while the accident it prevents needs nobody — but the
version summary a reader wants is: **v0.34.0 and earlier, no host is exposed; v0.35.0, only a host
that ticked the box; v0.36.0 and later, any host that has not unticked it.** The exposure is
inherent to the fix rather than an oversight in it — correctness there requires the wrong machine to
adopt the right state. Abusing it needs a colluding majority, not a
single bad peer. What v0.34.0 adds is not a fix but an
end to the silence — a joiner says once, before the first host state loads, what a savestate can
set (memory, page permissions, the stack pointer of the emulated machine), and distinguishes the
two cases the MAC created: with a password only the host can reach that parser; without one the key
derives from public nonces, so integrity holds only against blind off-path injection. Deliberately
not a prompt. A joiner that declines the state cannot join, so a dialog would be a choice between
joining and not joining wearing a security control's clothes (`StateImportTrust`).

**Why this stays open, and what was rejected.** The obvious mitigation is to avoid the join-time
import: the host sends its state's digest, the joiner hashes its own, and the transfer is skipped
when they match — provably safe, since equal SHA-256 means identical bytes, and it would skip a
multi-megabyte transfer on a fresh start. It was rejected *as a security measure* because it does
not close the finding: a hostile host can report a bad checksum, force a resync, and the resync
path imports unconditionally because by then the states genuinely do differ. That makes it a
performance feature wearing a security fix's clothes, bought on the most delicate path in the
codebase. Worth revisiting on its own merits as an optimisation; not worth claiming as this.

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
