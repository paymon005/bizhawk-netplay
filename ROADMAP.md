# Roadmap

What is planned, what is deliberately not, and what is blocked on measurement rather than on code.

For issues in what already ships, see [KNOWN-ISSUES.md](KNOWN-ISSUES.md). For what each release
changed, see [CHANGELOG.md](CHANGELOG.md).

## Identity and verification — shipped in v0.33.0 and v0.34.0

An external review (2026-08-04) reframed the priorities, and the reframing was right: the netcode
was strong, and what was weak was knowing that two peers are running the same thing and that the
checksum sees the whole machine. v0.33.0 did four of its five items; v0.34.0 did the fifth and the
per-disc identity below.

- **Build identity.** BizHawk's release, git commit, branch, dev flag, custom-build string and the
  process architecture, compared exactly. Firmware compared too.
- **Determinism qualified**, Mupen a named exception, sync-settings reads that fail closed.
- **Every emulated machine hashed**, which is what let the link cores be played again.
- **Author-bound UDP input**, per-pair keys, tags on every datagram.
- **Per-disc identity** (v0.34.0). A multi-disc set used to identify from disc one, so the same
  disc 1 with different disc 2s passed every check and diverged on the swap. What is still missing
  is cartridge ROM-hash strength: `GameInfo.Hash` can be a CRC32, and closing it needs the ROM
  bytes, which nothing in the ApiHawk surface hands over (KI-17).
- **Majority-aware recovery** (v0.34.0; *actually working* in v0.35.0; **on by default from
  v0.36.0** — see KI-20 for why the version distinctions matter). This was the item held back for
  real logs, on the argument that deciding an authority policy from a session that hit the case
  beats deciding it from reasoning. What settled it instead was the review that found the host had
  no way to be wrong *visibly*: the partition naming shipped first, and the policy followed once the
  case was legible. The default flipped once the two sides were weighed by what each requires — a
  colluding majority to abuse, nobody at all to trigger the accident it prevents.

Faster checksums and deeper rollback come after. A faster checksum over the wrong bytes is worse
than a slow one, because it is confidently green.

## Owed at the next protocol bump

Nothing here is worth a wire break on its own; they are queued so the next break carries them.

- **The password KDF moves from SHA-1 to SHA-256.** Faster on both targets *and* retires a legacy
  primitive: on .NET Framework 4.8, 100k iterations cost 1092ms with SHA-1 against 478ms with
  SHA-256, because the framework takes a slow legacy path for SHA-1. The measurements and what they
  rule out (hand-rolling, and cutting iterations) are in `SessionAuth` beside the constant, and in
  KNOWN-ISSUES.md. Held only because it changes the derived key.

## Needs people, not code

These are the gaps that cannot be closed from one desk. They are listed first because they are the
difference between "works for us" and "works".

- **Four separate machines.** Four players over the internet is done and logged, but those three
  joiners shared one laptop, so the joiner↔joiner mesh legs were loopback and only the host's legs
  met the network. Four distinct machines is what would finally exercise them.
- **A real symmetric NAT.** The token-learning path added in v0.24.0 is built, unit-tested against a
  transport deliberately pointed at the wrong port, and reasoned through — and has never met an
  actual router. `KNOWN-ISSUES.md` KI-12 records what to read off the first session that does.
- **A long soak.** Nothing here has been run for hours at a stretch.
- **A heavy core at four players.** Rollback at four players was measured on a light core. On a heavy
  one a repair costs 6–9 ms per frame instead of 0.6, and the same depths would be an order of
  magnitude dearer.

## Planned

- *(Live relay failover shipped in v0.31.0: a joiner starving on a dead leg reports it at 3s and
  the host carries the pair, installed once and never flapped — the 8s watchdog stays as the
  backstop for the host's own legs, which no relay can cover. The same release made sessions
  outlive their players: a graceful leave or an expired rejoin wait vacates the seat instead of
  ending the session.)*

## Considered and deferred

- **A lobby browser / rendezvous server.** Would let players find each other with a short code instead
  of exchanging IPs. Needs a server someone runs and pays for, which is a different kind of project
  from a DLL you drop in a folder.
- **A TURN-style relay.** The one case the host-as-relay cannot cover is a symmetric-NAT peer
  *hosting* without a forwarded port: there is no reachable rendezvous then. An external relay is the
  answer, and again needs infrastructure.
- **Refusing to start unless every joiner has a verified host UDP leg.** Rejected: a leg can open late
  — the punch keeps knocking for 300 s while the lobby's mesh sample is 1.5 s — so this would produce
  confident false rejections. A loud warning was shipped instead.

## Not planned

- **Moving emulation off the UI thread.** Cores are thread-affine (Waterbox, GL), so this is not
  available. The remaining levers on a heavy core are core/plugin settings and a capable CPU.
- **Compressing the rollback ring.** Whole-state transfers over the *network* are deflated, and that
  is where multi-second freezes on heavy cores came from. The rollback ring still saves and loads raw,
  deliberately: those are on the frame path, where the memcpy *is* the budget and compressing would
  cost more than it saves.

## Open questions

- **Rollback depth on heavy cores is arithmetic, not tuning.** N64 runs ~3 frames deep at native.
  Sparse keyframes bought one of those. Beyond it the wall is that the savestate is **74%** of what a
  repaired frame costs, and a 16.7 MiB state moves at memory bandwidth — 2.9 GB/s written, measured
  identically across ten games, both plugins and every resolution. A smaller or incremental state
  would be the real win and needs core support BizHawk does not expose.

  *Read against the 2.11.1 source (2026-08-04), part of that figure is a copy we could skip.*
  `N64.SaveStateBinary` is `api.SaveState(SaveStatePrivateBuff)` followed by
  `writer.Write(buff, 0, used)` — so every snapshot moves the state **twice**: native code fills the
  core's own 16 MiB buffer, then that buffer is copied into ours. The second copy is roughly a
  millisecond of the six. Removing it means calling `mupen64plusApi.SaveState` by reflection and
  hand-rolling the saveram/lag/frame tail that `SaveStateBinary` appends — i.e. trading the proven
  `IStatable` round-trip, which is also the format the resync path ships over the wire, for about
  20% of the dominant cost. Written down rather than done; the ratio is not obviously worth the
  seam.

- **The desync checksum is no longer the heavy-core hitch it was.** Protocol 19 reaches the raw
  block behind a delegate-wrapped domain, so N64 hashes all 8 MiB by memcpy (~2 ms) where it used to
  sample a quarter of it one word at a time (~7 ms). That is a hitch removed every five seconds and
  four times the coverage. What remains unmeasured is the figure itself on real hardware — the
  numbers above are the model, and the session log's `checksum:` line reports the truth.
- **Where N64's frame cost comes from** is mostly render resolution: a controlled sweep puts it at
  2.4 ms at 320×240 and 7.7 ms at 2880×2160. Whether resolution accounts for *all* of the 1.6–12 ms
  swing seen across one evening's sessions is still open — the top of that range is above anything
  measured here — but it is no longer an unexplained term.
