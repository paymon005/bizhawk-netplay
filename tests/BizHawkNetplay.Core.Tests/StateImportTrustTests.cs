using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-13: a joiner loads its host's savestate, and a savestate is a trusted-input format all the
/// way into the cores. The parser cannot be made safe from here; what can be done is to stop the
/// trust being invisible, and to be accurate about what the control-frame MAC did and did not fix.
/// </summary>
public class StateImportTrustTests
{
    [Fact]
    public void AHostIsToldNothingBecauseAHostImportsNothing()
    {
        // Every state-bearing path is joiner-side. A warning here would be false, and a false
        // warning is worse than none — it teaches people to skip the real one.
        Assert.Null(StateImportTrust.Advisory(isHost: true, hasPassword: true));
        Assert.Null(StateImportTrust.Advisory(isHost: true, hasPassword: false));
    }

    [Fact]
    public void WithAPasswordTheExposureIsNarrowedToTheHostAndSaysSo()
    {
        var advisory = StateImportTrust.Advisory(isHost: false, hasPassword: true);
        Assert.NotNull(advisory);
        Assert.Contains("only the host can send you one", advisory!);
        Assert.DoesNotContain("NO password", advisory!);
    }

    [Fact]
    public void WithoutAPasswordItNamesTheRemedyRatherThanJustTheRisk()
    {
        var advisory = StateImportTrust.Advisory(isHost: false, hasPassword: false);
        Assert.NotNull(advisory);
        Assert.Contains("NO password", advisory!);
        Assert.Contains("Set a password on both", advisory!);
    }

    [Fact]
    public void BothVersionsSayWhatASavestateActuallyIs()
    {
        // The point of the line is that "loads a savestate" sounds harmless. It has to say what the
        // format can set, or nobody reading it learns anything they did not already assume.
        foreach (bool password in new[] { true, false })
        {
            var advisory = StateImportTrust.Advisory(isHost: false, hasPassword: password)!;
            Assert.Contains("page permissions", advisory);
            Assert.Contains("stack pointer", advisory);
        }
    }
}
