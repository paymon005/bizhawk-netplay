# Roadmap

What is planned, what is deliberately not, and what is blocked on measurement rather than on code.

For issues in what already ships, see [KNOWN-ISSUES.md](KNOWN-ISSUES.md). For what each release
changed, see [CHANGELOG.md](CHANGELOG.md).

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

- **Live relay failover.** The host's relay for a failed joiner↔joiner leg is decided once, at session
  start. A leg that dies mid-game is not failed over to it. Start-of-session is deterministic, shows
  up in the log and cannot oscillate under packet loss; live failover is a harder problem and was
  deliberately left alone rather than half-done.

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
- **Where N64's frame cost comes from** is mostly render resolution: a controlled sweep puts it at
  2.4 ms at 320×240 and 7.7 ms at 2880×2160. Whether resolution accounts for *all* of the 1.6–12 ms
  swing seen across one evening's sessions is still open — the top of that range is above anything
  measured here — but it is no longer an unexplained term.
