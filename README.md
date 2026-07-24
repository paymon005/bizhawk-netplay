# BizHawk Netplay

A C# external tool (ApiHawk) that adds online multiplayer to any local-multiplayer game
BizHawk can run, using **delayed lockstep** as the universal baseline and **GGPO-style
rollback** as a probe-gated drop-in for cores that can afford it.

Design principle: lockstep and rollback are the *same system* with one swappable component
(`ISyncStrategy`). Everything else — transport, session, input serialization, determinism
enforcement, desync detection — is shared and built once. See
[bizhawk-netplay-architecture.md](bizhawk-netplay-architecture.md) for the full design.

## Status

Targets **BizHawk 2.11.x** (.NET Framework 4.8 build). Current progress:

| Milestone | State |
|---|---|
| **M0 — Probe harness** | ✅ Done. Runs the §5 probe + three API experiments. Validated on Genesis/GPGX (see below) |
| Core sync logic | Input serialization, layout negotiation, input pipeline / confirmed-frontier, **lockstep + rollback** strategies — unit-tested |
| **M1 — 2-player lockstep** | ✅ Verified on hardware (two EmuHawk instances, Genesis/GPGX): real-time pacing, working audio, desync detection (host saves quick-slot 10 on mismatch), configurable delay + packet redundancy |
| **M2 — hardening** | ✅ Live ping/RTT + delay hints, **desync auto-recovery** (mismatch → resync from an authoritative state instead of ending), alt-tab audio resilience |
| **3–4 players** | ✅ Code-complete — host-relay (star) topology; player count = the core's controller-port count. *Untested on hardware.* |
| **M3 — rollback** | ✅ Code-complete — GGPO-style `RollbackStrategy` drops in behind `ISyncStrategy`; probe-gated + handshake-negotiated; 2-player. *Untested on hardware.* |
| M4 (NAT punch-through) | Not started |

### M0 findings (Genesis / GPGX, Contra Hard Corps)

- **Rollback qualifies**: ~787 KiB state, save/load/frame ≈ 0.2 ms vs a 16.688 ms budget → maxDepth ≈ 20–29. Probe reads the real console frame period, not 60.000 Hz.
- **Reentrant `FrameAdvance` from a frame callback works** (the doc's §6.2 "deciding experiment") → **synchronous** rollback repair is available for M3, not just catch-up mode.
- No `InvisibleEmulation` API → DispSpeedupFeatures/SoundThrottle hide path, as designed; `SpeedMode`/`LimitFramerate` modulation confirmed.

### Networking (adapted from the RemotePlay app)

- **UDP for the input hot path, TCP for the reliable control channel** (handshake, state transfer, checksums) — the proven split from RemotePlay, rather than hand-rolling reliable-over-UDP.
- UDP datagrams carry a `MAGIC + version` envelope and are **pinned to the peer's exact ip:port** (foreign/off-path packets dropped).
- Handshake **verifies rather than trusts**: ROM/core/version/sync-settings/layout must match and both cores must be deterministic, or the session is refused with a reason.
- Known gotcha inherited from RemotePlay: on a **"Public" Windows network profile, the firewall silently drops inbound UDP** while the TCP handshake still connects — i.e. "connected but permanently stalling." An inbound allow-rule for the port fixes it.

## Layout

```
src/
  BizHawkNetplay.Core/    netstandard2.0 — no BizHawk dependency, fully unit-testable
    Emu/    IEmuAdapter, StateHandle          (the seam BizHawk sits behind)
    Input/  ControllerLayout, InputSerializer (generic-over-any-core packing)
    Sync/   ISyncStrategy, LockstepStrategy, InputPipeline, FrameDecision, FrameDriver
    Probe/  CapabilityProbe, ProbeResult      (§5 rollback-feasibility math)
    Net/    ITransport, LoopbackTransport, UdpTransport, InputPacketCodec, PacingInfo
    Session/ PeerIdentity, SessionNegotiator, ControlChannel, Handshake, ClockEstimator
  BizHawkNetplay.Tool/    net48 — the only project that references BizHawk
    ProbeToolForm.cs      [ExternalTool] entry point (the M0 harness)
    EmuHawkAdapter.cs     IEmuAdapter bridged onto ApiHawk + emulator services
    InputSetController.cs  InputSet -> IController for invisible frame advance
tests/
  BizHawkNetplay.Core.Tests/  net5.0 xUnit — 21 tests, no EmuHawk required
```

## Building

Prereqs: .NET SDK (5.0+) and the .NET Framework 4.8 targeting pack. The tool compiles against
a local BizHawk install; point `BizHawkHome` at yours if it differs from the default in
[Directory.Build.props](Directory.Build.props):

```sh
# Core + tests (no BizHawk needed)
dotnet test tests/BizHawkNetplay.Core.Tests

# The external tool (needs BizHawk assemblies)
dotnet build src/BizHawkNetplay.Tool -p:BizHawkHome="X:\path\to\BizHawk"
```

A successful Tool build copies `BizHawkNetplay.Tool.dll` + `BizHawkNetplay.Core.dll` into
`<BizHawkHome>\ExternalTools\`. Disable with `-p:DeployToExternalTools=false`.

## Running netplay (M1)

Both machines load the **same ROM** in EmuHawk (matching core + BizHawk build), then open
**Tools → External Tools → BizHawk Netplay**.

- **Host:** pick *Host*, choose a port (default 47800), *Start Hosting*.
- **Join:** pick *Join*, enter the host's IP + port, *Join*.

On connect the tool verifies ROM/core/version/sync-settings/layout match (refusing with a reason
otherwise), transfers the host's savestate so both sims start identical, then runs. It trades
memory-hash checksums every 60 frames and halts with a frame number if a desync is ever detected.

**Frame-driving model:** the tool pauses EmuHawk and advances exactly one confirmed frame per
timer tick via `DoFrameAdvance` — it *owns the clock* rather than fighting EmuHawk's own loop
(which pausing would silence). This is what makes lockstep stalls safe.

**Current limitations (M1):**
- Pacing uses a WinForms timer (~coarse), so speed may sit a hair under 100%. Smooth
  `speedmode`/drift-corrected pacing is M2.
- Direct IP / LAN / port-forward only; NAT punch-through is M4 (patterns already scouted in the
  RemotePlay app).
- Rollback (M3) is available for 2-player sessions: tick **Prefer rollback** on both ends. It's
  granted only if the capability probe clears the depth threshold on both cores; otherwise the
  session falls back to lockstep automatically. 3–4 players are always lockstep.
- Refuses to run sensibly alongside movies/TAStudio/Lua is not yet enforced — avoid those during a session.

## Capability probe

The M0 probe lives inside the netplay tool as the **Capability Probe** button (EmuHawk requires
exactly one external-tool entry point per DLL, so it's folded in rather than a separate tool). It
times save/load/frame-advance on the loaded core and prints the per-core rollback verdict, saving
and restoring your position so it doesn't disturb play.