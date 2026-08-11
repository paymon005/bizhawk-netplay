# BizHawk Netplay

Online multiplayer for any local-multiplayer game BizHawk can run.

An external tool (ApiHawk) for **BizHawk 2.11.x** on Windows. It networks controller input between
2–4 players, keeps every machine's emulation identical, and detects and repairs divergence without
ending the session. Analog sticks are networked too, so N64 and other analog-pad games play with full
stick control.

Two netcodes, chosen automatically: **delayed lockstep** everywhere, and **GGPO-style rollback**
where the core is fast enough to afford it.

| | |
|---|---|
| **Download** | [Releases](../../releases) — two DLLs, no compiler needed |
| **Players** | 2–4 tested; up to 8 permitted, bounded by the core's controller-port count |
| **Requires** | BizHawk 2.11.x (.NET Framework 4.8 build), Windows |
| **Licence** | [MIT](LICENSE) |

---

## Contents

- [Install](#install)
- [Everyone must run the same version](#everyone-must-run-the-same-version)
- [Quick start](#quick-start)
- [Session settings](#session-settings)
- [While you are playing](#while-you-are-playing)
- [Logs](#logs)
- [Troubleshooting](#troubleshooting)
- [Limitations](#limitations)
- [More documentation](#more-documentation)

---

## Install

1. Download **both** `BizHawkNetplay.Tool.dll` and `BizHawkNetplay.Core.dll` from the
   [Releases](../../releases) page.
2. Copy both into your BizHawk install's **`ExternalTools`** folder (create it if it does not exist).
3. The DLLs are unsigned, so Windows may block them: right-click each → Properties → **Unblock**.
4. Start EmuHawk, load a game, then open **Tools → External Tools → BizHawk Netplay**.

Both DLLs report their version in Windows' file properties. They ship together and must be installed
together — if the two disagree, you have a half-updated folder.

Every player needs the **same ROM**, the **same core**, and the **same BizHawk build**. The tool
checks all three when you connect and refuses with a reason rather than letting you desync later.

Prefer to build it yourself? See [CONTRIBUTING.md](CONTRIBUTING.md) — the Release build produces the
same files.

## Everyone must run the same version

The network protocol is versioned, and the handshake refuses a mismatch. When you update, your
friends usually have to update too — but not always, and the release notes always say which.

**v0.41.0 uses protocol 25 and everyone must update together.** It will refuse a v0.40.0 peer at the
handshake. WELCOME gained one line, the shared-controls agreement, and it is the first thing on the
wire that changes *what the core is fed* rather than how a value is computed. An older peer would
ignore the line it does not know, fold nothing, and run different input on the same frame — every
message still parsing perfectly while the two machines drift apart. That is exactly the failure the
version check exists to turn into a clear refusal.

**v0.38.0 through v0.40.0 share protocol 24** and mix freely with each other. The bump at v0.38.0:
two values that cross the wire changed — the desync checksum's hash, which got about eight times
faster and now produces a different number for the same memory, and the password KDF, which moved
off SHA-1 and changed the key both sides prove against. Neither is something a peer could tolerate
or fall back from.

v0.34.0 through v0.37.0 shared protocol 23 and still mix freely with each other. See
[CHANGELOG.md](CHANGELOG.md) for the full table of which release changed what and who can play with
whom.

A version mismatch is refused at the handshake with a clear message. That is the intended behaviour,
not a fault — the alternative is a session that appears to work and silently loses input.

## Quick start

Everyone loads the same ROM in EmuHawk, then opens **Tools → External Tools → BizHawk Netplay**.

**Hosting**

1. Select **Host**.
2. Set **Players** (2 up to the core's port count) and a **Port** (default 47800).
3. Click **Start Hosting** and give the others your address.

The host needs to be reachable: either forward the port on your router, leave **Auto-forward host
port (UPnP)** ticked and hope your router honours it, or use UDP Punch below.

**Joining**

1. Select **Join**.
2. Enter the host's address — either a bare IP (`1.2.3.4`, using the *Port* box) or `1.2.3.4:47800`,
   in which case the typed port wins. Recent hosts are remembered in the dropdown with their ports.
3. Click **Join**.

**UDP Punch** — for a joiner who cannot reach the host

No port forwarding needed on the joiner's side. The host still has to be reachable.

1. **Host:** click **Start Hosting** as usual and leave the lobby waiting.
2. **Joiner:** select **Join**, enter the host's IP, and click **UDP Punch**. It shows a short
   **connect code**.
3. **Joiner:** send that code to the host out of band — Discord, text message — and stay put.
4. **Host:** paste it into **Joiner's code** and click **Admit**.

Only the joiner produces a code; the host never does. Punched joiners land in the same 2–4 player
lobby as everyone else and mix freely with players who connected normally — one code per NAT'd
joiner. Their whole session runs over that single punched UDP socket.

## Session settings

On the Connection tab. Netcode, input delay, shared controls and UPnP are greyed out when **Join**
is selected: those are the host's to set.

| Setting | Who sets it | What it does |
|---|---|---|
| **Players** | Host | How many of the core's controller ports to fill. Unused ports read neutral, so you can play 2-player on a 4-port core. Above 4 (a PSX with two multitaps, say) the code supports it and nobody has run it — the host says so in the log and names what gets harder. |
| **My controls** | Each player | Which of *your* controller-port bindings the tool reads (default *Use P1 pad*), independent of the port you are assigned in-game. |
| **Shared controls** | Host | Off by default. Merges every player's pad onto **controller 1** and holds the others neutral, for games where the players take turns on one joystick. |
| **Password** | Host + joiners | Optional; must match. Empty means an open session. |
| **Netcode** | Host | *Automatic*, *Rollback* (forced) or *Lockstep* (forced). |
| **Input delay** | Host | Frames of delay applied to your own input. |
| **Auto from ping** | Host | On by default. Measures every direct UDP path before play starts and picks the delay, up to **Max**. |
| **Apply changes** | Host, mid-session | Push a new netcode or delay to everyone without disconnecting. |

### Players

The box is capped at what the **loaded core** exposes, shown beside it as *"of N"*. N64 is 4
natively; Genesis is **2 until you enable the 4-Way Play / Team Player adapter** in the core's
controller settings, which reboots the core and raises the cap. Every player needs the same adapter
setting — the handshake compares per-port layouts.

### My controls

Set this so a player assigned P2, P3 or P4 keeps using their normal P1 pad with no rebinding.
Same-console ports share button order, so the bindings map across by name.

### Shared controls

Some games never gave player 2 a controller. On the Atari 7800, Robotron 2084 reads **controller 1
as the movement stick and controller 2 as the fire-direction stick** — for both players, because
two-player is alternating and Atari's manual says player 2 uses the left controller too. Seat 2
therefore lands on the aim stick: the character turns and shoots but never walks. Most alternating
arcade ports on the 2600, 7800 and NES have the same shape.

Tick this and every player's pad drives controller 1, with the other controllers held neutral, so
whoever's turn it is has the stick. Robotron then plays its one-joystick scheme — move with the
stick, fire with the trigger in the direction you are facing.

Leave it **off** for anything two players play at once; it is not how a normal two-player game is
meant to work. Two things it will not do: the host keeps Reset, Select, Pause and the difficulty
switches (on the 7800, Select is how carts pick 1P/2P mode), and it cannot give one player two
controllers at the same time, so true twin-stick play is still out of reach. If your controllers are
not the same type on every port — a light gun against a pad — the host is refused in the lobby and
told which seat.

### Password

Never sent over the wire. Both ends prove they know it with a nonce challenge-response. Getting it
wrong costs the joiner their connection attempt, not the host's lobby — the host logs the refusal and
keeps waiting.

### Netcode

**Automatic** picks rollback if every peer's core clears the capability probe, otherwise lockstep.
The mode actually in use is shown in a box on the tab.

A joiner is not asked for a preference, but its machine's measured rollback depth is sent regardless,
and the host drops the whole session to lockstep if any joiner's is too shallow.

Rollback works at **3–4 players**, not just 2. Every peer predicts the other ports and input travels
peer-to-peer in one hop, so rollbacks fire more often than in a 2-player session but run no deeper.

### Input delay

This is the setting you feel.

- In **lockstep**, delay must cover the one-way latency (≈ RTT ÷ 2) or the session stalls. Raise it
  on a bad link.
- In **rollback**, prediction covers the link, so delay only shrinks how deep the average rollback
  runs. 1–2 is usually right; higher is felt latency for nothing.

**Auto from ping** (on by default) measures every direct UDP path in the lobby and picks the delay for
you, up to the **Max** beside it. It picks once, before play starts, and never lowers a delay a
player explicitly asked for.

A few seconds into a session the tool measures your link again and tells you which way to move the
delay. You can act on that advice without ending the session — see below.

### Apply changes

Netcode and input delay stay editable while you play. Change one, press **Apply changes**, and
everyone stays connected: the session pauses briefly, the host shares one savestate, every peer
rebuilds on it under the new settings, and play resumes. Nobody reconnects and nobody re-hosts.

It is the same machinery a desync recovery uses, so it costs what a resync costs — brief on Genesis
or SNES, longer on a heavy core with a big state. Switching to rollback is refused if any player's
core cannot replay, exactly as it would be at the start.

## While you are playing

The status bar shows the emulation speed you are actually sustaining (e.g. `55/60 fps (92%)`) and
flags **CPU-bound** in orange when your machine cannot run the core fast enough. That is the usual
cause of "lag" on a heavy core, and it is not a netcode problem.

Two more numbers appear when they matter, because "choppy" has more than one cause and each needs a
different fix:

- **`stall N%`** — the share of time spent waiting on remote input. High means the network: either
  input delay is not covering the link's worst moments, or the *other* machine cannot hold full speed
  and you are waiting for it. The bar turns orange past 25%.
- **`present N`** — frames actually drawn, shown only when it falls behind frames emulated. A gap
  means more than one frame is being emulated per timer callback and only the last reaches the
  screen. It feels like dropped inputs rather than a slow display. Nothing fixes it but cheaper
  frames: lower the render resolution or pick a lighter video plugin.

Reading them together:

| Symptom | Cause | Fix |
|---|---|---|
| High `stall%` | Network, or a peer that cannot keep up | More input delay, or that peer's core settings |
| Low `stall%`, fps under target | Your own CPU | Lighter core/plugin settings |
| Healthy fps, `present` well below it | Drawing, not emulation | Lower resolution or a lighter plugin |

**Connection status**, above the netcode box, is a running log of connection events: hosting,
connecting, joined, refused (with the reason), dropped, reconnected, ended. Red is a refusal or
failure, green is connected. Everything else stays on the *Log* tab.

**Verbose log** on the Diagnostics tab adds a per-second breakdown — core cost mean/p95/max, gate,
present, and `tick N/s`. That last one is a ceiling on everything else: a frame is presented at most
once per timer callback, so if the tick rate falls below the console's frame rate the picture judders
however fast the core runs.

### What happens automatically

- **Desync detection.** Every 300 frames (~5s) peers trade a memory hash. On a mismatch the host
  resyncs everyone from its authoritative state rather than ending the session.
- **Drop and rejoin.** If a player loses their connection, the host holds their seat and freezes the
  others while they reconnect. One missing player at a time.
- **Frame pacing.** Under load the tool renders only the last frame of a catch-up burst, so heavy
  cores stay responsive.

## Logs

The **Log** tab carries the full diagnostic output, timestamped. From the moment you host or join it
is also written to a file:

```
%APPDATA%\BizHawkNetplay\logs
```

Use **Open log folder** on the Log tab to find it. Send that file to whoever is helping you — it
holds the whole session, including everything the window has scrolled past, and it survives EmuHawk
being closed or killed.

Opening the tool and closing it again writes nothing. The ten most recent logs are kept, and one
file stops growing at 32 MiB.

When reporting a problem, the other player's log is usually as useful as yours.

**Before posting one publicly**, know what is in it. A session log contains your public IP address,
your LAN address, the addresses of everyone you played with, and — on cores whose sync settings name
a file, N64 among them — a path under your user folder, which is to say your Windows username. It
does not contain the session password or the mesh tokens. That is fine for sending to someone you
are already playing with, who knows your address anyway; it is worth a look before it goes into a
public issue or a forum thread.

### N64 and other heavy cores

N64 works — it connects, plays, stays in sync, and analog moves — but it is the core most sensitive to
settings, and two of them matter more than the rest:

- **Run the video plugin at native resolution.** Above native, N64 desyncs at *every* checksum. This
  is not a netcode fault and resyncing cannot fix it.
- **Use Mupen64Plus with the Rice plugin**, not Ares64 or Angrylion.

Every player needs identical N64 settings — they are sync settings, and a mismatch is refused at the
handshake. Full detail, including what resolution costs and how deep rollback runs, is in
[docs/n64-tuning.md](docs/n64-tuning.md).

## Troubleshooting

**"Connected" but the game never starts, or input never arrives.**
On a **Public** Windows network profile the firewall silently drops inbound UDP while the TCP
handshake still connects. Add an inbound allow-rule for the port, or set the network to Private.

**A joiner cannot reach the host at all.**
The host must be reachable. Forward the port, or use **UDP Punch**. Check **My public address** on
the Connection tab to see what the outside world sees.

**The connection is refused with a reason.**
That is the handshake doing its job. It names what differs — ROM, core, BizHawk version, controller
layout, or a specific sync setting with both sides' values. Fix the named item on both machines and
reload the ROM.

**Repeated desyncs on N64.**
Run the video plugin at **native resolution**. Above it, N64 desyncs at every checksum — see
[docs/n64-tuning.md](docs/n64-tuning.md).

**It plays badly and you are not sure why.**
Read `stall%` before blaming the core. On a heavy console a network stall and a CPU-bound core look
identical from the chair, and only one of them is fixed by video settings.

## Limitations

Honest gaps, worth knowing before relying on it:

- **Desync detection hashes main RAM only** — not CPU/mapper/PPU/APU/RTC state. A divergence confined
  to non-RAM state can slip past until it perturbs RAM.
- **Mesh input trusts peers.** Datagrams are pinned to a known endpoint but not cryptographically
  bound to a controller port, so a malicious peer could submit input for a port it does not own. Fine
  for playing with people you trust; not a hostile-network guarantee.
- **The session password is a real gate, not a fortress.** The password is never sent, a captured
  proof cannot be replayed to another session, and reflection is blocked — but it is a shared secret
  over a plaintext control channel with no forward secrecy.
- **A symmetric-NAT peer can join but cannot host** without forwarding a port. Since v0.24.0 such a
  peer is recognised at the address it really arrives from, but the host must still be reachable
  because it is the rendezvous for everyone else.
- **Symmetric NAT has never been tested on a real network.** The path that handles it is built and
  unit-tested; no session has been played across an actual symmetric NAT.
- **A recording or playing movie, TAStudio and A/V capture are refused** at session start — the
  session steps the core itself, so a recorder never sees the frames actually played. **Lua is not
  blocked**, only warned about: a script can set input, load state or advance frames, any of which
  desyncs a session. Avoid it.
- **The N64 Rice video plugin renders some games incorrectly.** That is a BizHawk plugin issue rather
  than a netplay one, but it looks like a netplay fault from the chair.

### What has been proven, and what has not

- **Three players over the internet**, three separate machines: SNES on rollback at low delay, N64 on
  lockstep, N64 on rollback at delay 5. Reported as playing well; no logs kept, so these are players'
  judgements rather than measurements.
- **Four players over the internet** (2026-07-30, logs kept): host on broadband, three joiners behind
  mobile carrier-grade NAT, all 12 mesh paths answered. Six injected desyncs each recovered, seven
  live delay/netcode changes rebuilt the session without dropping anyone.
- **Drop and rejoin**, same session: the network was pulled from a joiner, the host held the seat, and
  the returning player rejoined with all four agreeing on checksums afterwards.
- **Not yet tested:** four *separate* machines (those three joiners shared one laptop, so the
  joiner↔joiner links were loopback), symmetric NAT on a real path, and a long soak.

Full detail, including what to measure when those gaps are closed, is in
[KNOWN-ISSUES.md](KNOWN-ISSUES.md).

## More documentation

| Document | What is in it |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | Release history and protocol compatibility |
| [KNOWN-ISSUES.md](KNOWN-ISSUES.md) | Open issues, validation status, what is fixed |
| [ROADMAP.md](ROADMAP.md) | What is planned and what is deliberately not |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Building, testing, CI, source layout, cutting a release |
| [docs/n64-tuning.md](docs/n64-tuning.md) | Getting N64 and other heavy cores to full speed |
| [docs/capability-probe.md](docs/capability-probe.md) | How rollback feasibility is measured |
| [bizhawk-netplay-architecture.md](bizhawk-netplay-architecture.md) | Full design |

## License

[MIT](LICENSE).
