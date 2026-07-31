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
> refuses a mismatch, so when you update, your friends must update to the same release too. The
> protocol version numbers in the table below are historical — each says what that release changed.
> **v0.21.0 moves to protocol 13, a break from v0.20.0 (protocol 12)** — the lobby measures every UDP
> mesh edge before choosing input delay, the host can change netcode or delay mid-session, and every
> savestate transfer (the initial join, a resync, a rejoin, a settings change) is deflated on the wire
> instead of being sent raw. A v0.20.0 peer and a v0.21.0 peer will refuse each other at the
> handshake, which is the intended behaviour rather than a fault. **v0.22.0 and v0.23.0 keep protocol
> 13**, so anything from v0.21.0 onward mixes freely — update whenever suits you. v0.23.0 adds host
> relaying, which changes only what the host chooses to forward, not the format of anything on the wire.
> **v0.24.0 moves to protocol 14** — every seat now gets a token in its WELCOME and announces it over
> UDP, so a peer can be recognised at the address it actually arrives from rather than the one it
> advertised. A protocol-13 peer sends no token, and its packets would stay unroutable to anyone whose
> NAT rewrote the source port — silent one-way input loss — so the handshake refuses the mix instead.

## Status

Targets **BizHawk 2.11.x** (.NET Framework 4.8 build). Current progress:

| Milestone | State |
|---|---|
| **M0 — Probe harness** | ✅ Done. Runs the §5 probe + three API experiments. Validated on Genesis/GPGX (see below) |
| Core sync logic | Input serialization (digital **+ analog axes**), layout negotiation, input pipeline / confirmed-frontier, **lockstep + rollback** strategies — unit-tested |
| **M1 — 2-player lockstep** | ✅ Verified on hardware (two EmuHawk instances, Genesis/GPGX + N64): real-time pacing, working audio, desync detection (host saves quick-slot 10 on mismatch), configurable delay + packet redundancy |
| **M2 — hardening** | ✅ Live ping/RTT + delay hints, **desync auto-recovery** (mismatch → resync from an authoritative state instead of ending), alt-tab audio resilience |
| **2–4 players** | ✅ Host picks the player count (2 up to the core's port count); direct peer-to-peer input **full mesh** — every peer sends straight to every other, so input is normally one hop from its author and the host coordinates without carrying it. Where a joiner↔joiner leg fails to open, the host relays just that leg (one extra hop, decided at session start; it cannot help a peer whose leg to the *host* also failed) — with **host-as-rendezvous** connectivity checks (active hole-punch + UDP keepalive, per-peer direct-link status). **2P and 4P lockstep verified on hardware** in July 2025. **3P verified on three separate machines over the internet** in July 2026 — SNES rollback, N64 lockstep, and N64 rollback at delay 5 — see KNOWN-ISSUES KI-8. 4P remains measured on one machine only. |
| **M3 — rollback** | ✅ Code-complete — GGPO-style `RollbackStrategy` drops in behind `ISyncStrategy`; probe-gated + handshake-negotiated (or forced via the netcode dropdown). **Verified in real 3-player internet play** in July 2026 on SNES (Gauntlet II, low delay) and N64 (Pokemon Stadium, delay 5). |
| **M4 — NAT punch-through** | ✅ Code-complete — STUN + UPnP; **UDP Punch** (RemotePlay-style connect-code hole-punching) carries a whole 2-player session over a reliable-over-UDP control channel; host-as-rendezvous auto-punches the 3–4P mesh legs. Cone NAT, plus **symmetric NAT via per-seat tokens** as of v0.24.0 — code-complete, not yet proven on a real symmetric path. **The 3P mesh legs were punched over the real internet** in July 2026 across three separate machines; whether the connect-code UDP Punch admission path specifically was exercised there is not recorded. |
| **Session passwords** (v0.8.0) | ✅ Nonce challenge-response with a slow KDF — the password never crosses the wire and a captured proof can't be replayed. A refused joiner loses only its own connection; the host keeps hosting. **Protocol v4 — everyone must update.** |
| **Latency & liveness** (v0.9.0) | ✅ Audio cushion halved (a permanent 80ms video→audio offset, now 40ms); RTT measured on the UDP path input actually rides rather than the TCP control link; rollback time-syncs on a real **frame advantage** exchange, so the peer genuinely ahead yields instead of both guessing from a symmetric RTT; delay advice is mode-aware. Per-link drop detection no longer goes blind during a resync. *Protocol unchanged — mixes with v0.8.0 peers.* |
| **Punch admission** (v0.11.0) | ✅ Hosting and UDP punch are one flow, RemotePlay-style: the host just clicks **Start Hosting**; a joiner who can't reach it enters the host's IP and clicks **UDP Punch**, sends the code it gets, and the host pastes it to admit them — into the same 2–4 player lobby as TCP joiners, over a reliable control stream on the session's own UDP socket. One code per NAT'd joiner; no port-forwarding on their side. |
| **Generations & auto-delay** (v0.10.0) | ✅ Every session/timeline carries a **(session ID, epoch) generation** stamped on input, checksums, pacing, READY/GO and resync — stale-generation packets are rejected on every ingress path, so a rebuilt timeline can't be poisoned by the old one. The UDP mesh groups each peer's LAN + public endpoints as **routes**: all candidates probed, input rides the best live path, a silently-dead path fails over in ~2.5s. State transfers declare their size with **bounded, size-scaled deadlines**; the host **auto-selects input delay from lobby RTT** (capped, never lowers a manual ask). Rollback gains **gap retransmission** — a loss burst that outruns the redundant window no longer freezes both players forever. The whole diff was adversarially reviewed and every finding fixed (v0.10.1); open items in `KNOWN-ISSUES.md`. **Protocol v6 — everyone must update.** |

| **Host integration & compression** (v0.21.0) | ✅ The session now owns the emulator through BizHawk's own seams, from the moment the lobby opens rather than from GO: `BlockFrameAdvance` stops EmuHawk's run loop stepping the core, `IControlMainform` refuses Rewind and Reboot, and `BeforeQuickLoad` refuses Quick Load while leaving **Quick Save working normally**; any other load ends the session on the load itself. Pause, rewind and run-in-background are snapshotted and restored exactly as found. Axes rest at each axis's own Neutral (not 0), the "My controls" remap compares control *names* rather than counts, and audio finally honours the volume slider and mute. Every savestate transfer is deflated on the wire. CI builds the shipping DLL against a hash-pinned BizHawk 2.11.1. **Protocol v13 — everyone must update.** |

| **Input & host commands** (v0.22.0) | ✅ **The tool window no longer steals your controller.** BizHawk refuses host input outright while an external tool has focus (`IExternalToolForm => AllowInput.None`), so clicking this window mid-game stopped your pad — fixed the way TAStudio does it, conditionally, so typing an IP still goes only to the box. Input capture now reads `Joypad.Get`, the end of EmuHawk's own controller chain, instead of re-deriving the bind maths here. A **host loading a savestate now takes every player with it** — the same resync a desync recovery uses — while a joiner's load is refused. Plus **Watch Analog**, which reports every distinct value a stick actually delivers to the core. *Protocol unchanged — mixes with v0.21.0 peers.* |

| **Audio & mesh relay** (v0.23.0) | ✅ **Opening Config → Sound no longer kills audio for the rest of the session** — the dialog re-attaches EmuHawk's own provider (and may replace the `Sound` object outright), which then fought the session for the device at zero volume; ownership is now re-taken before every pump. Master mute works too, which it never had. Where a **joiner↔joiner UDP leg fails to open**, the host relays that leg rather than the pair simply never hearing each other — no external server, since the host is already the rendezvous. It cannot help a peer whose leg to the *host* also failed (true symmetric NAT), and now says so loudly instead of relaying into a void. *Protocol unchanged — mixes with v0.21.0+.* |

| **Symmetric NAT & named edges** (v0.24.0) | ✅ An advertised address is a guess: a symmetric NAT hands out a different public port per destination, so the endpoint a peer learns from STUN is valid for the STUN server and nobody else — everyone else saw its packets arrive from an address they were never told about, and dropped them unread. Each **seat now gets a 16-byte token**, minted by the host and delivered in WELCOME over the already-authenticated control channel; a peer announces it alongside its punch probes, and the receiver binds that seat to wherever the packet really came from. Tokens are keyed by seat and outlive the player in it, so a **rejoin on a new address is recognisable** with no redistribution. The learned address is probed and kept warm like any other, and the lobby now **names the edges** — who answered, who did not, and who could only be reached at an address they never advertised. **Code-complete; not yet proven on a real symmetric-NAT path.** **Protocol v14 — everyone must update.** |

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
    Net/    ITransport, MeshUdpTransport (mesh + punch + reliable control streams),
            ReliableUdpStream, StunClient, UpnpPortMapper, ConnectCode, InputPacketCodec
    Session/ PeerIdentity, SessionNegotiator, ControlChannel, Handshake, HandshakeCodec,
            SessionPhase (session lifecycle), DelayAdvice, SustainedTrigger, RecoveryPolicy
  BizHawkNetplay.Tool/    net48 — the only project that references BizHawk
    NetplayToolForm*.cs   [ExternalTool] entry point, split into partials by region:
                          .Ui .Lobby .Punch .Session .Frame .Telemetry .Peers .Recovery
                          .Reconnect .Probe .Helpers .Types — shared state lives in the
                          root NetplayToolForm.cs, each partial owns state only it uses
    EmuHawkAdapter.cs     IEmuAdapter bridged onto ApiHawk + emulator services
    InputSetController.cs  InputSet -> IController for invisible frame advance
    NetplaySettings.cs    persisted UI prefs (UPnP, port, delay, netcode, input source, recent IPs)
tests/
  BizHawkNetplay.Core.Tests/  xUnit, multi-targeted net10.0 + net48 — no EmuHawk required
                          (run: dotnet test); net48 is the runtime the tool actually ships on
```

## Building

Prereqs: the **.NET 10 SDK** and the .NET Framework 4.8 targeting pack. The SDK major matters — the
test project targets `net10.0`, so an older SDK fails with `NETSDK1045` before it compiles anything.
Core stays `netstandard2.0` and the Tool stays `net48` regardless; only the tests target a runtime.
The tool compiles against a local BizHawk install; point `BizHawkHome` at yours if it differs from
the default in [Directory.Build.props](Directory.Build.props):

```sh
# Core + tests (no BizHawk needed)
dotnet test tests/BizHawkNetplay.Core.Tests

# The external tool (needs BizHawk assemblies)
dotnet build src/BizHawkNetplay.Tool -p:BizHawkHome="X:\path\to\BizHawk"
```

A successful Tool build copies `BizHawkNetplay.Tool.dll` + `BizHawkNetplay.Core.dll` into
`<BizHawkHome>\ExternalTools\`. Disable with `-p:DeployToExternalTools=false`.

**CI** ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs the suite on both target
frameworks *and* builds the shipping tool, against a **BizHawk pinned by version and SHA-256** that
it downloads per run — so the DLL people install is compiled on every push, not only on a
maintainer's machine, and a silently different BizHawk fails the build instead of quietly changing
what that DLL was compiled against. Both DLLs are uploaded as a build artifact. Moving to a new
BizHawk means bumping `BIZHAWK_VERSION` **and** `BIZHAWK_SHA256` together; the workflow refuses to
build if they disagree.

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
- **Input delay** (host decides) — what you feel. In **lockstep** it must cover the one-way latency (≈ RTT/2) or the session stalls, so raise it on a bad link. In **rollback** prediction covers the link, so delay only shrinks how deep the average rollback runs — 1–2 is usually right, and anything higher is felt latency for nothing. The tool measures your link a few seconds in and tells you which way to move it, in either direction — and you can act on that advice without ending the session (see *Apply changes*).
- **Apply changes** (host, during a session) — netcode and input delay stay editable while you play. Change one, press *Apply changes*, and everyone stays connected: the session pauses for a moment, the host shares one savestate, every peer rebuilds on it under the new settings, and play resumes. Nobody reconnects and nobody has to re-host. It is the same machinery a desync recovery uses, so it costs what a resync costs — brief on Genesis or SNES, longer on a heavy core with a big state. The delay box shows the delay the session is *actually* on (which auto-delay may have raised above the floor you asked for), so what you see is what Apply sends. Switching to rollback is refused if any player's core can't replay, exactly as it would be at the start; if a session began on lockstep, the first switch spends about a second measuring this machine's rollback depth.
- **Connection status** — a running log of connection events right above the netcode box: hosting, connecting, joined, refused (with the reason), dropped, reconnected, ended. Red is a refusal or failure, green is connected. Everything else — per-frame diagnostics, audio, probe output — stays on the *Log* tab.

**Analog** sticks are networked (not just digital buttons), so N64/analog-pad games play with full stick control. During a session the status bar shows the emulation speed you're actually sustaining
(e.g. `55/60 fps (92%)`) and flags **CPU-bound** in orange when your machine can't run the core fast enough — the true cause of "lag" on a heavy core, distinct from any netcode issue.

Two more numbers appear when they matter, because "choppy" has more than one cause and they need different fixes:

- **`stall N%`** — the share of the time spent waiting on remote input. High means the network: either input delay isn't covering the link's worst moments, or the *other* machine can't hold full speed and you're waiting for it (check whether their fps reads CPU-bound — only faster core settings fix that one). The bar turns orange past 25%.
- **`present N`** — frames actually drawn, shown only when it falls behind frames emulated. A gap means a timer callback is emulating more than one frame and only the last of them reaches the screen. Every other reading — fps, CPU-bound, `stall%` — is computed from frames *emulated*, which hold at the console's rate precisely because the pacing code is succeeding, so this is the only place a session that reads `60/60 fps (100%)` can admit that the window is updating half that often. It feels like dropped inputs rather than like a slow display, which is why the tool now says so in the connection log once the gap persists. Nothing fixes it but cheaper frames: lower the render resolution or pick a lighter video plugin.

So: high `stall%` is a network or peer-speed problem, low `stall%` with fps under target is your own CPU or frame pacing, and a healthy fps with `present` well below it is the picture alone. Tick **Verbose log** on the Diagnostics tab for the full per-second breakdown — mean/p95/max core cost, gate, present, `undrawn` (pictures rendered and then thrown away unshown), `rebases` (how often the scheduler gave up on accumulated debt and discarded frames outright), and **`tick N/s`**.

That last one is a ceiling on everything else: a frame is presented at most once per timer callback, so if the tick rate falls below the console's frame rate the picture judders however fast the core runs. It should sit comfortably above 60.

**Sim latency** (Diagnostics tab) delays inbound UDP so rollback can be exercised without a second machine — but it delays only what *that* instance receives. Setting it on the joiners alone leaves the host with input arriving instantly, so the host never mispredicts and logs `rollbacks 0` while the joiners roll back normally. **Set it on every instance, host included**, or you are testing one direction of one machine. The lobby's auto-delay only folds in the sim value of the machine it is running on, so a joiner's setting is invisible to the delay the host chooses — a session run this way will start at the delay the real (0ms) loopback deserves, which is the point if you want to see rollback work hard, and the wrong thing if you meant to test auto-delay.

On connect the tool verifies ROM/core/version/sync-settings/layout match (refusing with a reason otherwise), transfers the host's savestate so both sims start identical, then runs. It trades memory-hash checksums every 300 frames (~5s) and, on a mismatch, resyncs everyone from the host's authoritative state (saving the diverged state to quick-slot 10 for inspection) rather than ending.

**Frame-driving model:** the tool pauses EmuHawk and steps the core exactly one confirmed frame per timer tick with only the merged network inputs — it *owns the clock* rather than fighting EmuHawk's own loop (which pausing would silence). This is what makes lockstep stalls safe, and it keeps input capture entirely out of the emulation path so both peers stay deterministic. Under load it renders only the last frame of a catch-up burst (Dolphin-style frame-skip) to keep heavy cores responsive.

### Heavy cores (N64 and friends)

N64 works (connects, plays, stays in sync, analog moves). BizHawk's N64 core is **interpreter-only (no dynamic recompiler)**, so it's often described as CPU-heavy — but measure before believing it: on a modern machine this core runs a frame in ~2-4ms against a 16.7ms budget, and what's expensive is its **savestate** (~6ms for a 16MiB state), which is a rollback cost rather than an emulation one. Two instances sharing one machine is still the worst case. To get it to full speed:

- **Core:** Mupen64Plus (not Ares64 — Ares is accurate but slower).
- **Video plugin:** **Rice** (or Glide64mk2), *not* the default GLideN64, and never Angrylion (software renderer). Worth 5–23% of the frame against GLideN64 — real, but an order of magnitude less than the resolution below it.
- **RSP:** Hle (the default). Keep GLideN64, if used, at native (1x) resolution with enhancements off.
- Both machines must use **identical** N64 settings (they're sync settings — a mismatch desyncs).
- N64 reports non-deterministic. That is not treated as a refusal (it usually just means determinism wasn't requested) — the session runs and desync detection guards you. You'll see a warning in the log; in practice it stays in sync.
- **Run the video plugin at native resolution.** Above it, N64 desyncs at *every* checksum — measured over a long two-machine session: at 800×600 every single checksum disagreed, in lockstep *and* in rollback; at native resolution the same pair ran 15,000+ frames with every checksum agreeing. The cause isn't the netcode. Rice and GLideN64 resolve their framebuffer back into RDRAM, and above native those bytes are produced by your GPU rather than the emulated core — so they differ between machines and land inside the region the desync checksum reads. Resyncing can't fix it; the tool now says so after the second consecutive disagreement.
- **Resolution is the frame cost, and it decides whether rollback is available.** Forty probes, five at each of four resolutions on each of two plugins, same machine and save (median frame cost, and the verdict across those five runs):

  | resolution | Rice | GLideN64 |
  |---|---|---|
  | 320×240 | 2.42 ms — rollback, depth 3 | 2.62 ms — rollback, depth 3 |
  | 800×600 | 2.64 ms — rollback, depth 3 | 3.26 ms — rollback, depth 3 |
  | 1400×1050 | 3.52 ms — depth 3, `MARGINAL` | 4.33 ms — depth 2, **lockstep only** |
  | 2880×2160 | 8.14 ms — depth 1, lockstep only | 8.57 ms — depth 1, lockstep only |

  Resolution is worth ~3.4× across that range; the plugin is worth 5–23% at a fixed resolution. Both matter, but not equally — and the plugin gap is what costs GLideN64 rollback at 1400×1050, one step earlier than Rice. Savestate cost stays flat at ~5.9 ms throughout, as it should: state size doesn't depend on how you render. An earlier independent sweep of the Rice column landed within 0.05 ms at three of the four points, so this is a replication rather than a single run.

  Since the desync boundary is *native* and the performance boundary is somewhere past 800×600, native is the setting that satisfies both.
- **`render: false` saves nothing on this core.** The probe times a frame both ways, and across all twelve runs the rendered and unrendered figures agree within ~5%, with the rendered one sometimes *cheaper* — i.e. the difference is noise. Mupen64Plus/Rice does its video work regardless of the flag, so skipping the render on a discarded catch-up frame buys no time here (it may still on other cores).

Watch the fps readout while you tune: at ~100% you're good; well under means CPU-bound (faster settings or a second machine, not netcode). Check `stall%` before blaming the core, though — on a heavy console the two look identical from the picture, and only one of them is fixed by video-plugin settings.

Also worth knowing: turning on **Verbose log** and reading the per-second `pacing:` line tells you whether the core is genuinely missing budget (`core mean` at or above the frame period) or whether the schedule discarded frames it could have run (`rebases` above zero).

**Rollback on N64 is available**, and no longer overridden to lockstep. The capability probe decides, using the model the session actually runs: a snapshot is skipped on any frame whose inputs are all already confirmed — most of them on a healthy link — so rollback's steady cost is the frame itself rather than a whole-core savestate every frame. The catch is depth. N64 measures a usable prediction horizon of about **3 frames**, so it hides roughly 3 frames of one-way latency and no more: worth it against someone nearby, not against someone far away, and lockstep remains a click away on the Netcode dropdown. When a misprediction does land, you pay a brief hitch where lockstep would have paid a stall. The session log tells you the depth it measured.

Frames that *are* still predicted get a snapshot every other one rather than every one, because the snapshot is where a repair's budget goes: 6.82 ms of the 9.24 ms a repaired frame costs, against 2.41 ms for the frame itself. A correction then restarts from the nearest keyframe at or before its target and replays at most one extra frame to reach it — cheap, at that ratio. It buys a frame of depth, and it brings a worst-case repair from ~31.5 ms down to ~27 ms, which is the first time one fits inside the frame tick's own ~28.4 ms budget instead of overrunning it as a hitch. The status line shows the price when it is paid: `last d3+1wb` is a depth-3 correction that walked back one frame. Going sparser is not better — past every third frame the walk-back costs more than the snapshots it saves.

See [Known limitations](#known-limitations) for the honest gaps (NAT scope, checksum scope, etc.).

## Capability probe

The M0 probe lives inside the netplay tool as the **Capability Probe** button (EmuHawk requires exactly one external-tool entry point per DLL, so it's folded in rather than a separate tool). It times save/load/frame-advance on the loaded core and prints the per-core rollback verdict, saving and restoring your position so it doesn't disturb play.

It reports **two** frame costs, because the two things a frame is used for need not cost the same: `frame=` is a frame advanced with rendering off — what a rollback repair re-simulates — and `live=` is one with video rendered, which is what the player's own frame costs. When `live=` alone eats the frame budget there is no rollback depth to have at any repair budget. On Mupen64Plus/Rice the two match within noise, so the render flag saves nothing there; that is a fact about the core, and the point of printing both is that you can see it rather than assume it.

Each probe line also carries the video settings it was measured under, so a run of them across resolutions is comparable by reading rather than by remembering.

It also reports `MARGINAL` when the median frame cost qualifies for rollback and the slow end of the same run does not — a heavy core's frame cost moves enough between runs to flip the verdict, and re-rolling the probe until it says what you want is not a fix.

### The repair line

Everything above is a term timed on its own, and the depth verdict is those terms added up: `load + depth × (frame + save)`. The second line checks that sum against the thing it claims to describe, by timing a **whole repair** — a load, then N frames re-simulated from it — at two depths:

```
repair 1f=3.812ms 8f=20.640ms (+saves 67.910ms) -> per-frame 2.404ms +save 5.887ms, load 1.408ms | modelled 67.800ms (+0.2%)
```

Two depths give a line: its slope is what one more re-simulated frame really costs and its intercept is what the load really costs. Running the deep pass twice — once snapshotting every re-simulated frame, once not — isolates the snapshot, because those two passes differ by nothing else.

That matters because none of the model's three assumptions is obvious on a recompiling core. A load from further back can invalidate the code cache, and the frames right after one run on caches the load has just cleared. Timing a load by itself would answer the narrower half of the question and miss exactly the effect most likely to bite. If the `per-frame`, `+save` and `load` figures come back matching `frame=`, `save=` and `load=` from the first line, the model describes the core; where they diverge, the difference says which term is wrong.

`REPAIR OVERRUNS MODEL` appears when the measured repair costs more than 15% over the modelled one. That is the direction that desyncs a session: the depth was solved from a sum that a real repair cannot meet, so every correction overruns its budget. Cheaper-than-modelled is reported too, as a negative percentage, and is not an alarm.

The first thing it caught was the probe. Saving does not advance the core, so the save pass was snapshotting memory nothing had touched since the previous sample, and the load pass was then restoring the state the core was already standing on — 16.7 MiB written back over identical bytes. Both are cheaper than the real operation: across six N64 configurations `save=` wandered between 5.6 and 6.7 ms with no pattern while the same snapshot timed inside a repair held steady at ~7.0 ms (±5%), and `load=` read ~1.4 ms against ~3.0. Understating both inflated the depth verdict, by enough to report 4 where the answer was 3. Those passes now advance a frame between samples, and the repair line stands as the standing cross-check.

Measured and reported, not spent: the depth is still solved from the isolated terms, which are now timed against state that actually changes. Costs about 0.7 s of extra freeze on N64. The pass that re-snapshots every frame runs only two frames deep: it is the dearest thing here, the probe sits on the connect path where it lands as a hitch on joining, and the snapshot is a per-frame cost that reads the same off any depth.

### Sweeping the probe unattended

`tools/probe-sweep.ps1` drives the whole thing — patch `config.ini`, launch EmuHawk, load a savestate, open the tool, click the probe, read the log, kill EmuHawk — once per configuration:

```powershell
.\tools\probe-sweep.ps1 -Config Rice:320x240,Rice:1280x960,GLideN64:320x240 -Runs 5
```

Each run is a **fresh EmuHawk**, so the core is always constructed with the plugin and resolution already in place rather than having them changed underneath it. The savestate slot follows the plugin (`-SlotByPlugin`), because the video plugin is a sync setting and a state saved under Rice is not the one to load under GLideN64. `-StateSlot 0` probes at boot instead, which is the only option for a game with no state.

Loading a state is about keeping the workload *still*, not about it being dearer. Eight runs each way on Super Smash Bros. put the boot screen at 2.21 ms a frame against 2.32 ms in-game — the same, inside the spread. But the probe's passes run over several seconds, and a booting game moves through logos, an intro and an attract demo while they do; the repair decomposition assumes a stationary cost, and on some boot runs it misreads badly enough to put the derived load at zero.

# To Do
- **Heavy-core performance:** BizHawk's N64 core is interpreter-only, so it's CPU-heavy. Frame-skip, audio-under-load smoothing, a frame-relative catch-up budget and pacing telemetry are in; moving emulation off the UI thread is *not* an option (cores are thread-affine — Waterbox/GL), so the remaining levers are core/plugin settings and a capable CPU.
- **State transfers are compressed (v13), but rollback's savestates are not.** Every whole-state transfer over the control channel — initial join, resync, rejoin, live settings change — is deflated, which is where the multi-second freezes on heavy cores came from. That is the *network* copy only. The rollback ring still saves and loads raw, deliberately: those are on the frame path, where the memcpy is the budget and compressing would cost more than it saves.
- **Rollback depth on heavy cores:** N64 runs rollback ~3 frames deep at native — enough for a nearby opponent, not a distant one. Sparse keyframes (snapshotting every *other* predicted frame; see below) is in and buys a frame of that. Beyond it the wall is arithmetic: the savestate is **74%** of what a repaired frame costs, and a 16.7 MiB state moves at memory bandwidth — 2.9 GB/s written, measured identically across ten games, both plugins and every resolution. Making the state smaller or incremental would be the real win and needs core support BizHawk doesn't expose.
- **The depth verdict is ~15% optimistic.** The probe's [repair line](#the-repair-line) says so on every stationary run, and the cause is known: `load=` is timed in isolation and the load defers work onto the frame that follows it, so the once-per-repair cost is nearer 3.8 ms than the 1.6 ms reported. Feeding the repair-derived terms to the solver is the fix; it moves the reported depth at native from 3 to what sparse keyframes now earns honestly.
- **A repair spends up to `MaxFramesPerTick` frame periods, and the catch-up path can run exactly that many back per tick.** The two are now tied rather than chosen alongside each other, so raising either alone can no longer leave repairs running up arrears the pacing rebase quietly discards.
- **Where N64's frame cost actually comes from:** mostly the render resolution — a controlled sweep puts it at 2.4 ms at 320×240 and 7.7 ms at 2880×2160 (see [Heavy cores](#heavy-cores-n64-and-friends)). An earlier uncontrolled sweep looked like noise and was read that way; it simply hadn't spanned enough of the range for the curve to clear the scatter. Whether resolution accounts for *all* of the 1.6–12 ms swing seen across one evening's sessions is still open — the top of that range is above anything measured here — but it is no longer an unexplained term.
- **Symmetric-NAT peers still can't HOST.** As of v0.24.0 such a peer can *join* and be recognised at
  the address it really arrives from (per-seat tokens, protocol 14), and the legs that still don't
  open are relayed through the host — which needs no external server because the host is already the
  rendezvous everyone reached. What none of that solves is a symmetric-NAT peer *hosting* without
  forwarding a port: there is no reachable rendezvous then, and that is the case a TURN-style server
  would be for. Relay is also still start-of-session only: a leg that dies mid-game is not failed
  over to it.
- **The symmetric-NAT path has never been exercised for real.** It is built, unit-tested against a
  transport that deliberately advertises the wrong port, and reasoned through — but no session has
  been played across an actual symmetric NAT, so treat it as untested rather than working.

## Known limitations

Things that are by-design gaps or not-yet-built, worth knowing before relying on it:

- **Desync detection hashes main RAM only** — not CPU/mapper/PPU/APU/RTC state. A divergence confined to non-RAM state can slip past the checksum until it perturbs RAM.
- **Sync-settings check is best-effort** — the handshake compares the core's real sync-settings blob (read via `ISettable.GetSyncSettings()`), so mismatched per-core settings (e.g. different N64 video plugins) are refused up front rather than desyncing. If a core's settings can't be read it falls back to a coarse core+version+system digest, which wouldn't catch such a mismatch.
- **NAT traversal punches cone NAT; symmetric NAT is handled a different way** — a symmetric router
  assigns a different mapping per destination, so there is no address such a peer can advertise that
  works for everyone, and it cannot be punched in the usual sense. As of v0.24.0 it doesn't have to
  be: the peer announces a **per-seat token** (handed out in WELCOME over the authenticated control
  channel) alongside its probes, and whoever receives it binds that seat to the address the packet
  genuinely came from. Legs that still don't open are relayed through the host. What symmetric NAT
  still prevents is *hosting* without a forwarded port — the host must be reachable (forwarded, or
  via the connect-code punch) either way, since it is the rendezvous for the joiner↔joiner mesh and
  the relay for it too. **This path is code-complete but has never been exercised on a real
  symmetric NAT.**
  The tool also **detects and names** the condition: pressing **My public address**, or starting a **UDP Punch**, asks two STUN servers for the same socket's mapping and compares them. A symmetric verdict is logged with what it does and doesn't break. It's advisory and never refuses a connection: two servers can be wrong in your favour.
- **Mesh input trusts peers** — datagrams are pinned to a known endpoint but not cryptographically bound to a controller port, so a malicious peer could submit input for a port it doesn't own. The
  v0.24.0 seat tokens don't change this: they keep *outsiders* out and let a member's real address be
  found, but every member is told every seat's token, so they are not a defence against each other.
  Fine for playing with people you trust; not a hostile-network guarantee.
- **The session password** is verified by a nonce challenge-response (`SessionAuth`): the password is never sent (not even hashed), a captured proof can't be replayed to another session, and role-tagging blocks a reflection attack. An empty password means an open session. A refused joiner (wrong password, wrong ROM/core, a HELLO that never arrives) only loses its own connection — the host logs it and keeps listening, so a typo doesn't cost you the lobby. Still not a fortress — it's a shared secret over a plaintext control channel with no forward secrecy — but it's a real gate, not an echo-able hash.
- **Movies / TAStudio / Lua aren't blocked** during a session — see the limitation above; avoid them.
- **Symmetric NAT is untested** over a real internet path — the token-learning path that is meant to
  make it work (see the NAT bullet above) has only ever been exercised by unit tests.
- **Three players works on real hardware over the internet.** Several 20–30 minute sessions on three
  separate machines: Gauntlet II (SNES) on **rollback** at low delay, Mario Golf (N64) on
  **lockstep**, and Pokémon Stadium (N64) on **rollback at delay 5**, all reported as playing well.
  No logs were kept, so those are players' judgements rather than measurements.
- **Four players, desync recovery and live settings changes are proven on a real internet path**
  (2026-07-30, logs kept): host on broadband, three joiners behind mobile carrier-grade NAT, all 12
  mesh paths answered. Six injected desyncs each recovered to `back in sync — recovery confirmed`,
  seven live delay/netcode changes rebuilt the session without dropping anyone, and protocol 13's
  compression moved a 421KiB state as 85KiB — 20% — arriving byte-exact every time.
- **Drop and rejoin is proven too** (same day, same setup). The network was pulled from a joiner
  mid-session; the host held the seat, froze the survivors at an epoch boundary, and the returning
  player re-joined into `epoch 2, 421KiB baseline synchronized` with all four agreeing on checksums
  afterwards. **The hold covers one missing player at a time** — a second drop during a pending
  reconnect ends the session by design, so peers sharing a single connection can't be recovered
  (pull that link and they all go at once). 4-player rollback was measured in July 2026 on four
  instances of one machine, absorbing a simulated 400ms round-trip at input delay 2 without stalling
  and hitting its ring cap at 600ms. Numbers and caveats in `KNOWN-ISSUES.md` KI-8 and KI-11.
- **The N64 Rice video plugin renders some games incorrectly.** A BizHawk plugin issue rather than a
  netplay one, but it looks like a netplay fault from the chair. Every peer must run the same plugin
  in any case — the handshake compares the core's sync-settings blob and refuses a mismatch up front.

## License

[MIT](LICENSE).