# N64 and other heavy cores

N64 works: it connects, plays, stays in sync, and analog moves. Getting it to run *well* is mostly
about core and video settings, and one of those settings also decides whether the session stays in
sync at all.

BizHawk's N64 core is **interpreter-only** (no dynamic recompiler), which is often taken to mean it
is too slow for this. Measure before believing it: on a modern machine the core runs a frame in
~2–4 ms against a 16.7 ms budget. What is expensive is its **savestate** (~6 ms for a 16 MiB state),
which is a rollback cost rather than an emulation one. Two EmuHawk instances sharing one machine
remains the worst case.

## Settings

| Setting | Use | Why |
|---|---|---|
| **Core** | Mupen64Plus | Ares64 is more accurate but slower. |
| **Video plugin** | Rice, or Glide64mk2 | Worth 5–23% of the frame against the default GLideN64. Never Angrylion — it is a software renderer. |
| **Resolution** | **Native (1x)**, enhancements off | Decides both frame cost and whether you desync. See below. |
| **RSP** | HLE (the default) | |

Every player must use **identical** N64 settings. They are sync settings, so a mismatch is refused at
the handshake — and since v0.24.0 the refusal names which setting differs and both sides' values.

N64 reports itself as non-deterministic. That is **not** treated as a refusal: it usually means
determinism was not requested rather than that the core will diverge. You will see a warning in the
log, the session runs, and the periodic checksum is what actually guards you. In practice it stays in
sync.

## Resolution and desyncs

Historically, above native resolution **N64 desynced at every checksum**.

Measured over a long two-machine session: at 800×600 every single checksum disagreed, in lockstep
*and* in rollback; at native resolution the same pair ran 15,000+ frames with every checksum agreeing.

The cause was never the netcode. Rice and GLideN64 resolve their framebuffer back into RDRAM, and
above native those bytes are produced by your GPU rather than by the emulated core — so they differ
between machines and land inside the region the desync checksum reads. Resyncing cannot fix it,
because the next frame reproduces it.

**The divergent region is measured, not guessed.** Right after every session start or resync all
machines hold byte-identical memory, so over the first three checksum boundaries the peers compare
per-block hashes of RDRAM; blocks that disagree on identical states can only be machine-produced.
The log names them as addresses, which is worth having whatever you do next.

**Excluding them is opt-in, and it is a trade rather than a fix.** Rice copies rendered data back
into RDRAM when the emulated CPU reads framebuffer memory — so on a game that reads its own
framebuffer, those bytes are real game-visible state and masking them means a genuine divergence
there goes undetected. Most games never read it; some do, and nothing here can tell which yours is.
So the mask only applies when **both**: you tick *Allow above-native N64 (experimental)* on the
Diagnostics tab, and the session is actually above native. At native nothing is ever excluded. Both
players must match the setting — a peer that declines keeps hashing everything and will read as a
desync against one that does not, with a log line saying so.

**Native resolution is still the setting that needs no trade**, and the one with fifteen thousand
frames of evidence behind it. Above native with the opt-in has not yet been confirmed on two
machines at all; KI-14 in [KNOWN-ISSUES.md](../KNOWN-ISSUES.md) lists what to read off the first
session that tries it.

Note also what none of this buys: a higher resolution still costs frame time (see the sweep below),
and on a heavy core that is the budget rollback depth comes out of. The mask removes a correctness
barrier, not a performance one.

## What resolution costs

Forty probes — five at each of four resolutions on each of two plugins, same machine and same save.
Median frame cost, and the rollback verdict across those five runs:

| Resolution | Rice | GLideN64 |
|---|---|---|
| 320×240 | 2.42 ms — rollback, depth 3 | 2.62 ms — rollback, depth 3 |
| 800×600 | 2.64 ms — rollback, depth 3 | 3.26 ms — rollback, depth 3 |
| 1400×1050 | 3.52 ms — depth 3, `MARGINAL` | 4.33 ms — depth 2, **lockstep only** |
| 2880×2160 | 8.14 ms — depth 1, lockstep only | 8.57 ms — depth 1, lockstep only |

Resolution is worth ~3.4× across that range; the plugin is worth 5–23% at a fixed resolution. Both
matter, but not equally — and the plugin gap is what costs GLideN64 rollback at 1400×1050, one step
earlier than Rice.

Savestate cost stays flat at ~5.9 ms throughout, as it should: state size does not depend on how you
render. An earlier independent sweep of the Rice column landed within 0.05 ms at three of the four
points, so this is a replication rather than a single run.

The desync boundary is native unless you take the experimental opt-in above, and the performance
boundary is somewhere past 800×600 — so native remains the setting that satisfies both without a
trade.

## `render: false` saves nothing here

The capability probe times a frame both ways. Across all twelve runs the rendered and unrendered
figures agree within ~5%, with the rendered one sometimes *cheaper* — the difference is noise.
Mupen64Plus/Rice does its video work regardless of the flag, so skipping the render on a discarded
catch-up frame buys no time on this core. It may still on others.

## Rollback on N64

Rollback is **available** on N64 and no longer overridden to lockstep. The capability probe decides,
using the model the session actually runs: a snapshot is skipped on any frame whose inputs are
already confirmed — most of them on a healthy link — so rollback's steady cost is the frame itself
rather than a whole-core savestate every frame.

The catch is depth. N64 measures a usable prediction horizon of about **3 frames**, so it hides
roughly 3 frames of one-way latency and no more: worth it against someone nearby, not against someone
far away. Lockstep remains one click away on the Netcode dropdown. When a misprediction lands you pay
a brief hitch where lockstep would have paid a stall. The session log reports the depth it measured.

### Sparse keyframes

Frames that *are* predicted get a snapshot every other one rather than every one, because the
snapshot is where a repair's budget goes: 6.82 ms of the 9.24 ms a repaired frame costs, against
2.41 ms for the frame itself. A correction restarts from the nearest keyframe at or before its target
and replays at most one extra frame to reach it — cheap, at that ratio.

It buys a frame of depth, and brings a worst-case repair from ~31.5 ms down to ~27 ms — the first
time one fits inside the frame tick's own ~28.4 ms budget instead of overrunning it as a hitch. The
status line shows the price when it is paid: `last d3+1wb` is a depth-3 correction that walked back
one frame.

Going sparser is not better: past every third frame the walk-back costs more than the snapshots it
saves.

## Diagnosing a bad session

Watch the fps readout while you tune. At ~100% you are fine; well under means CPU-bound, which is
fixed by faster settings or a better machine, not by netcode.

Check `stall%` before blaming the core. On a heavy console a network stall and a slow core look
identical from the chair, and only one of them is fixed by video-plugin settings.

Turn on **Verbose log** and read the per-second `pacing:` line to tell the two apart:

- `core mean` at or above the frame period — the core is genuinely missing budget.
- `rebases` above zero — the schedule discarded frames it could have run.
