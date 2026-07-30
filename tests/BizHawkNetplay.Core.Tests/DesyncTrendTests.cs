using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Telling emulation drift apart from a mismatch no resync can clear. Getting this wrong in the
/// quiet direction sends a player off to change video settings that were never the problem.
/// </summary>
public class DesyncTrendTests
{
    [Fact]
    public void DesyncsSeparatedByAgreementsAreNeverSystematic()
    {
        var trend = new DesyncTrend();

        // The sequence an ordinary session produces: it syncs, it drifts, the resync works, it
        // syncs again, it drifts again. Nothing here is systematic, however long it runs.
        for (int round = 0; round < 10; round++)
        {
            trend.RecordAgreement();
            Assert.False(trend.RecordDesync());
            Assert.False(trend.IsSystematic);
        }
        // Not merely "under the threshold" — the run never starts at all. A desync with an
        // agreement in front of it is evidence the machines CAN match, so it is drift by
        // definition and contributes nothing to the unbroken count.
        Assert.Equal(0, trend.UnbrokenDesyncs);
    }

    [Fact]
    public void UnbrokenDesyncsAreAnnouncedOnceAndOnlyOnce()
    {
        var trend = new DesyncTrend();

        Assert.False(trend.RecordDesync());          // one desync says nothing yet
        Assert.False(trend.IsSystematic);
        Assert.True(trend.RecordDesync());           // two in a row with nothing agreeing between
        Assert.True(trend.IsSystematic);

        // It stays systematic, but the caller is told exactly once — a message repeated every
        // checksum interval is noise, and the condition has not changed.
        Assert.False(trend.RecordDesync());
        Assert.False(trend.RecordDesync());
        Assert.True(trend.IsSystematic);
    }

    [Fact]
    public void AnAgreementAfterASystematicRunDoesNotUnsayIt()
    {
        var trend = new DesyncTrend();
        trend.RecordDesync();
        Assert.True(trend.RecordDesync());

        // The run is over, but the diagnosis already given was correct at the time and the count
        // is history, not a live gauge. What matters is that it is not announced a second time.
        trend.RecordAgreement();
        Assert.True(trend.IsSystematic);
        Assert.False(trend.RecordDesync());
    }

    [Fact]
    public void ResetClearsBothTheRunAndTheAgreement()
    {
        var trend = new DesyncTrend();
        trend.RecordDesync();
        trend.RecordDesync();
        Assert.True(trend.IsSystematic);

        trend.Reset();
        Assert.False(trend.IsSystematic);
        Assert.Equal(0, trend.UnbrokenDesyncs);
        // And the agreement flag went with it: the first desync of a fresh session must count,
        // otherwise a session could inherit credit for the previous one's clean checksums.
        Assert.False(trend.RecordDesync());
        Assert.Equal(1, trend.UnbrokenDesyncs);
    }
}
