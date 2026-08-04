using System.Collections.Generic;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

public class SessionNegotiatorTests
{
    private static PeerIdentity Id(
        int protocol = 1, string rom = "ROMHASH", string core = "GPGX",
        string coreVer = "2.11.1.0", string sync = "SYNC1",
        bool deterministic = true, int depth = 20, string layout = "L0",
        bool syncReadable = true, string? video = null,
        string? build = null, string? firmware = null)
        => new(protocol, rom, core, coreVer, sync,
            new[] { layout, "L1" }, deterministic, depth,
            syncSettingsReadable: syncReadable, videoSettings: video,
            buildId: build, firmwareHash: firmware);

    private static SessionPreferences Pref(int delay = 2, bool rollback = false, string password = "")
        => new(delay, rollback, password);

    [Fact]
    public void MatchingPeers_AcceptLockstep()
    {
        var r = SessionNegotiator.Negotiate(Id(), Id(), Pref(), Pref());
        Assert.True(r.Accepted);
        Assert.Equal(SyncMode.Lockstep, r.Mode);
    }

    [Fact]
    public void InputDelay_TakesTheLargerAsk()
    {
        var r = SessionNegotiator.Negotiate(Id(), Id(), Pref(delay: 2), Pref(delay: 5));
        Assert.True(r.Accepted);
        Assert.Equal(5, r.InputDelay);
    }

    [Theory]
    [InlineData("ROM")]
    [InlineData("CORE")]
    [InlineData("COREVER")]
    [InlineData("SYNC")]
    [InlineData("PROTO")]
    [InlineData("LAYOUT")]
    public void AnyIdentityMismatch_IsRejected(string which)
    {
        var local = Id();
        PeerIdentity remote = which switch
        {
            "ROM" => Id(rom: "OTHER"),
            "CORE" => Id(core: "NesHawk"),
            "COREVER" => Id(coreVer: "2.10.0.0"),
            "SYNC" => Id(sync: "SYNC2"),
            "PROTO" => Id(protocol: 2),
            "LAYOUT" => Id(layout: "DIFF"),
            _ => Id(),
        };
        var r = SessionNegotiator.Negotiate(local, remote, Pref(), Pref());
        Assert.False(r.Accepted);
        Assert.False(string.IsNullOrEmpty(r.RejectReason));
    }

    [Fact]
    public void LayoutMismatch_NamesTheOffendingPort()
    {
        // First port matches, the SECOND differs — the reason must point at P2 specifically.
        var local = new PeerIdentity(1, "ROMHASH", "GPGX", "2.11.1.0", "SYNC1", new[] { "L0", "L1" }, true, 20);
        var remote = new PeerIdentity(1, "ROMHASH", "GPGX", "2.11.1.0", "SYNC1", new[] { "L0", "DIFF" }, true, 20);
        var r = SessionNegotiator.Negotiate(local, remote, Pref(), Pref());
        Assert.False(r.Accepted);
        Assert.Contains("P2", r.RejectReason);
    }

    [Fact]
    public void ControllerCountMismatch_IsReportedWithCounts()
    {
        var local = new PeerIdentity(1, "ROMHASH", "GPGX", "2.11.1.0", "SYNC1", new[] { "L0", "L1" }, true, 20);
        var remote = new PeerIdentity(1, "ROMHASH", "GPGX", "2.11.1.0", "SYNC1", new[] { "L0" }, true, 20);
        var r = SessionNegotiator.Negotiate(local, remote, Pref(), Pref());
        Assert.False(r.Accepted);
        Assert.Contains("count differs", r.RejectReason);
    }

    [Fact]
    public void Password_IsNotTheNegotiatorsConcern()
    {
        // Password verification moved to the handshake's nonce challenge-response (SessionAuth); the
        // stateless negotiator no longer sees or compares it, so differing passwords don't reject here.
        Assert.True(SessionNegotiator.Negotiate(Id(), Id(), Pref(password: "hunter2"), Pref(password: "letmein")).Accepted);
    }

    [Fact]
    public void NonDeterministicEitherSide_IsRejected()
    {
        Assert.False(SessionNegotiator.Negotiate(Id(deterministic: false), Id(), Pref(), Pref()).Accepted);
        Assert.False(SessionNegotiator.Negotiate(Id(), Id(deterministic: false), Pref(), Pref()).Accepted);
    }

    [Fact]
    public void Rollback_OnlyWhenBothOptInAndBothQualify()
    {
        // Both want it, both deep enough -> rollback.
        var r1 = SessionNegotiator.Negotiate(Id(depth: 20), Id(depth: 20),
            Pref(rollback: true), Pref(rollback: true));
        Assert.Equal(SyncMode.Rollback, r1.Mode);

        // Both want it, but the worst peer is too shallow -> lockstep. Written against the
        // threshold rather than a literal, so moving it can't silently reclassify this case.
        var r2 = SessionNegotiator.Negotiate(
            Id(depth: 20), Id(depth: ProbeResult.RollbackDepthThreshold - 1),
            Pref(rollback: true), Pref(rollback: true));
        Assert.Equal(SyncMode.Lockstep, r2.Mode);

        // ...and the peer exactly at the threshold is on the qualifying side of it.
        var r2b = SessionNegotiator.Negotiate(
            Id(depth: 20), Id(depth: ProbeResult.RollbackDepthThreshold),
            Pref(rollback: true), Pref(rollback: true));
        Assert.Equal(SyncMode.Rollback, r2b.Mode);

        // One peer didn't opt in -> lockstep even though both qualify.
        var r3 = SessionNegotiator.Negotiate(Id(depth: 20), Id(depth: 20),
            Pref(rollback: true), Pref(rollback: false));
        Assert.Equal(SyncMode.Lockstep, r3.Mode);
    }

    private static PeerIdentity IdWithFields(string sync, params string[] pairs)
    {
        var fields = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < pairs.Length; i += 2)
            fields.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
        return new PeerIdentity(1, "ROMHASH", "GPGX", "2.11.1.0", sync,
            new[] { "L0", "L1" }, true, 20, fields);
    }

    [Fact]
    public void ASyncSettingsMismatchNamesTheSettingsAndBothSidesValues()
    {
        var mine = IdWithFields("SYNC1", "VideoPlugin", "GLideN64", "RspPlugin", "HLE", "Region", "NTSC");
        var theirs = IdWithFields("SYNC2", "VideoPlugin", "Rice", "RspPlugin", "HLE", "Region", "PAL");

        var r = SessionNegotiator.Negotiate(mine, theirs, Pref(), Pref());

        Assert.False(r.Accepted);
        Assert.Contains("VideoPlugin (yours GLideN64, theirs Rice)", r.RejectReason);
        Assert.Contains("Region (yours NTSC, theirs PAL)", r.RejectReason);
        Assert.DoesNotContain("RspPlugin", r.RejectReason); // matched — naming it would be noise

        // Symmetric: the other peer runs the same comparison and reads its own half.
        var flipped = SessionNegotiator.Negotiate(theirs, mine, Pref(), Pref());
        Assert.Contains("VideoPlugin (yours Rice, theirs GLideN64)", flipped.RejectReason);
    }

    /// <summary>
    /// Both peers run the same core build, so their settings have the same shape — except where
    /// flattening indexes a collection, which is how one side ends up with a key the other lacks
    /// (`Controllers[2]` exists for one of them). Named from whichever side is looking.
    /// </summary>
    [Fact]
    public void ASettingOnlyOneSideHasIsNamedFromEitherDirection()
    {
        var mine = IdWithFields("SYNC1",
            "Region", "NTSC", "Controllers[0]", "Standard", "Controllers[1]", "Mempak");
        var theirs = IdWithFields("SYNC2", "Region", "NTSC", "Controllers[0]", "Standard");

        Assert.Contains("Controllers[1] (yours Mempak, theirs absent)",
            SessionNegotiator.Negotiate(mine, theirs, Pref(), Pref()).RejectReason);
        Assert.Contains("Controllers[1] (yours absent, theirs Mempak)",
            SessionNegotiator.Negotiate(theirs, mine, Pref(), Pref()).RejectReason);
    }

    /// <summary>A peer that sent no fields at all is indistinguishable from one whose core exposes
    /// none, so neither gets a named answer — the fallback covers both without guessing.</summary>
    [Fact]
    public void OneSideWithNoFieldsAtAllGetsTheFallbackRatherThanAOneSidedList()
    {
        var r = SessionNegotiator.Negotiate(
            IdWithFields("SYNC1", "Region", "NTSC"), IdWithFields("SYNC2"), Pref(), Pref());

        Assert.Equal("core sync-settings mismatch — align sync settings on both ends", r.RejectReason);
    }

    /// <summary>
    /// The digest decides; the field list only explains. Flattening is lossy, so it can fail to
    /// account for a difference the hash saw — and "no difference found" from a comparison that
    /// could not see one must not read like a genuine match.
    /// </summary>
    [Fact]
    public void AMismatchTheFieldsCannotExplainSaysSoRatherThanClaimingTheyMatch()
    {
        var mine = IdWithFields("SYNC1", "VideoPlugin", "GLideN64");
        var theirs = IdWithFields("SYNC2", "VideoPlugin", "GLideN64");

        var r = SessionNegotiator.Negotiate(mine, theirs, Pref(), Pref());

        Assert.False(r.Accepted);
        Assert.Contains("cannot see", r.RejectReason);
    }

    [Fact]
    public void APeerThatSentNoFieldsFallsBackToTheOldAdvice()
    {
        var r = SessionNegotiator.Negotiate(Id(sync: "SYNC1"), Id(sync: "SYNC2"), Pref(), Pref());

        Assert.False(r.Accepted);
        Assert.Equal("core sync-settings mismatch — align sync settings on both ends", r.RejectReason);
    }

    [Fact]
    public void ADozenDifferencesAreSummarisedRatherThanListedInFull()
    {
        var minePairs = new List<string>();
        var theirPairs = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            minePairs.Add($"Setting{i:00}"); minePairs.Add("a");
            theirPairs.Add($"Setting{i:00}"); theirPairs.Add("b");
        }
        var r = SessionNegotiator.Negotiate(
            IdWithFields("SYNC1", minePairs.ToArray()),
            IdWithFields("SYNC2", theirPairs.ToArray()), Pref(), Pref());

        Assert.Contains("Setting00 (yours a, theirs b)", r.RejectReason);
        Assert.Contains("and 6 more", r.RejectReason);
        Assert.DoesNotContain("Setting11", r.RejectReason);
    }

    // ---------------------------------------------------------------- KI-19

    [Fact]
    public void AnUnreadableSyncSettingsBlobRefusesInsteadOfMatchingItself()
    {
        // The failure this exists for: both peers failed to read their settings, both fell back to
        // an empty blob, and an empty blob hashes to a constant — so the digests MATCHED and the
        // comparison passed at precisely the moment it had nothing to compare.
        var r = SessionNegotiator.Negotiate(
            Id(syncReadable: false), Id(syncReadable: false), Pref(), Pref());

        Assert.False(r.Accepted);
        Assert.Contains("could not be read", r.RejectReason);
    }

    [Fact]
    public void EitherSideFailingToReadItsSettingsIsEnoughToRefuse()
    {
        var mine = SessionNegotiator.Negotiate(Id(syncReadable: false), Id(), Pref(), Pref());
        Assert.False(mine.Accepted);
        Assert.Contains("this core", mine.RejectReason);

        var theirs = SessionNegotiator.Negotiate(Id(), Id(syncReadable: false), Pref(), Pref());
        Assert.False(theirs.Accepted);
        Assert.Contains("other player", theirs.RejectReason);
    }

    [Fact]
    public void ACoreWithNoSyncSettingsAtAllStillPasses()
    {
        // "Nothing to read" is an answer; only "tried and failed" refuses. Otherwise every core
        // without sync settings would become unplayable.
        var r = SessionNegotiator.Negotiate(
            Id(sync: "EMPTY"), Id(sync: "EMPTY"), Pref(), Pref());
        Assert.True(r.Accepted);
    }

    [Fact]
    public void DifferingVideoSettingsWarnWithoutRefusing()
    {
        var r = SessionNegotiator.Negotiate(
            Id(video: "800x600, plugin Rice"), Id(video: "320x240, plugin Rice"), Pref(), Pref());

        Assert.True(r.Accepted);          // not part of any core's sync settings; not ours to forbid
        Assert.NotNull(r.Warning);
        Assert.Contains("800x600", r.Warning);
        Assert.Contains("320x240", r.Warning);
    }

    [Fact]
    public void MatchingOrAbsentVideoSettingsSayNothing()
    {
        Assert.Null(SessionNegotiator.Negotiate(
            Id(video: "320x240"), Id(video: "320x240"), Pref(), Pref()).Warning);
        // A core exposing none, or a peer predating the field, must not produce a warning about a
        // difference nobody can see.
        Assert.Null(SessionNegotiator.Negotiate(Id(video: "320x240"), Id(), Pref(), Pref()).Warning);
        Assert.Null(SessionNegotiator.Negotiate(Id(), Id(), Pref(), Pref()).Warning);
    }

    // ---------------------------------------------------------------- KI-16 / KI-17

    [Fact]
    public void TwoBuildsOfTheSameReleaseFromDifferentCommitsAreRefused()
    {
        // Both report CoreVersion "2.11.1.0", so the check above them passes. This is the one that
        // notices, and the message has to say why the version matching was not enough.
        var stock = BuildIdentity.Format("2.11.1", "bdddf4a58aa1", "release", false, null, true);
        var fork = BuildIdentity.Format("2.11.1", "0123456789ab", "fork", true, null, true);

        var r = SessionNegotiator.Negotiate(Id(build: stock), Id(build: fork), Pref(), Pref());
        Assert.False(r.Accepted);
        Assert.Contains("different commits", r.RejectReason);
    }

    [Fact]
    public void APeerThatCannotNameItsBuildIsNotRefusedForIt()
    {
        // Absence is a weaker guarantee, not a mismatch — otherwise every peer on an older build,
        // and every unusual build with no commit hash, becomes unplayable.
        var known = BuildIdentity.Format("2.11.1", "bdddf4a58aa1", "release", false, null, true);
        Assert.True(SessionNegotiator.Negotiate(Id(build: known), Id(), Pref(), Pref()).Accepted);
        Assert.True(SessionNegotiator.Negotiate(Id(), Id(build: known), Pref(), Pref()).Accepted);
        Assert.True(SessionNegotiator.Negotiate(Id(), Id(), Pref(), Pref()).Accepted);
    }

    [Fact]
    public void DifferentFirmwareIsRefusedAndNamed()
    {
        var r = SessionNegotiator.Negotiate(
            Id(firmware: "AAAAAAAAAAAAAAAAAAAA"), Id(firmware: "BBBBBBBBBBBBBBBBBBBB"),
            Pref(), Pref());

        Assert.False(r.Accepted);
        Assert.Contains("firmware mismatch", r.RejectReason);
        Assert.Contains("AAAAAAAAAAAA", r.RejectReason);
    }

    [Fact]
    public void OnePeerHavingFirmwareAndTheOtherNotReadsAsThat()
    {
        var r = SessionNegotiator.Negotiate(Id(firmware: "AAAAAAAAAAAAAAAAAAAA"), Id(), Pref(), Pref());
        Assert.False(r.Accepted);
        Assert.Contains("none", r.RejectReason);   // not two similar-looking hex strings
    }

    [Fact]
    public void SystemsThatBootNoFirmwareStillMatch()
    {
        Assert.True(SessionNegotiator.Negotiate(Id(), Id(), Pref(), Pref()).Accepted);
    }

    [Fact]
    public void TheIdentityFieldsSurviveTheHandshakeRoundTrip()
    {
        // Both new fields cross the wire, and absence decodes as "readable" so a peer predating
        // them is not refused for never having said so.
        var id = Id(syncReadable: false, video: "800x600, plugin Rice (InN64Resolution=False)",
            build: BuildIdentity.Format("2.11.1", "bdddf4a58aa1", "release", false, null, true),
            firmware: "0123456789ABCDEF0123");
        var encoded = HandshakeCodec.Encode(id, Pref(), 47800, null);
        var (decoded, _, _, _, _) = HandshakeCodec.Decode(encoded);

        Assert.False(decoded.SyncSettingsReadable);
        Assert.Equal(id.VideoSettings, decoded.VideoSettings);
        Assert.Equal(id.BuildId, decoded.BuildId);
        Assert.Equal(id.FirmwareHash, decoded.FirmwareHash);

        var plain = HandshakeCodec.Encode(Id(), Pref(), 47800, null);
        var (decodedPlain, _, _, _, _) = HandshakeCodec.Decode(plain);
        Assert.True(decodedPlain.SyncSettingsReadable);
        Assert.Equal("", decodedPlain.VideoSettings);
        Assert.Equal("", decodedPlain.BuildId);
        Assert.Equal("", decodedPlain.FirmwareHash);
    }
}
