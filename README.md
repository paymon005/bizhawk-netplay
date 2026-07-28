# BizHawk Netplay

A C# external tool (ApiHawk) that adds online multiplayer to any local-multiplayer game
BizHawk can run, using **delayed lockstep** as the universal baseline and **GGPO-style
rollback** as a probe-gated drop-in for cores that can afford it.

Design principle: lockstep and rollback are the *same system* with one swappable component (`ISyncStrategy`). Everything else — transport, session, input serialization, determinism enforcement, desync detection — is shared and built once. See
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
| **2–4 players** | ✅ Host picks the player count (2 up to the core's port count); direct peer-to-peer input mesh with **host-as-rendezvous** connectivity checks (active hole-punch + UDP keepalive, per-peer direct-link status). **2P and 4P verified on hardware** (3P runs the same N-player path). |
| **M3 — rollback** | ✅ Code-complete — GGPO-style `RollbackStrategy` drops in behind `ISyncStrategy`; probe-gated + handshake-negotiated (or forced via the netcode dropdown). *Untested on hardware.* |
| **M4 — NAT punch-through** | ✅ Code-complete — STUN + UPnP; **UDP Punch** (RemotePlay-style connect-code hole-punching) carries a whole 2-player session over a reliable-over-UDP control channel; host-as-rendezvous auto-punches the 3–4P mesh legs. Cone NAT. *Untested on real internet NAT (no second machine).* |
| **Session passwords** (v0.8.0) | ✅ Nonce challenge-response with a slow KDF — the password never crosses the wire and a captured proof can't be replayed. A refused joiner loses only its own connection; the host keeps hosting. **Protocol v4 — everyone must update.** |
| **Latency & liveness** (v0.9.0) | ✅ Audio cushion halved (a permanent 80ms video→audio offset, now 40ms); RTT measured on the UDP path input actually rides rather than the TCP control link; rollback time-syncs on a real **frame advantage** exchange, so the peer genuinely ahead yields instead of both guessing from a symmetric RTT; delay advice is mode-aware. Per-link drop detection no longer goes blind during a resync. *Protocol unchanged — mixes with v0.8.0 peers.* |
| **Punch admission** (v0.11.0) | ✅ Hosting and UDP punch are one flow, RemotePlay-style: the host just clicks **Start Hosting**; a joiner who can't reach it enters the host's IP and clicks **UDP Punch**, sends the code it gets, and the host pastes it to admit them — into the same 2–4 player lobby as TCP joiners, over a reliable control stream on the session's own UDP socket. One code per NAT'd joiner; no port-forwarding on their side. |
| **Generations & auto-delay** (v0.10.0) | ✅ Every session/timeline carries a **(session ID, epoch) generation** stamped on input, checksums, pacing, READY/GO and resync — stale-generation packets are rejected on every ingress path, so a rebuilt timeline can't be poisoned by the old one. The UDP mesh groups each peer's LAN + public endpoints as **routes**: all candidates probed, input rides the best live path, a silently-dead path fails over in ~2.5s. State transfers declare their size with **bounded, size-scaled deadlines**; the host **auto-selects input delay from lobby RTT** (capped, never lowers a manual ask). Rollback gains **gap retransmission** — a loss burst that outruns the redundant window no longer freezes both players forever. The whole diff was adversarially reviewed and every finding fixed (v0.10.1); open items in `KNOWN-ISSUES.md`. **Protocol v6 — everyone must update.** |

### M0 findings (Genesis / GPGX, Contra Hard Corps)

- **Rollback qualifies**: ~787 KiB state, save 0.41 ms / load 0.22 ms / frame 0.19 ms vs a 16.688 ms budget → maxDepth 54 (clamped to the ring cap of 16). Probe reads the real console frame period, not 60.000 Hz. Note the save costs about twice a frame, which is why skipping it on already-confirmed frames cuts rollback's steady cost from 0.60 ms to 0.19 ms.
- **Reentrant `FrameAdvance` from a frame callback works** (the doc's §6.2 "deciding experiment") → **synchronous** rollback repair is available for M3, not just catch-up mode.
- No `InvisibleEmulation` API → DispSpeedupFeatures/SoundThrottle hide path, as designed; `SpeedMode`/`LimitFramerate` modulation confirmed.

### Networking (adapted from the RemotePlay app)

- **UDP for the input hot path, TCP for the reliable control channel** (handshake, state transfer, checksums) — the proven split from RemotePlay. The one exception is the **UDP Punch** path: with no port-forwarding there's only one punched UDP socket, so a `ReliableUdpStream` re-implements the essential slice of TCP (sequencing, cumulative ACKs, retransmit, a flow window) and the *same* handshake/state/checksum code runs over it unchanged.
- UDP datagrams carry a `MAGIC + version` envelope and are **pinned to the peer's exact ip:port** (foreign/off-path packets dropped).
- Handshake **verifies rather than trusts**: ROM/core/version/sync-settings/layout must match, or the session is refused with a reason. A core reporting non-deterministic is *not* refused — that report usually means "determinism wasn't requested" rather than "this will diverge" — so the periodic checksum is what actually proves sync. Peer-supplied numbers (delay, player count) are clamped so a malformed/hostile peer can't hang the host.
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

Prereqs: .NET SDK (5.0+) and the .NET Framework 4.8 targeting pack. The tool compiles against a local BizHawk install; point `BizHawkHome` at yours if it differs from the default in [Directory.Build.props](Directory.Build.props):

```sh
# Core + tests (no BizHawk needed)
dotnet test tests/BizHawkNetplay.Core.Tests

# The external tool (needs BizHawk assemblies)
dotnet build src/BizHawkNetplay.Tool -p:BizHawkHome="X:\path\to\BizHawk"
```

A successful Tool build copies `BizHawkNetplay.Tool.dll` + `BizHawkNetplay.Core.dll` into
`<BizHawkHome>\ExternalTools\`. Disable with `-p:DeployToExternalTools=false`.

**Cutting a release:** build Release, then attach the two DLLs to a GitHub Release — that's all a release is. Binaries are never committed; they live on the Releases page.

```powershell
dotnet build src/BizHawkNetplay.Tool -c Release -p:DeployToExternalTools=false
gh release create v0.8.0 <release-output>\BizHawkNetplay.Tool.dll <release-output>\BizHawkNetplay.Core.dll `
    --title v0.8.0 --target main --notes-file notes.md
```

Use `--notes-file`, not `--notes`: PowerShell re-parses quotes when building a native command line, so notes containing a `"` or a newline get split and `gh` rejects the fragments as bad asset paths.

## Running netplay

Both machines load the **same ROM** in EmuHawk (matching core + BizHawk build), then open **Tools → External Tools → BizHawk Netplay**.

- **Host:** pick *Host*, choose a port (default 47800), *Start Hosting*.
- **Join:** pick *Join*, enter the host's address, *Join*. The box takes either a bare IP (`1.2.3.4`, using the *Port* box) or the `ip:port` form the host reads out (`1.2.3.4:47800`), in which case the typed port wins and the *Port* box updates to match. Recent hosts are remembered in the dropdown, with their ports.
- **UDP Punch** (2 players, no port-forwarding): each side picks its role, clicks *UDP Punch*, and
  gets a short **connect code**. Swap codes out of band (Discord/text), paste your friend's, and
  *Connect*. Both punch outbound and the whole session runs over that one UDP socket.

Settings worth knowing on the Connection tab:

- **Players** (host decides) — how many of the core's controller ports to fill, from 2 up to the core's port count. So you can play 2-player on a core that exposes 4 ports (e.g. N64); the unused ports read neutral. The box is capped at what the **loaded core** exposes, shown next to it as *"of N"*: N64 is 4 natively, but Genesis is **2 until you enable the 4-Way Play / Team Player adapter** in the core's controller settings (which reboots the core, and the cap follows). Both players must have the same adapter setting — the handshake compares per-port layouts.
- **My controls** — which of *your* controller-port bindings the tool reads (default *Use P1 pad*), independent of the port you're assigned in-game. So a player assigned P2/P3/P4 just uses their normal P1 pad with no rebinding.
- **Password** (optional) — must match on both ends. Leave it empty for an open session. It's never sent over the wire; both ends prove they know it via a nonce challenge-response (see *Known limitations*). Getting it wrong costs the joiner their connection attempt, not the host's lobby — the host logs the refusal and keeps waiting.
- **Netcode** (host decides) — **Automatic** (rollback if every peer's core clears the capability probe, else lockstep), **Rollback** (forced, probe bypassed), or **Lockstep** (forced). The active mode shows in a box on the tab. Netcode, input delay and UPnP are greyed out while **Join** is selected: they're the host's to set, and a joiner asks for nothing that could override them. What a joiner still decides isn't a preference — its machine's measured rollback depth is sent regardless, and the host drops to lockstep if any joiner's is too shallow. Rollback works at **3–4 players too**, not just 2: every peer predicts the other ports and input travels peer-to-peer in one hop, so rollbacks fire more often than in a 2-player session but run no deeper.
- **Input delay** (host decides) — what you feel. In **lockstep** it must cover the one-way latency (≈ RTT/2) or the session stalls, so raise it on a bad link. In **rollback** prediction covers the link, so delay only shrinks how deep the average rollback runs — 1–2 is usually right, and anything higher is felt latency for nothing. The tool measures your link a few seconds in and tells you which way to move it, in either direction.
- **Connection status** — a running log of connection events right above the netcode box: hosting, connecting, joined, refused (with the reason), dropped, reconnected, ended. Red is a refusal or failure, green is connected. Everything else — per-frame diagnostics, audio, probe output — stays on the *Log* tab.

**Analog** sticks are networked (not just digital buttons), so N64/analog-pad games play with full stick control. During a session the status bar shows the emulation speed you're actually sustaining
(e.g. `55/60 fps (92%)`) and flags **CPU-bound** in orange when your machine can't run the core fast enough — the true cause of "lag" on a heavy core, distinct from any netcode issue.

Two more numbers appear when they matter, because "choppy" has more than one cause and they need different fixes:

- **`stall N%`** — the share of the time spent waiting on remote input. High means the network: either input delay isn't covering the link's worst moments, or the *other* machine can't hold full speed and you're waiting for it (check whether their fps reads CPU-bound — only faster core settings fix that one). The bar turns orange past 25%.
- **`present N`** — frames actually drawn, shown only when it falls behind frames emulated. A gap means a timer callback is emulating more than one frame and only the last of them reaches the screen. Every other reading — fps, CPU-bound, `stall%` — is computed from frames *emulated*, which hold at the console's rate precisely because the pacing code is succeeding, so this is the only place a session that reads `60/60 fps (100%)` can admit that the window is updating half that often. It feels like dropped inputs rather than like a slow display, which is why the tool now says so in the connection log once the gap persists. Nothing fixes it but cheaper frames: lower the render resolution or pick a lighter video plugin.

So: high `stall%` is a network or peer-speed problem, low `stall%` with fps under target is your own CPU or frame pacing, and a healthy fps with `present` well below it is the picture alone. Tick **Verbose log** on the Diagnostics tab for the full per-second breakdown — mean/p95/max core cost, gate, present, `undrawn` (pictures rendered and then thrown away unshown), `rebases` (how often the scheduler gave up on accumulated debt and discarded frames outright), and **`tick N/s`**.

That last one is a ceiling on everything else: a frame is presented at most once per timer callback, so if the tick rate falls below the console's frame rate the picture judders however fast the core runs. It should sit comfortably above 60.

On connect the tool verifies ROM/core/version/sync-settings/layout match (refusing with a reason otherwise), transfers the host's savestate so both sims start identical, then runs. It trades memory-hash checksums every 300 frames (~5s) and, on a mismatch, resyncs everyone from the host's authoritative state (saving the diverged state to quick-slot 10 for inspection) rather than ending.

**Frame-driving model:** the tool pauses EmuHawk and steps the core exactly one confirmed frame per timer tick with only the merged network inputs — it *owns the clock* rather than fighting EmuHawk's own loop (which pausing would silence). This is what makes lockstep stalls safe, and it keeps input capture entirely out of the emulation path so both peers stay deterministic. Under load it renders only the last frame of a catch-up burst (Dolphin-style frame-skip) to keep heavy cores responsive.

### Heavy cores (N64 and friends)

N64 works (connects, plays, stays in sync, analog moves). BizHawk's N64 core is **interpreter-only (no dynamic recompiler)**, so it's often described as CPU-heavy — but measure before believing it: on a modern machine this core runs a frame in ~2-4ms against a 16.7ms budget, and what's expensive is its **savestate** (~6ms for a 16MiB state), which is a rollback cost rather than an emulation one. Two instances sharing one machine is still the worst case. To get it to full speed:

- **Core:** Mupen64Plus (not Ares64 — Ares is accurate but slower).
- **Video plugin:** **Rice** (or Glide64mk2), *not* the default GLideN64, and never Angrylion (software renderer). The plugin is the biggest adjustable cost.
- **RSP:** Hle (the default). Keep GLideN64, if used, at native (1x) resolution with enhancements off.
- Both machines must use **identical** N64 settings (they're sync settings — a mismatch desyncs).
- N64 reports non-deterministic. That is not treated as a refusal (it usually just means determinism wasn't requested) — the session runs and desync detection guards you. You'll see a warning in the log; in practice it stays in sync.
- **Run the video plugin at native resolution.** Above it, N64 desyncs at *every* checksum — measured over a long two-machine session: at 800×600 every single checksum disagreed, in lockstep *and* in rollback; at native resolution the same pair ran 15,000+ frames with every checksum agreeing. The cause isn't the netcode. Rice and GLideN64 resolve their framebuffer back into RDRAM, and above native those bytes are produced by your GPU rather than the emulated core — so they differ between machines and land inside the region the desync checksum reads. Resyncing can't fix it; the tool now says so after the second consecutive disagreement.

Watch the fps readout while you tune: at ~100% you're good; well under means CPU-bound (faster settings or a second machine, not netcode). Check `stall%` before blaming the core, though — on a heavy console the two look identical from the picture, and only one of them is fixed by video-plugin settings.

Also worth knowing: turning on **Verbose log** and reading the per-second `pacing:` line tells you whether the core is genuinely missing budget (`core mean` at or above the frame period) or whether the schedule discarded frames it could have run (`rebases` above zero).

**Rollback on N64 is available**, and no longer overridden to lockstep. The capability probe decides, using the model the session actually runs: a snapshot is skipped on any frame whose inputs are all already confirmed — most of them on a healthy link — so rollback's steady cost is the frame itself rather than a whole-core savestate every frame. The catch is depth. N64 measures a usable prediction horizon of about **3 frames**, so it hides roughly 3 frames of one-way latency and no more: worth it against someone nearby, not against someone far away, and lockstep remains a click away on the Netcode dropdown. When a misprediction does land, you pay a brief hitch where lockstep would have paid a stall. The session log tells you the depth it measured.

See [Known limitations](#known-limitations) for the honest gaps (NAT scope, checksum scope, etc.).

## Capability probe

The M0 probe lives inside the netplay tool as the **Capability Probe** button (EmuHawk requires exactly one external-tool entry point per DLL, so it's folded in rather than a separate tool). It times save/load/frame-advance on the loaded core and prints the per-core rollback verdict, saving and restoring your position so it doesn't disturb play.

It reports **two** frame costs, because the two things a frame is used for cost differently: `frame=` is a frame advanced with rendering off — what a rollback repair re-simulates — and `live=` is one with video rendered, which is what the player's own frame costs. The difference is the video plugin. When they are far apart, the setting worth changing is the render one; when `live=` alone eats the frame budget, there is no rollback depth to have at any repair budget.

It also reports `MARGINAL` when the median frame cost qualifies for rollback and the slow end of the same run does not — a heavy core's frame cost moves enough between runs to flip the verdict, and re-rolling the probe until it says what you want is not a fix.

# To Do
- **Heavy-core performance:** BizHawk's N64 core is interpreter-only, so it's CPU-heavy. Frame-skip, audio-under-load smoothing, a frame-relative catch-up budget and pacing telemetry are in; moving emulation off the UI thread is *not* an option (cores are thread-affine — Waterbox/GL), so the remaining levers are core/plugin settings and a capable CPU.
- **Rollback depth on heavy cores:** N64 now runs rollback, but only ~3 frames deep — enough for a nearby opponent, not a distant one. Going deeper means making the *repair* cheaper, not the steady state (that part is done): re-simulated frames still carry a savestate each, because a correction generally confirms only the frames near its own and leaves the rest of the window predicted. Sparse keyframes during repair are the obvious next lever.
- **Where N64's frame cost actually comes from:** the same machine, game and settings measured frame costs from 1.6 ms to 12 ms across one evening, with the probe correctly predicting the live cost every time. Render resolution is *not* the variable — fourteen probes across every Rice setting landed in a 1.9–3.6 ms band, and a 1280×960 session ran cheaper than several lower ones. Depth, tick rate, picture rate and input feel are all downstream of this term, so it outranks every other performance lever until it is understood.
- **Symmetric-NAT traversal:** a TURN-style relay fallback for the peers cone-NAT punching can't reach.

## Known limitations

Things that are by-design gaps or not-yet-built, worth knowing before relying on it:

- **Desync detection hashes main RAM only** — not CPU/mapper/PPU/APU/RTC state. A divergence confined to non-RAM state can slip past the checksum until it perturbs RAM.
- **Sync-settings check is best-effort** — the handshake compares the core's real sync-settings blob (read via `ISettable.GetSyncSettings()`), so mismatched per-core settings (e.g. different N64 video plugins) are refused up front rather than desyncing. If a core's settings can't be read it falls back to a coarse core+version+system digest, which wouldn't catch such a mismatch.
- **NAT traversal is cone-only** — UDP Punch and the mesh connectivity checks open cone-NAT paths;
  **symmetric NAT** (a different mapping per destination) still needs a TURN-style relay, which isn't built. The host must also be reachable (forwarded, or via the connect-code punch) to act as the rendezvous for the joiner↔joiner mesh.
- **Mesh input trusts peers** — datagrams are pinned to a known endpoint but not cryptographically bound to a controller port, so a malicious peer could submit input for a port it doesn't own. Fine
  for playing with people you trust; not a hostile-network guarantee.
- **The session password** is verified by a nonce challenge-response (`SessionAuth`): the password is never sent (not even hashed), a captured proof can't be replayed to another session, and role-tagging blocks a reflection attack. An empty password means an open session. A refused joiner (wrong password, wrong ROM/core, a HELLO that never arrives) only loses its own connection — the host logs it and keeps listening, so a typo doesn't cost you the lobby. Still not a fortress — it's a shared secret over a plaintext control channel with no forward secrecy — but it's a real gate, not an echo-able hash.
- **Movies / TAStudio / Lua aren't blocked** during a session — see the limitation above; avoid them.
- **Symmetric NAT is untested** over a real internet path, as is rollback with more than 2 players (3–4P forces lockstep). 2P and 4P sessions are verified working; everything below the socket layer is unit-tested.

## License

[MIT](LICENSE).