using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// How many recoveries a session gets, and every way the allowance comes back.
///
/// The counter was an int on the tool form, spent in two places under two different rules and
/// cleared in four more. The gate that decided whether an attempt could start lived somewhere else
/// again, taking the counter as an argument — so whether a debounced trigger COST an attempt
/// depended on which caller you read. Six sites, one rule.
/// </summary>
public class ResyncBudgetTests
{
    // ---- the gate (moved here from RecoveryPolicy, which did not own the counter) ----

    [Fact]
    public void ARebuildInFlightWinsOverEverything()
    {
        var budget = new ResyncBudget(3);
        Assert.Equal(ResyncGate.AlreadyInProgress, budget.Gate(true, 0.0, 5.0));
    }

    [Fact]
    public void RepeatTriggersForTheSameDesyncAreDebounced()
    {
        var budget = new ResyncBudget(3);
        Assert.Equal(ResyncGate.Debounced, budget.Gate(false, 4.9, 5.0));
        Assert.Equal(ResyncGate.Start, budget.Gate(false, 5.0, 5.0));
    }

    [Fact]
    public void GivingUpHappensBeyondTheCapRatherThanAtIt()
    {
        var budget = new ResyncBudget(3);
        Assert.Equal(ResyncGate.Start, budget.Gate(false, 60.0, 5.0));   // #1
        Assert.Equal(ResyncGate.Start, budget.Gate(false, 60.0, 5.0));   // #2
        Assert.Equal(ResyncGate.Start, budget.Gate(false, 60.0, 5.0));   // #3 — the cap itself
        Assert.Equal(ResyncGate.GiveUp, budget.Gate(false, 60.0, 5.0));
    }

    /// <summary>
    /// A refused trigger costs nothing.
    ///
    /// This is the half that was ambiguous while the gate and the counter had different owners: the
    /// gate answered "no" and the caller incremented anyway. Charged, three genuine desyncs could
    /// exhaust a six-attempt budget — every one of them arriving twice, once during the rebuild it
    /// triggered and once inside the grace window after it.
    /// </summary>
    [Fact]
    public void ARefusedTriggerDoesNotSpendAnAttempt()
    {
        var budget = new ResyncBudget(3);
        for (int i = 0; i < 20; i++)
        {
            budget.Gate(true, 60.0, 5.0);    // a rebuild is in flight
            budget.Gate(false, 0.0, 5.0);    // and this one is inside the grace window
        }
        Assert.Equal(0, budget.Used);
        Assert.Equal(ResyncGate.Start, budget.Gate(false, 60.0, 5.0));
        Assert.Equal(1, budget.Used);
    }

    [Fact]
    public void TheAttemptNumberIsTheOneToPrint()
    {
        // "resync #N" quotes Used, so Start must have already charged the attempt it authorises.
        var budget = new ResyncBudget(6);
        budget.Gate(false, 60.0, 5.0);
        Assert.Equal(1, budget.Used);
        budget.Gate(false, 60.0, 5.0);
        Assert.Equal(2, budget.Used);
    }

    // ---- spending ----

    [Fact]
    public void TheJoinersSpendAgreesWithTheHostsGate()
    {
        // The two used to differ: the host checked `count + 1 > max` before incrementing, the
        // joiner `++count > max`. Same arithmetic, but only by coincidence — one call each.
        var host = new ResyncBudget(3);
        var joiner = new ResyncBudget(3);
        for (int i = 0; i < 5; i++)
        {
            bool hostOk = host.Gate(false, 60.0, 5.0) == ResyncGate.Start;
            bool joinerOk = joiner.TrySpend();
            Assert.Equal(hostOk, joinerOk);
            Assert.Equal(host.Used, joiner.Used);
        }
    }

    /// <summary>
    /// A deliberate rebuild costs nothing at all.
    ///
    /// Mechanically a settings change is the same operation as a desync recovery, which is the
    /// trap: charged against this budget, six input-delay tweaks would end a session that never
    /// desynced once. The budget exists to catch a determinism bug; a player moving a slider is not
    /// evidence of one.
    /// </summary>
    [Fact]
    public void ADeliberateRebuildIsFree()
    {
        var budget = new ResyncBudget(3);
        for (int i = 0; i < 50; i++) budget.Excuse();
        Assert.Equal(0, budget.Used);
        Assert.True(budget.TrySpend());
    }

    // ---- getting the allowance back ----

    [Fact]
    public void AgreementClearsTheCounterAndSaysItDidSo()
    {
        var budget = new ResyncBudget(3);
        Assert.False(budget.RecordAgreement());   // nothing to clear: no line worth printing
        budget.TrySpend();
        Assert.True(budget.RecordAgreement());
        Assert.Equal(0, budget.Used);
        Assert.False(budget.RecordAgreement());   // and only the first one is news
    }

    [Fact]
    public void QuietClearsTheCounterOnlyAfterTheRecoveryWindow()
    {
        var budget = new ResyncBudget(3);
        budget.TrySpend();
        Assert.False(budget.RecordQuiet(7.9, 8.0));
        Assert.Equal(1, budget.Used);
        Assert.True(budget.RecordQuiet(8.1, 8.0));
        Assert.Equal(0, budget.Used);
        Assert.False(budget.RecordQuiet(99.0, 8.0));   // nothing left to clear
    }

    /// <summary>
    /// A run of successful recoveries never exhausts the budget.
    ///
    /// This is what both recovery signals exist for. Without them a healthy session that recovers
    /// from six transient hiccups over an hour would be killed for "persistent desync" on the
    /// seventh — the failure the give-up limit is supposed to prevent, inflicted on a session that
    /// was working.
    /// </summary>
    [Fact]
    public void SuccessfulRecoveriesNeverAccumulateIntoAGiveUp()
    {
        var host = new ResyncBudget(3);
        var joiner = new ResyncBudget(3);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(ResyncGate.Start, host.Gate(false, 60.0, 5.0));
            host.RecordAgreement();                    // checksums re-agree
            Assert.True(joiner.TrySpend());
            joiner.RecordQuiet(60.0);                  // and a joiner runs quietly past the window
        }
    }

    [Fact]
    public void ResetIsWhatAFreshSessionAndARejoinBothGet()
    {
        var budget = new ResyncBudget(3);
        budget.TrySpend();
        budget.TrySpend();
        budget.Reset();
        Assert.Equal(0, budget.Used);
        Assert.Equal(ResyncGate.Start, budget.Gate(false, 60.0, 5.0));
    }

    [Fact]
    public void AnAllowanceBelowOneIsStillOneAttempt()
    {
        // A misconfigured cap must not produce a session that gives up before trying at all.
        var budget = new ResyncBudget(0);
        Assert.True(budget.MaxAttempts >= 1);
        Assert.Equal(ResyncGate.Start, budget.Gate(false, 60.0, 5.0));
    }
}
