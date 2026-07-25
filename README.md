# BizHawk Netplay

A C# external tool (ApiHawk) that adds online multiplayer to any local-multiplayer game
BizHawk can run, using **delayed lockstep** as the universal baseline and **GGPO-style
rollback** as a probe-gated drop-in for cores that can afford it.

Design principle: lockstep and rollback are the *same system* with one swappable component
(`ISyncStrategy`). Everything else — transport, session, input serialization, determinism
enforcement, desync detection — is shared and built once. See
[bizhawk-netplay-architecture.md](bizhawk-netplay-architecture.md) for the full design.

## Download & install

Grab the two DLLs from the [**Releases**](../../releases) page (no compiler needed):

1. Download `BizHawkNetplay.Tool.dll` **and** `BizHawkNetplay.Core.dll`.
2. Copy **both** into your BizHawk install's **`ExternalTools`** folder (create it if missing).
3. The DLLs are unsigned, so Windows may flag them: right-click each → Properties → **Unblock**.
4. Start EmuHawk, load a game, then open **Tools → External Tools → BizHawk Netplay**.

Requires **BizHawk 2.11.x** (the .NET Framework 4.8 build) on Windows. Both players must run the
**same ROM** on the **same core and BizHawk build**. Prefer building it yourself? See
[Building](#building) — the Release build produces the exact same files.

## Status

Targets **BizHawk 2.11.x** (.NET Framework 4.8 build). Current progress:

| Milestone | State |
|---|---|
| **M0 — Probe harness** | ✅ Done. Runs the §5 probe + three API experiments. Validated on Genesis/GPGX (see below) |
| Core sync logic | Input serialization, layout negotiation, input pipeline / confirmed-frontier, **lockstep + rollback** strategies — unit-tested |
| **M1 — 2-player lockstep** | ✅ Verified on hardware (two EmuHawk instances, Genesis/GPGX): real-time pacing, working audio, desync detection (host saves quick-slot 10 on mismatch), configurable delay + packet redundancy |
| **M2 — hardening** | ✅ Live ping/RTT + delay hints, **desync auto-recovery** (mismatch → resync from an authoritative state instead of ending), alt-tab audio resilience |
| **3–4 players** | ✅ Code-complete — direct peer-to-peer input mesh; player count = the core's controller-port count. *Untested on hardware.* |
| **M3 — rollback** | ✅ Code-complete — GGPO-style `RollbackStrategy` drops in behind `ISyncStrategy`; probe-gated + handshake-negotiated (or forced via the netcode dropdown). *Untested on hardware.* |
| **M4 — NAT punch-through** | ✅ Code-complete — STUN + UPnP; **UDP Punch** (RemotePlay-style connect-code hole-punching) carries the whole session over a reliable-over-UDP control channel; 2-player, cone NAT. *Untested on real internet NAT (no second machine).* |

### M0 findings (Genesis / GPGX, Contra Hard Corps)

- **Rollback qualifies**: ~787 KiB state, save/load/frame ≈ 0.2 ms vs a 16.688 ms budget → maxDepth ≈ 20–29. Probe reads the real console frame period, not 60.000 Hz.
- **Reentrant `FrameAdvance` from a frame callback works** (the doc's §6.2 "deciding experiment") → **synchronous** rollback repair is available for M3, not just catch-up mode.
- No `InvisibleEmulation` API → DispSpeedupFeatures/SoundThrottle hide path, as designed; `SpeedMode`/`LimitFramerate` modulation confirmed.

### Networking (adapted from the RemotePlay app)

- **UDP for the input hot path, TCP for the reliable control channel** (handshake, state transfer, checksums) — the proven split from RemotePlay. The one exception is the **UDP Punch** path: with no port-forwarding there's only one punched UDP socket, so a `ReliableUdpStream` re-implements the essential slice of TCP (sequencing, cumulative ACKs, retransmit, a flow window) and the *same* handshake/state/checksum code runs over it unchanged.
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
  BizHawkNetplay.Core.Tests/  net5.0 xUnit — no EmuHawk required (run: dotnet test)
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

**Cutting a release:** [`build-dist.ps1`](build-dist.ps1) builds Release and stages the two DLLs in
`dist/` (gitignored). Pass a tag to publish them as a GitHub Release via the `gh` CLI:

```powershell
.\build-dist.ps1 -Tag v0.4.0    # build, stage dist\, and create the Release with both DLLs attached
```

## Running netplay (M1)

Both machines load the **same ROM** in EmuHawk (matching core + BizHawk build), then open
**Tools → External Tools → BizHawk Netplay**.

- **Host:** pick *Host*, choose a port (default 47800), *Start Hosting*.
- **Join:** pick *Join*, enter the host's IP + port, *Join*. (Recent hosts are remembered in the IP dropdown.)
- **UDP Punch** (2 players, no port-forwarding): each side picks its role, clicks *UDP Punch*, and
  gets a short **connect code**. Swap codes out of band (Discord/text), paste your friend's, and
  *Connect*. Both punch outbound and the whole session runs over that one UDP socket.

On connect the tool verifies ROM/core/version/sync-settings/layout match (refusing with a reason
otherwise), transfers the host's savestate so both sims start identical, then runs. It trades
memory-hash checksums every 60 frames and halts with a frame number if a desync is ever detected.

**Frame-driving model:** the tool pauses EmuHawk and advances exactly one confirmed frame per
timer tick via `DoFrameAdvance` — it *owns the clock* rather than fighting EmuHawk's own loop
(which pausing would silence). This is what makes lockstep stalls safe.

**Current limitations (M1):**
- Pacing uses a WinForms timer (~coarse), so speed may sit a hair under 100%. Smooth
  `speedmode`/drift-corrected pacing is M2.
- Direct IP / LAN / port-forward for any player count; for two players with no forwarding, **UDP
  Punch** hole-punches a direct link from swapped connect codes (cone NAT; symmetric NAT still needs
  a relay, not built). Host-as-rendezvous for automatic N-player punch is planned.
- The host picks the netcode from a dropdown: **Automatic** (rollback if both cores clear the
  capability probe, else lockstep), **Rollback** (forced, probe bypassed), or **Lockstep** (forced).
  The active mode is shown in a box on the Connection tab.
- Refuses to run sensibly alongside movies/TAStudio/Lua is not yet enforced — avoid those during a session.

## Capability probe

The M0 probe lives inside the netplay tool as the **Capability Probe** button (EmuHawk requires
exactly one external-tool entry point per DLL, so it's folded in rather than a separate tool). It
times save/load/frame-advance on the loaded core and prints the per-core rollback verdict, saving
and restoring your position so it doesn't disturb play.

# To Do
- anyway to help optimize which player uses what controller without them having to rebind their controls to only a certain port? how how is that currently being handled?
- the screen lags a bit on the sega for both users while a lot of going on on the screen, anyway to optimize this?
- n64 games don't seem to work 
  -connecting to 127.0.0.1:47800…
  -connection failed: this core is not running deterministically here
- players need to have their controls bound prior to joining, does this help with reliability, or can we change this after joining?
- incorporate host-as-rendezvous for UDP

## GPT Code Review

Code review — main at 9603e3c

High — input delays above eight permanently lose frame 0.
The UI permits delay 1–20, but every driver uses redundancy 8. Start() creates all D neutral frames, trims the window to eight, and only then sends it. At delay 9, frame 0 is discarded before the first packet: lockstep stalls immediately and rollback eventually hits its prediction cap. The code’s own requirement of R >= 2D+1 is also unenforced.
FrameDriver startup · window trimming · hardcoded redundancy
Fix: dynamically use at least 2 * delay + 1, or add reliable recovery for evicted frames. Add tests at delays 4, 9, and 20.
High — sync settings are not actually compared.
SyncSettingsDigest is currently a hash of core name | assembly version | system ID. Different core sync settings therefore pass the handshake and can deterministically diverge, despite the README claiming they are verified.
Placeholder digest
Fix: canonically serialize the core’s real sync-settings object and hash that.
High/security — the handshake is unbounded and has no timeout.
Remote delay is only lower-clamped. A client can echo the host’s HELLO with delay=2147483647; the host accepts it and later loops billions of times seeding inputs on the UI thread. Alternatively, a client can connect and never send HELLO, blocking the host’s handshake indefinitely.
Unbounded decode · blocking receive · remote delay accepted
Fix: enforce protocol limits such as delay 1–20, valid UDP ports and player counts, plus handshake read/write timeouts and cancellation.
High/integrity — relay endpoints are not bound to controller ports.
The relay checks that a datagram came from a known endpoint, but never verifies that its embedded port matches that endpoint’s assigned player. In 3–4 player sessions, P2 can submit input as P3 or the host; the pipeline accepts the first value received for that frame.
Endpoint-only peer list · blind relay
Fix: store endpoint → assigned port, decode enough of the packet to validate it before enqueueing or relaying, and ideally authenticate datagrams with a per-session key.
High — reconnect bypasses rollback qualification.
HostGreet may negotiate lockstep because the returning peer did not opt into rollback or failed its probe. The result is discarded, and CompleteReconnect sends the old session’s rollback mode anyway.
Greeting result discarded · existing mode forced
Fix: require the reconnect negotiation to reproduce the existing mode, player count, and compatible delay; otherwise reject the reconnect.
Medium-priority findings
Connection cancellation is unsafe. The emulator is paused before IP validation, so an invalid address leaves it paused. While joining, Disconnect cannot close the locally scoped TcpClient; the background thread may later start a “ghost” session. Start path · early-return teardown
Rollback savestates leak when a driver is replaced or destroyed. The strategy releases pruned entries, but has no disposal path; resync simply overwrites _driver, and teardown nulls it. Roughly 16–20 BizHawk state blobs can remain after every resync/session. State ring · driver replacement
Rollback probe results survive ROM/core changes. Restart() does not invalidate _probeDepth; MeasureRollbackDepth() returns the old cached value. Switching from a light core to a heavy one can incorrectly enable rollback. Restart · cache
Dead peers may never trigger reconnect. Pings are sent only after successfully advancing a frame. Once a lost peer causes lockstep to stall, pinging stops and the blocking TCP read can remain alive until the OS timeout. Frame-driven ping · reader loop
Joiners never clear their resync counter. The host clears its counter after a matching checksum, but joiners only increment theirs. Seven separate, successfully recovered resyncs eventually disconnect a healthy joiner as a “persistent desync.” Host-only reset · joiner counter
Coverage gaps
Analog axes are always transmitted as neutral, so analog-dependent games are not currently supported. Input capture
Desync detection hashes only main RAM, not CPU, mapper, PPU/APU, RTC, or other core state. Checksum implementation
All synchronization integration tests use delay 2, and there is no RelayUdpTransport test. The multiplayer rollback test uses a direct full-mesh simulator and disables time-sync, so it does not model the actual two-hop relay path.
The README is substantially behind the implementation: test count, removed ProbeToolForm, frame-driving method, and 3–4 player rollback status are stale.

I inspected the full 58-file snapshot. I could not independently execute the reported 60 tests because this review environment lacks the .NET SDK; the repository also currently has no CI workflow. No code was changed.