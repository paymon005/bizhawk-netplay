# The capability probe

Rollback is only worth running if the core can afford to re-simulate frames inside a frame budget.
The **Capability Probe** button on the Diagnostics tab measures whether yours can, on the ROM you
have loaded, at the settings you are using.

It lives inside the netplay tool rather than as a separate tool because EmuHawk requires exactly one
external-tool entry point per DLL. It times save, load and frame-advance on the loaded core, prints a
per-core verdict, and saves and restores your position so it does not disturb play.

## Reading the verdict

The probe reports **two** frame costs, because the two things a frame is used for need not cost the
same:

- `frame=` — a frame advanced with rendering off. This is what a rollback repair re-simulates.
- `live=` — a frame with video rendered. This is what the player's own frame costs.

When `live=` alone eats the frame budget there is no rollback depth to have at any repair budget. On
Mupen64Plus/Rice the two match within noise, so the render flag saves nothing there — that is a fact
about the core, and printing both is what lets you see it rather than assume it.

Each probe line also carries the video settings it was measured under, so a run of them across
resolutions is comparable by reading rather than by remembering.

`MARGINAL` appears when the median frame cost qualifies for rollback and the slow end of the same run
does not. A heavy core's frame cost moves enough between runs to flip the verdict, and re-rolling the
probe until it says what you want is not a fix.

## The repair line

Everything above is a term timed on its own, and the depth verdict is those terms added up:
`load + depth × (frame + save)`. The second line checks that sum against the thing it claims to
describe, by timing a **whole repair** — a load, then N frames re-simulated from it — at two depths:

```
repair 1f=3.812ms 8f=20.640ms (+saves 67.910ms) -> per-frame 2.404ms +save 5.887ms, load 1.408ms | modelled 67.800ms (+0.2%)
```

Two depths give a line: its slope is what one more re-simulated frame really costs, and its intercept
is what the load really costs. Running the deep pass twice — once snapshotting every re-simulated
frame, once not — isolates the snapshot, because those two passes differ by nothing else.

That matters because none of the model's three assumptions is obvious on a recompiling core. A load
from further back can invalidate the code cache, and the frames right after one run on caches the
load has just cleared. Timing a load by itself would answer the narrower half of the question and
miss exactly the effect most likely to bite.

If `per-frame`, `+save` and `load` come back matching `frame=`, `save=` and `load=` from the first
line, the model describes the core. Where they diverge, the difference says which term is wrong.

`REPAIR OVERRUNS MODEL` appears when the measured repair costs more than 15% over the modelled one.
That is the direction that desyncs a session: the depth was solved from a sum a real repair cannot
meet, so every correction overruns its budget. Cheaper-than-modelled is reported too, as a negative
percentage, and is not an alarm.

### What it caught first

Itself. Saving does not advance the core, so the save pass was snapshotting memory nothing had
touched since the previous sample, and the load pass was restoring the state the core was already
standing on — 16.7 MiB written back over identical bytes.

Both are cheaper than the real operation. Across six N64 configurations `save=` wandered between 5.6
and 6.7 ms with no pattern, while the same snapshot timed inside a repair held steady at ~7.0 ms
(±5%); `load=` read ~1.4 ms against ~3.0. Understating both inflated the depth verdict by enough to
report 4 where the answer was 3.

Those passes now advance a frame between samples, and the repair line stands as the standing
cross-check.

Measured and reported, not spent: the depth is still solved from the isolated terms, which are now
timed against state that actually changes. It costs about 0.7 s of extra freeze on N64. The pass that
re-snapshots every frame runs only two frames deep — it is the dearest thing here, the probe sits on
the connect path where it lands as a hitch on joining, and the snapshot is a per-frame cost that
reads the same off any depth.

## First measurements (Genesis / GPGX, Contra Hard Corps)

The findings the probe was built to establish, kept because they are what the whole rollback design
rests on:

- **Rollback qualifies.** ~787 KiB state; save 0.41 ms, load 0.22 ms, frame 0.19 ms against a
  16.688 ms budget → maxDepth 54, clamped to the ring cap of 16. The probe reads the real console
  frame period rather than assuming 60.000 Hz.
  Note that the save costs about twice a frame, which is why skipping it on already-confirmed frames
  cuts rollback's steady cost from 0.60 ms to 0.19 ms.
- **Reentrant `FrameAdvance` from a frame callback works.** This was the deciding experiment: it makes
  **synchronous** rollback repair available, not just catch-up mode.
- **No `InvisibleEmulation` API**, so hiding a frame goes through `DispSpeedupFeatures` /
  `SoundThrottle` as designed. `SpeedMode` / `LimitFramerate` modulation confirmed.

## Sweeping the probe unattended

`tools/probe-sweep.ps1` drives the whole thing — patch `config.ini`, launch EmuHawk, load a savestate,
open the tool, click the probe, read the log, kill EmuHawk — once per configuration:

```powershell
.\tools\probe-sweep.ps1 -Config Rice:320x240,Rice:1280x960,GLideN64:320x240 -Runs 5
```

Each run is a **fresh EmuHawk**, so the core is always constructed with the plugin and resolution
already in place rather than having them changed underneath it.

The savestate slot follows the plugin (`-SlotByPlugin`), because the video plugin is a sync setting
and a state saved under Rice is not the one to load under GLideN64. `-StateSlot 0` probes at boot
instead, which is the only option for a game with no state.

Loading a state is about keeping the workload *still*, not about it being dearer. Eight runs each way
on Super Smash Bros. put the boot screen at 2.21 ms a frame against 2.32 ms in-game — the same,
inside the spread. But the probe's passes run over several seconds, and a booting game moves through
logos, an intro and an attract demo while they do. The repair decomposition assumes a stationary
cost, and on some boot runs it misreads badly enough to put the derived load at zero.
