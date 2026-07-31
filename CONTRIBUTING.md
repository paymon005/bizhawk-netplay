# Building and contributing

## Prerequisites

- **.NET 10 SDK.** The major version matters: the test project targets `net10.0`, so an older SDK
  fails with `NETSDK1045` before it compiles anything. Core stays `netstandard2.0` and the Tool stays
  `net48` regardless — only the tests target a runtime.
- **.NET Framework 4.8 targeting pack.**
- A local **BizHawk 2.11.x** install, for the assemblies the tool compiles against. Point
  `BizHawkHome` at yours if it differs from the default in
  [Directory.Build.props](Directory.Build.props).

## Building

```sh
# Core + tests — no BizHawk needed
dotnet test tests/BizHawkNetplay.Core.Tests

# The external tool — needs BizHawk assemblies
dotnet build src/BizHawkNetplay.Tool -p:BizHawkHome="X:\path\to\BizHawk"
```

A successful Tool build copies `BizHawkNetplay.Tool.dll` and `BizHawkNetplay.Core.dll` into
`<BizHawkHome>\ExternalTools\`, and stages the same pair in `dist\`. Disable the first with
`-p:DeployToExternalTools=false`.

> **EmuHawk holds the tool DLL open.** Close it before building, or the copy step fails with a file
> lock. This is the single most common build failure here.

`dist\` is last-build-wins: a Debug build overwrites Release DLLs and vice versa. Build `-c Release`
immediately before attaching anything to a release.

## Source layout

```
src/
  BizHawkNetplay.Core/    netstandard2.0 — no BizHawk dependency, fully unit-testable
    Compat/  IsExternalInit                     (shim so `init`/records compile here)
    Diag/    RotatingLogFile                    (the on-disk session log)
    Emu/     IEmuAdapter, StateHandle           (the seam BizHawk sits behind)
    Input/   ControllerLayout, InputSerializer  (generic-over-any-core packing)
    Sync/    ISyncStrategy, LockstepStrategy, RollbackStrategy, InputPipeline,
             FrameDriver, PacingStats
    Probe/   CapabilityProbe, ProbeResult       (rollback-feasibility math)
    Net/     ITransport, MeshUdpTransport (mesh + punch + reliable control streams),
             ReliableUdpStream, StunClient, UpnpPortMapper, ConnectCode,
             InputPacketCodec, PeerRoute, MeshTokens, MeshEdgeReport
    Session/ PeerIdentity, SessionNegotiator, ControlChannel, Handshake, HandshakeCodec,
             SessionPhase, DelayAdvice, SustainedTrigger, RecoveryPolicy
  BizHawkNetplay.Tool/    net48 — the only project that references BizHawk
    NetplayToolForm*.cs   [ExternalTool] entry point, split into partials by region:
                          .Ui .Lobby .Punch .Session .Frame .Telemetry .Peers .Recovery
                          .Reconnect .Probe .Helpers .Types .HostCommands — shared state
                          lives in the root NetplayToolForm.cs, each partial owns state
                          only it uses
    EmuHawkAdapter*.cs    IEmuAdapter bridged onto ApiHawk + emulator services, split by
                          subject: .Identity .Input .InputDiagnostics .Output
    InputSetController.cs InputSet -> IController for invisible frame advance
    NetplaySettings.cs    persisted UI prefs
    NetplaySoundBuffer.cs the audio ring the session pumps EmuHawk's device from
    StopwatchClock.cs     IMonotonicClock over Stopwatch, for the probe and tuning
    SessionLogFile.cs     where this tool keeps its logs, and how many
tests/
  BizHawkNetplay.Core.Tests/  xUnit, multi-targeted net10.0 + net48 — no EmuHawk required
                              (run: dotnet test). net48 is the runtime the tool ships on.
```

### Where to put things

**Core is the testable half, and it is worth defending.** The Tool assembly cannot be tested at all —
it needs a live EmuHawk — so anything with no BizHawk dependency belongs in Core, even when its only
caller is the Tool. `RotatingLogFile` is the pattern: the Tool decides *where* logs live, Core
implements what a log file does, and the rules that would otherwise be untestable (rotation, bounded
buffering, failing safely) have tests.

Core also has **no third-party dependencies** and should keep none. That is why the handshake uses a
`key=value` line format rather than JSON. Where structured data has to cross the wire, flatten it on
the Tool side and let Core compare plain strings.

### Wire invariants

Things to preserve when touching the network layer, because breaking any of them fails quietly:

- **UDP carries the input hot path; a reliable channel carries control** — handshake, state transfer,
  checksums. Normally that channel is TCP. The exception is UDP Punch, where there is only one
  punched socket, so `ReliableUdpStream` re-implements the needed slice of TCP (sequencing,
  cumulative ACKs, retransmit, a flow window) and the *same* handshake code runs over it unchanged.
- **Every datagram carries a `MAGIC + version + type` envelope** and is resolved against known peer
  endpoints. Anything unrecognised is dropped unread — with one deliberate exception, a valid seat
  token, which is the whole symmetric-NAT mechanism.
- **Peer-supplied numbers are clamped** — input delay, player count. Without an upper bound a peer can
  report `delay=int.MaxValue` and the host loops billions of times seeding neutral inputs on the UI
  thread. The wire is untrusted even between friends.
- **Anything that changes what a byte on the wire means needs a `Protocol` bump.** That includes the
  checksum function: it is compared against a peer's value, so a silent change reads as a desync every
  interval rather than as a version mismatch.

## Tests

```sh
dotnet test tests/BizHawkNetplay.Core.Tests
```

Both target frameworks run. `net48` is not optional — it is the runtime the tool actually ships on,
and it has caught behaviour `net10.0` did not.

Tests carry their reasoning in the test name and a comment: what breaks if the property does not
hold, and why the case was chosen. Several pin decisions that would otherwise look arbitrary and be
"tidied" away later — pruning logs by filename rather than mtime, for instance.

## Testing without a second machine

Several EmuHawk instances on one machine will play against each other over loopback, which covers
most of the session lifecycle. Two diagnostics on the Diagnostics tab make that more than a smoke
test.

**Sim latency** delays inbound UDP so rollback can be exercised without a real link — but it delays
only what *that* instance receives. Setting it on the joiners alone leaves the host with input
arriving instantly, so the host never mispredicts and logs `rollbacks 0` while the joiners roll back
normally. **Set it on every instance, host included**, or you are testing one direction of one
machine.

The lobby's auto-delay folds in only the sim value of the machine it runs on, so a joiner's setting is
invisible to the delay the host chooses. A session run this way starts at the delay a real (0 ms)
loopback deserves — which is the point if you want to see rollback work hard, and the wrong thing if
you meant to test auto-delay.

**Force desync** injects a fake checksum mismatch at the next interval, which exercises the resync
path. **Simulate unresponsive** stops answering pings so the other side drops you in ~3 s.

What loopback cannot test: NAT of any kind, real packet loss, and the joiner↔joiner mesh legs as they
behave on a network rather than as loopback.

## CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs the suite on both target frameworks *and*
builds the shipping tool against a **BizHawk pinned by version and SHA-256**, downloaded per run. So
the DLL people install is compiled on every push rather than only on a maintainer's machine, and a
silently different BizHawk fails the build instead of quietly changing what that DLL was compiled
against. Both DLLs are uploaded as a build artifact.

Moving to a new BizHawk means bumping `BIZHAWK_VERSION` **and** `BIZHAWK_SHA256` together; the
workflow refuses to build if they disagree.

## Cutting a release

Binaries are never committed — they live on the Releases page.

1. **Bump the version in both csprojs** — `src/BizHawkNetplay.Tool` and `src/BizHawkNetplay.Core`.
   They ship together and must agree; a pair that disagrees about which release it is defeats the
   point of stamping either. The version is printed at the top of every log file, so a stale one
   actively misleads whoever is reading a log someone sent them.
2. **Bump `Protocol`** in `NetplayToolForm.cs` if anything on the wire changed, and add a line to the
   comment above it saying what and why. Add the row to [CHANGELOG.md](CHANGELOG.md).
3. Update [README.md](README.md) and [KNOWN-ISSUES.md](KNOWN-ISSUES.md) if the release changes what
   either claims. Say what is *not* proven as plainly as what is.
4. Build Release and verify the artifacts:

```powershell
dotnet build src/BizHawkNetplay.Tool -c Release
gh release create v0.0.0 dist\BizHawkNetplay.Tool.dll dist\BizHawkNetplay.Core.dll `
    --title "v0.0.0 - short summary" --target main --notes-file notes.md
```

Use `--notes-file`, not `--notes`: PowerShell re-parses quotes when building a native command line,
so notes containing a `"` or a newline get split and `gh` rejects the fragments as bad asset paths.

### Verify by content, not by timestamp

A fresh timestamp only proves *a* build happened. Before publishing, check that the artifacts contain
strings unique to the change:

```powershell
$b = [System.IO.File]::ReadAllBytes("dist\BizHawkNetplay.Core.dll")
$u0 = [System.Text.Encoding]::Unicode.GetString($b)
$u1 = [System.Text.Encoding]::Unicode.GetString($b, 1, $b.Length - 1)
$u0.Contains("some new string") -or $u1.Contains("some new string")
```

.NET string literals are UTF-16, so an ASCII search finds nothing — and they can start at either byte
alignment, which is why both are checked. Checking one alignment produces confident false negatives.

Also confirm both DLLs report the version you just set:

```powershell
[System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path "dist\BizHawkNetplay.Tool.dll"))
```

## Style

The code comments explain **why**, including what was tried and rejected, and name the symptom a
change fixed. That is deliberate: this is a codebase where the same wrong assumption is easy to make
twice, and a comment saying "measured, and it is not this" is worth more than one restating the code.

When a comment turns out to be wrong, correct it in the same change rather than leaving it beside the
fix. Several long comments here exist because an earlier one was confidently wrong.
