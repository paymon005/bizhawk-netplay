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

> **Everyone must be on the same version.** The network protocol is versioned and the handshake
> refuses a mismatch, so when you update, your friends must update to the same release too.

## Status

Targets **BizHawk 2.11.x** (.NET Framework 4.8 build). Current progress:

| Milestone | State |
|---|---|
| **M0 — Probe harness** | ✅ Done. Runs the §5 probe + three API experiments. Validated on Genesis/GPGX (see below) |
| Core sync logic | Input serialization (digital **+ analog axes**), layout negotiation, input pipeline / confirmed-frontier, **lockstep + rollback** strategies — unit-tested |
| **M1 — 2-player lockstep** | ✅ Verified on hardware (two EmuHawk instances, Genesis/GPGX + N64): real-time pacing, working audio, desync detection (host saves quick-slot 10 on mismatch), configurable delay + packet redundancy |
| **M2 — hardening** | ✅ Live ping/RTT + delay hints, **desync auto-recovery** (mismatch → resync from an authoritative state instead of ending), alt-tab audio resilience |
| **2–4 players** | ✅ Host picks the player count (2 up to the core's port count); direct peer-to-peer input mesh with **host-as-rendezvous** connectivity checks (active hole-punch + UDP keepalive, per-peer direct-link status). 2P verified on hardware; 3–4P *untested on hardware.* |
| **M3 — rollback** | ✅ Code-complete — GGPO-style `RollbackStrategy` drops in behind `ISyncStrategy`; probe-gated + handshake-negotiated (or forced via the netcode dropdown). *Untested on hardware.* |
| **M4 — NAT punch-through** | ✅ Code-complete — STUN + UPnP; **UDP Punch** (RemotePlay-style connect-code hole-punching) carries a whole 2-player session over a reliable-over-UDP control channel; host-as-rendezvous auto-punches the 3–4P mesh legs. Cone NAT. *Untested on real internet NAT (no second machine).* |

### M0 findings (Genesis / GPGX, Contra Hard Corps)

- **Rollback qualifies**: ~787 KiB state, save/load/frame ≈ 0.2 ms vs a 16.688 ms budget → maxDepth ≈ 20–29. Probe reads the real console frame period, not 60.000 Hz.
- **Reentrant `FrameAdvance` from a frame callback works** (the doc's §6.2 "deciding experiment") → **synchronous** rollback repair is available for M3, not just catch-up mode.
- No `InvisibleEmulation` API → DispSpeedupFeatures/SoundThrottle hide path, as designed; `SpeedMode`/`LimitFramerate` modulation confirmed.

### Networking (adapted from the RemotePlay app)

- **UDP for the input hot path, TCP for the reliable control channel** (handshake, state transfer, checksums) — the proven split from RemotePlay. The one exception is the **UDP Punch** path: with no port-forwarding there's only one punched UDP socket, so a `ReliableUdpStream` re-implements the essential slice of TCP (sequencing, cumulative ACKs, retransmit, a flow window) and the *same* handshake/state/checksum code runs over it unchanged.
- UDP datagrams carry a `MAGIC + version` envelope and are **pinned to the peer's exact ip:port** (foreign/off-path packets dropped).
- Handshake **verifies rather than trusts**: ROM/core/version/sync-settings/layout must match and both cores must be deterministic (or both opt into the experimental non-deterministic override), or the session is refused with a reason. Peer-supplied numbers (delay, player count) are clamped so a malformed/hostile peer can't hang the host.
- Known gotcha inherited from RemotePlay: on a **"Public" Windows network profile, the firewall silently drops inbound UDP** while the TCP handshake still connects — i.e. "connected but permanently stalling." An inbound allow-rule for the port fixes it.

## Layout

```
src/
  BizHawkNetplay.Core/    netstandard2.0 — no BizHawk dependency, fully unit-testable
    Emu/    IEmuAdapter, StateHandle          (the seam BizHawk sits behind)
    Input/  ControllerLayout, InputSerializer (generic-over-any-core packing)
    Sync/   ISyncStrategy, LockstepStrategy, RollbackStrategy, InputPipeline, FrameDriver
    Probe/  CapabilityProbe, ProbeResult      (§5 rollback-feasibility math)
    Net/    ITransport, MeshUdpTransport (mesh + punch), PunchedPeerLink + ReliableUdpStream
            (UDP-punch path), StunClient, UpnpPortMapper, ConnectCode, InputPacketCodec
    Session/ PeerIdentity, SessionNegotiator, ControlChannel, Handshake, HandshakeCodec
  BizHawkNetplay.Tool/    net48 — the only project that references BizHawk
    NetplayToolForm.cs    [ExternalTool] entry point (host/join, session UI, folds in the probe)
    EmuHawkAdapter.cs     IEmuAdapter bridged onto ApiHawk + emulator services
    InputSetController.cs  InputSet -> IController for invisible frame advance
    NetplaySettings.cs    persisted UI prefs (UPnP, port, delay, netcode, input source, recent IPs)
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

## Running netplay

Both machines load the **same ROM** in EmuHawk (matching core + BizHawk build), then open
**Tools → External Tools → BizHawk Netplay**.

- **Host:** pick *Host*, choose a port (default 47800), *Start Hosting*.
- **Join:** pick *Join*, enter the host's IP + port, *Join*. (Recent hosts are remembered in the IP dropdown.)
- **UDP Punch** (2 players, no port-forwarding): each side picks its role, clicks *UDP Punch*, and
  gets a short **connect code**. Swap codes out of band (Discord/text), paste your friend's, and
  *Connect*. Both punch outbound and the whole session runs over that one UDP socket.

Settings worth knowing on the Connection tab:

- **Players** (host decides) — how many of the core's controller ports to fill, from 2 up to the
  core's port count. So you can play 2-player on a core that exposes 4 ports (e.g. N64); the unused
  ports read neutral.
- **My controls** — which of *your* controller-port bindings the tool reads (default *Use P1 pad*),
  independent of the port you're assigned in-game. So a player assigned P2/P3/P4 just uses their
  normal P1 pad with no rebinding.
- **Netcode** (host decides) — **Automatic** (rollback if both cores clear the capability probe, else
  lockstep), **Rollback** (forced, probe bypassed), or **Lockstep** (forced). The active mode shows in
  a box on the tab.

**Analog** sticks are networked (not just digital buttons), so N64/analog-pad games play with full
stick control. During a session the status bar shows the emulation speed you're actually sustaining
(e.g. `55/60 fps (92%)`) and flags **CPU-bound** in orange when your machine can't run the core fast
enough — the true cause of "lag" on a heavy core, distinct from any netcode issue.

On connect the tool verifies ROM/core/version/sync-settings/layout match (refusing with a reason
otherwise), transfers the host's savestate so both sims start identical, then runs. It trades
memory-hash checksums every 60 frames and, on a mismatch, resyncs everyone from the host's
authoritative state (saving the diverged state to quick-slot 10 for inspection) rather than ending.

**Frame-driving model:** the tool pauses EmuHawk and steps the core exactly one confirmed frame per
timer tick with only the merged network inputs — it *owns the clock* rather than fighting EmuHawk's
own loop (which pausing would silence). This is what makes lockstep stalls safe, and it keeps input
capture entirely out of the emulation path so both peers stay deterministic. Under load it renders
only the last frame of a catch-up burst (Dolphin-style frame-skip) to keep heavy cores responsive.

### Heavy cores (N64 and friends)

N64 works (connects, plays, stays in sync, analog moves), but BizHawk's N64 core is **interpreter-only
(no dynamic recompiler)**, so it's CPU-heavy — worst when two instances share one machine. To get it
to full speed:

- **Core:** Mupen64Plus (not Ares64 — Ares is accurate but slower).
- **Video plugin:** **Rice** (or Glide64mk2), *not* the default GLideN64, and never Angrylion (software
  renderer). The plugin is the biggest adjustable cost.
- **RSP:** Hle (the default). Keep GLideN64, if used, at native (1x) resolution with enhancements off.
- Both machines must use **identical** N64 settings (they're sync settings — a mismatch desyncs).
- N64 reports non-deterministic, so tick the **experimental override** on the Diagnostics tab on *both*
  ends. In practice it stays in sync; desync detection guards you.

Watch the fps readout while you tune: at ~100% you're good; well under means CPU-bound (faster
settings or a second machine, not netcode).

See [Known limitations](#known-limitations) for the honest gaps (NAT scope, checksum scope, etc.).

## Capability probe

The M0 probe lives inside the netplay tool as the **Capability Probe** button (EmuHawk requires
exactly one external-tool entry point per DLL, so it's folded in rather than a separate tool). It
times save/load/frame-advance on the loaded core and prints the per-core rollback verdict, saving
and restoring your position so it doesn't disturb play.

# To Do
- **Heavy-core performance:** BizHawk's N64 core is interpreter-only, so it's CPU-heavy. Frame-skip and audio-under-load smoothing are in; moving emulation off the UI thread is *not* an option (cores are thread-affine — Waterbox/GL), so the real levers are core/plugin settings and a capable CPU.
- **Symmetric-NAT traversal:** a TURN-style relay fallback for the peers cone-NAT punching can't reach.
- **Guard movies / TAStudio / Lua:** detect and refuse them at session start instead of only documenting it.
- **Compare real sync settings:** hash the core's actual sync-settings blob at handshake so mismatched per-core settings (e.g. different N64 plugins) are caught up front instead of surfacing as a desync.
- **Authenticate the session password:** today both peers just exchange a SHA-256 hash and compare, so a peer on the wire can echo the hash back without knowing the password. A nonce challenge-response with a slow KDF would make the password a real gate rather than a casual one.

## Known limitations

Things that are by-design gaps or not-yet-built, worth knowing before relying on it:

- **Desync detection hashes main RAM only** — not CPU/mapper/PPU/APU/RTC state. A divergence confined to non-RAM state can slip past the checksum until it perturbs RAM.
- **The sync-settings check is coarse** — the handshake compares core + assembly version + system ID, not the core's full sync-settings blob, so two peers with the same core but different per-core sync settings (e.g. different N64 video plugins) can pass the handshake and then desync. Match settings on both machines manually.
- **NAT traversal is cone-only** — UDP Punch and the mesh connectivity checks open cone-NAT paths;
  **symmetric NAT** (a different mapping per destination) still needs a TURN-style relay, which isn't built. The host must also be reachable (forwarded, or via the connect-code punch) to act as the rendezvous for the joiner↔joiner mesh.
- **Mesh input trusts peers** — datagrams are pinned to a known endpoint but not cryptographically bound to a controller port, so a malicious peer could submit input for a port it doesn't own. Fine
  for playing with people you trust; not a hostile-network guarantee.
- **The session password is a casual gate, not authentication** — peers exchange a SHA-256 hash and compare it, so it keeps out someone who doesn't know the password but not someone on the wire who can echo the hash. Treat it as "don't join by accident", not as protection against a determined attacker.
- **Movies / TAStudio / Lua aren't blocked** during a session — see the limitation above; avoid them.
- **Untested on real hardware** for 3–4 players and any over-the-internet NAT path (developed on a single machine). Everything below the socket layer is unit-tested; the last mile needs two boxes.