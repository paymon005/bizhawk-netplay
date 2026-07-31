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
        bool deterministic = true, int depth = 20, string layout = "L0")
        => new(protocol, rom, core, coreVer, sync,
            new[] { layout, "L1" }, deterministic, depth);

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
}
