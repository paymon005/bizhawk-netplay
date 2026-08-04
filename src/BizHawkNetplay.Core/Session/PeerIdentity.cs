using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Everything two peers must agree on before a session can be deterministic, exchanged in the
/// handshake. The tool verifies configuration rather than trusting it (§3.1): any mismatch
/// here refuses the session up front instead of surfacing later as a silent desync.
/// </summary>
public sealed class PeerIdentity
{
    public PeerIdentity(
        int protocolVersion,
        string romHash,
        string coreName,
        string coreVersion,
        string syncSettingsDigest,
        IReadOnlyList<string> portLayoutDigests,
        bool deterministic,
        int maxRollbackDepth,
        IReadOnlyList<KeyValuePair<string, string>>? syncSettingsFields = null,
        bool syncSettingsReadable = true,
        string? videoSettings = null,
        string? buildId = null,
        string? firmwareHash = null,
        IReadOnlyList<string>? discHashes = null)
    {
        DiscHashes = discHashes ?? Array.Empty<string>();
        SyncSettingsReadable = syncSettingsReadable;
        VideoSettings = videoSettings ?? "";
        BuildId = buildId ?? "";
        FirmwareHash = firmwareHash ?? "";
        SyncSettingsFields = syncSettingsFields ?? Array.Empty<KeyValuePair<string, string>>();
        ProtocolVersion = protocolVersion;
        RomHash = romHash ?? "";
        CoreName = coreName ?? "";
        CoreVersion = coreVersion ?? "";
        SyncSettingsDigest = syncSettingsDigest ?? "";
        PortLayoutDigests = portLayoutDigests ?? Array.Empty<string>();
        Deterministic = deterministic;
        MaxRollbackDepth = maxRollbackDepth;
    }

    public int ProtocolVersion { get; }
    public string RomHash { get; }
    public string CoreName { get; }
    public string CoreVersion { get; }
    public string SyncSettingsDigest { get; }

    /// <summary>
    /// The same sync settings as a flat, sorted <c>name → value</c> list — what
    /// <see cref="SyncSettingsDigest"/> is a hash OF, in a form that can be diffed.
    ///
    /// Explanatory only. The digest remains the decision, because flattening is lossy and a hash is
    /// not: two peers whose lists look identical may still hash differently, and the negotiator has
    /// to say so rather than quietly declare a match. Empty when the peer could not read its
    /// settings, or predates this field.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> SyncSettingsFields { get; }

    /// <summary>
    /// False when this peer TRIED to read its core's sync settings and could not.
    ///
    /// The distinction this draws is the whole point. A core with no sync settings and a core whose
    /// settings threw both produced an empty blob, and an empty blob hashes to a constant — so two
    /// peers who each failed to read their own settings produced identical digests and the handshake
    /// congratulated them on matching. The one case where the check was most needed was the one case
    /// it could not fail.
    ///
    /// True for a peer that has no sync settings to read, which is a real answer rather than a
    /// missing one, and true for peers predating this field.
    /// </summary>
    public bool SyncSettingsReadable { get; }

    /// <summary>
    /// Settings that change what lands in main memory without being part of the core's sync settings
    /// — N64's render resolution and plugin, in practice. Advisory: carried so a mismatch can be
    /// NAMED in the log rather than discovered as a desync, but not a refusal, because what counts
    /// as too high is a property of the game and the plugin rather than of the numbers. Empty when
    /// the core exposes nothing of the kind.
    /// </summary>
    public string VideoSettings { get; }

    /// <summary>
    /// Which BizHawk this is — release, commit, branch, dev flag, architecture. See
    /// <see cref="BuildIdentity"/> for why <see cref="CoreVersion"/> could not do this: an assembly
    /// version is the same string for every build of a release, so a fork, a dev build and the
    /// stock download all looked identical to the handshake.
    ///
    /// Empty for a peer predating the field, which compares equal to another such peer and is why
    /// the negotiator treats two empties as "not known" rather than "known to match".
    /// </summary>
    public string BuildId { get; }

    /// <summary>
    /// The firmware BizHawk identified alongside the ROM — a PSX or Saturn BIOS, an NDS bootrom.
    ///
    /// Two players with different BIOS revisions run different code before the game starts and
    /// diverge for reasons nothing in the game explains. BizHawk had this on <c>GameInfo</c> the
    /// whole time and the handshake never asked for it. Empty on the many systems that need none.
    /// </summary>
    public string FirmwareHash { get; }

    /// <summary>
    /// A hash per mounted disc, in the order the core was given them.
    ///
    /// Empty for the many systems with no discs. See <see cref="DiscIdentity"/> for why the order
    /// matters and why the per-disc list travels rather than only a digest: a multi-disc set was
    /// identified from disc one, so two players holding the same disc 1 and different disc 2s
    /// passed every check and diverged on the swap.
    /// </summary>
    public IReadOnlyList<string> DiscHashes { get; }

    public IReadOnlyList<string> PortLayoutDigests { get; }

    /// <summary>
    /// Whether this peer's core qualifies as deterministic — the core's own flag, or a named
    /// exception to it. See <c>DeterminismPolicy</c> for why the flag is not simply passed through.
    /// </summary>
    public bool Deterministic { get; }

    /// <summary>This peer's capability-probe result (§5); rollback needs both peers to qualify.</summary>
    public int MaxRollbackDepth { get; }
}

/// <summary>The sync mode a negotiated session will run in.</summary>
public enum SyncMode
{
    Lockstep,
    Rollback,
}
