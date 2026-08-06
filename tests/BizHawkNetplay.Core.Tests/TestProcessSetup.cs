using System;
using System.Threading;
using BizHawkNetplay.Core.Session;
using Xunit;

// Block-scoped namespaces rather than the file-scoped form used elsewhere: this file declares two
// namespaces, which the file-scoped form cannot do.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// <summary>.NET Framework 4.8 has no ModuleInitializerAttribute — it arrived in .NET 5. The C#
    /// compiler is happy with a user-defined one, which is the standard way to use the feature when
    /// multi-targeting down to net48.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// Process-wide settings applied before any test runs.
    ///
    /// <b>The KDF cost, which is the one that mattered.</b>
    /// <c>TwoPlayerHandshake_WaitsForPostApplyCallbackBeforeReady</c> failed about one net48
    /// full-suite run in three while passing 4/4 in isolation, and passing always on net10.0. It
    /// was blamed on ThreadPool injection and a floor of 64 was raised to fix it; the floor took
    /// effect and the flake continued, which should have settled that. It did not, because nobody
    /// measured.
    ///
    /// Instrumenting the failure said: both handshake tasks <c>Running</c>, neither faulted, and
    /// <b>32,756 free pool threads</b>. Not starvation, conclusively. Timing the work said the
    /// rest: one <see cref="SessionAuth.ProofPair"/> is 100,000 PBKDF2 iterations, which measures
    /// <b>1,043ms on net48 and 108ms on net10.0</b> — a 9.7× framework gap, and exactly why only
    /// one target ever failed. A handshake derives on both sides, so reaching the client's callback
    /// costs ~2.1s uncontended against a 5s budget. Add seven other collections competing for eight
    /// cores and 2.1s becomes 5.
    ///
    /// So the test was racing its own cryptography. The iteration count is a cost parameter and
    /// nothing about correctness depends on its value, so it is turned down here and
    /// <see cref="TestProcessSetupTests.TheShippingKdfCostIsUnchanged"/> pins the shipping figure.
    /// That also gives the net48 suite back about ninety seconds of pure CPU.
    ///
    /// <b>The pool floor</b> stays, on its own merits rather than as a fix for the above: this
    /// suite blocks about forty-six <c>Task.Run</c> bodies on events with timeouts, and a pool that
    /// injects one thread per 500ms past its floor is a scheduler deciding whether a test passes.
    /// It was never the cause of the flake it was written for, and the comment now says so.
    /// </summary>
    internal static class TestProcessSetup
    {
        /// <summary>Comfortably above the worst concurrent blocked-thread count: xUnit runs about
        /// one collection per core, each blocking two or three threads.</summary>
        internal const int ThreadFloor = 64;

        /// <summary>
        /// PBKDF2 iterations for the suite. Low enough to be free (~10ms on net48, where the
        /// shipping count is over a second), high enough that the loop is still a loop rather than
        /// a single pass — a value of 1 would leave the iteration path itself unexercised.
        /// </summary>
        internal const int TestKdfIterations = 1_000;

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Apply()
        {
            ThreadPool.GetMinThreads(out int worker, out int completionPort);
            // Only ever raise. Never lower whatever the runtime chose for this machine.
            ThreadPool.SetMinThreads(Math.Max(worker, ThreadFloor), Math.Max(completionPort, ThreadFloor));
            SessionAuth.Iterations = TestKdfIterations;
        }
    }

    /// <summary>Guards the setup against becoming a silent no-op. A module initializer that stops
    /// running — the net48 attribute polyfill dropped, the feature turned off — would let the flake
    /// back in with nothing pointing at why, because everything still compiles and still passes.
    /// This fails instead. Top-level and public on purpose: xUnit does not discover a public class
    /// nested inside an internal one.</summary>
    public sealed class TestProcessSetupTests
    {
        [Fact]
        public void TheModuleInitializerActuallyRan()
        {
            ThreadPool.GetMinThreads(out int worker, out int completionPort);
            Assert.True(worker >= TestProcessSetup.ThreadFloor,
                $"min worker threads {worker} < {TestProcessSetup.ThreadFloor}");
            Assert.True(completionPort >= TestProcessSetup.ThreadFloor,
                $"min IOCP threads {completionPort} < {TestProcessSetup.ThreadFloor}");
            Assert.Equal(TestProcessSetup.TestKdfIterations, SessionAuth.Iterations);
        }

        /// <summary>
        /// The shipping stretch is unchanged by the fact that tests turn it down.
        ///
        /// This is the whole reason the override is safe. If someone ever "fixes" a slow suite by
        /// lowering the constant instead of the override, every session ever played would get a
        /// cheaper password — silently, and with the tests still green.
        /// </summary>
        [Fact]
        public void TheShippingKdfCostIsUnchanged()
        {
            Assert.Equal(100_000, SessionAuth.DefaultIterations);
        }

        /// <summary>
        /// The proofs still verify at the shipping iteration count, not merely at the cheap one the
        /// rest of the suite runs.
        ///
        /// Turning a cost parameter down for speed is only safe while something still exercises the
        /// real value; otherwise the suite proves the protocol works in a configuration nobody
        /// ships. This costs about two seconds on net48 and is the only test that pays it.
        ///
        /// The cost is PASSED rather than switched on. The first version of this test set the
        /// process-wide knob, did its work and put it back — and immediately broke
        /// <c>SessionAuthTests.ProofPair_MatchesProofComputedSeparately</c>, which was deriving in
        /// a parallel collection and got one proof at each cost. A test that reaches for shared
        /// mutable state to describe a local condition is the bug it is testing for.
        /// </summary>
        [Fact]
        public void ProofsVerifyAtTheShippingIterationCount()
        {
            int cost = SessionAuth.DefaultIterations;
            var hostNonce = SessionAuth.NewNonce();
            var joinNonce = SessionAuth.NewNonce();

            var host = SessionAuth.ProofPairWithKey(
                cost, "correct horse", SessionAuth.RoleHost, SessionAuth.RoleJoin, hostNonce, joinNonce);
            var join = SessionAuth.ProofPairWithKey(
                cost, "correct horse", SessionAuth.RoleJoin, SessionAuth.RoleHost, hostNonce, joinNonce);

            // Each side computes what it will send and what it expects to receive; they cross.
            Assert.Equal(host.mine, join.peers);
            Assert.Equal(join.mine, host.peers);
            Assert.NotEqual(host.mine, join.mine);   // the role tag is what stops a reflection
            Assert.Equal(host.key, join.key);        // ...and both ends land on one session key

            var wrong = SessionAuth.ProofPairWithKey(
                cost, "wrong horse", SessionAuth.RoleJoin, SessionAuth.RoleHost, hostNonce, joinNonce);
            Assert.NotEqual(join.mine, wrong.mine);
            Assert.NotEqual(join.key, wrong.key);
        }

        /// <summary>
        /// The knob refuses to be turned twice.
        ///
        /// Not defensive tidiness: the failure it prevents is a peer rejecting a proof neither side
        /// computed wrongly, which reads as a wrong password and is nearly impossible to diagnose
        /// from a log.
        /// </summary>
        [Fact]
        public void TheKdfCostCannotBeChangedAfterStartUp()
        {
            Assert.Throws<InvalidOperationException>(() => SessionAuth.Iterations = 42);
            Assert.Equal(TestProcessSetup.TestKdfIterations, SessionAuth.Iterations);
        }
    }
}
