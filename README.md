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
| **M0 — Probe harness** | External tool builds & loads; runs the §5 capability probe and the three API experiments |
| Core sync logic (M1 groundwork) | Input serialization, controller-layout negotiation, input pipeline / confirmed-frontier, lockstep strategy — all unit-tested |
| M1 — 2-player lockstep | Not started (transport, session handshake, FrameDriver) |
| M2–M4 | Not started |

## Layout

```
src/
  BizHawkNetplay.Core/    netstandard2.0 — no BizHawk dependency, fully unit-testable
    Emu/    IEmuAdapter, StateHandle          (the seam BizHawk sits behind)
    Input/  ControllerLayout, InputSerializer (generic-over-any-core packing)
    Sync/   ISyncStrategy, LockstepStrategy, InputPipeline, FrameDecision
    Probe/  CapabilityProbe, ProbeResult      (§5 rollback-feasibility math)
    Net/    PacingInfo
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

## Running the M0 probe

1. Build the Tool (deploys to `ExternalTools\` by default).
2. Launch EmuHawk, load a ROM.
3. **Tools → External Tool → BizHawk Netplay — Capability Probe**.
4. *Run Capability Probe* times save/load/frame-advance and prints the per-core rollback
   verdict. *Run API Experiments* answers the reentrant-frame-advance question and checks the
   speed/hide controls the repair path depends on.
