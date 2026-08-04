using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-16: the handshake compared an assembly version, which is the same string for every build of
/// a release. A fork, a developer build and the stock download all looked identical to it.
/// </summary>
public class BuildIdentityTests
{
    private const string Stock = "2.11.1";
    private const string Commit = "bdddf4a58aa1a022afb11dc73294a81a5aa7bbd5";

    [Fact]
    public void TheSameBuildProducesTheSameLine()
    {
        var a = BuildIdentity.Format(Stock, Commit, "release", false, null, true);
        var b = BuildIdentity.Format(Stock, Commit, "release", false, "", true);
        Assert.Equal(a, b);
        Assert.Null(BuildIdentity.Mismatch(a, b));
    }

    [Fact]
    public void SameReleaseDifferentCommitIsCaughtAndExplained()
    {
        // The case the old check could not see at all, and the one worth spelling out: both players
        // believe they are on "2.11.1", because they are.
        var stock = BuildIdentity.Format(Stock, Commit, "release", false, null, true);
        var fork = BuildIdentity.Format(Stock, "0123456789abcdef0123", "my-fork", true, null, true);

        var why = BuildIdentity.Mismatch(stock, fork);
        Assert.NotNull(why);
        Assert.Contains("same BizHawk release", why!);
        Assert.Contains("different commits", why!);
    }

    [Fact]
    public void ADifferentReleaseSaysSoRatherThanBlamingTheCommit()
    {
        var why = BuildIdentity.Mismatch(
            BuildIdentity.Format("2.11.1", Commit, "release", false, null, true),
            BuildIdentity.Format("2.10.0", "aaaaaaaaaaaa", "release", false, null, true));

        Assert.Contains("different BizHawk releases", why!);
        Assert.DoesNotContain("different commits", why!);
    }

    [Fact]
    public void ArchitectureOutranksEverythingElseInTheExplanation()
    {
        // x86 and x64 of the identical commit are different programs, and it is the most confusing
        // mismatch to hit — everything a player can see about their build says it matches.
        var why = BuildIdentity.Mismatch(
            BuildIdentity.Format(Stock, Commit, "release", false, null, true),
            BuildIdentity.Format(Stock, Commit, "release", false, null, false));

        Assert.Contains("32-bit", why!);
        Assert.Contains("64-bit", why!);
    }

    [Fact]
    public void ACustomBuildStringDistinguishesTwoOtherwiseIdenticalBuilds()
    {
        var plain = BuildIdentity.Format(Stock, Commit, "release", false, null, true);
        var custom = BuildIdentity.Format(Stock, Commit, "release", false, "SomeoneElsesHawk", true);
        Assert.NotEqual(plain, custom);
        Assert.NotNull(BuildIdentity.Mismatch(plain, custom));
    }

    [Fact]
    public void AHostileCustomBuildStringCannotBreakTheLine()
    {
        // It is the first line of a file anyone can drop in their BizHawk folder, so it arrives as
        // arbitrary text. A separator or a newline inside it would let one field pretend to be
        // several, which is how a peer could forge a matching prefix.
        var nasty = BuildIdentity.Format(Stock, Commit, "release", false,
            "a|b\nc\td " + new string('x', 500), true);

        Assert.DoesNotContain("\n", nasty);
        Assert.DoesNotContain("\t", nasty);
        Assert.Equal(6, nasty.Split('|').Length);   // version, hash, branch, dev, arch, custom
        Assert.True(nasty.Length < 200);
    }

    [Fact]
    public void MissingFactsDegradeToAStableLineRatherThanAMismatch()
    {
        // A build that cannot report a commit hash is a weaker guarantee, not a refusal — two such
        // peers still match each other.
        var a = BuildIdentity.Format(Stock, null, null, false, null, true);
        var b = BuildIdentity.Format(Stock, null, null, false, null, true);
        Assert.Equal(a, b);
        Assert.Null(BuildIdentity.Mismatch(a, b));
        Assert.Contains("?", a);
    }

    [Fact]
    public void TheShortHashIsWhatTravels()
    {
        // Enough to distinguish builds; the full forty characters would be repeated in every log
        // line that quotes the identity.
        var line = BuildIdentity.Format(Stock, Commit, "release", false, null, true);
        Assert.Contains(Commit.Substring(0, 9), line);
        Assert.DoesNotContain(Commit, line);
    }
}
