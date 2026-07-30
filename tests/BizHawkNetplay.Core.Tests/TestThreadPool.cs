using System;
using System.Threading;
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
    /// Raises the ThreadPool's floor before any test runs.
    ///
    /// This suite tests a network stack, so a lot of it is deliberately blocking: ~36 places start a
    /// handshake or a stream peer with <c>Task.Run</c> and then wait on an event with a timeout,
    /// which is what the 58 xUnit1031 warnings are pointing at. xUnit runs test classes in parallel
    /// by default, so several of those are in flight at once, each pinning two or three pool threads
    /// that are asleep rather than working.
    ///
    /// On .NET Framework the pool starts at one worker per processor and then injects roughly one
    /// more every 500ms. Past the floor a freshly queued work item waits on that heuristic —
    /// seconds, not milliseconds — so a test whose callback must fire within five seconds is racing
    /// thread injection rather than testing the handshake.
    ///
    /// What is actually known, rather than assumed:
    /// TwoPlayerHandshake_WaitsForPostApplyCallbackBeforeReady failed once on CI (run 30585077449)
    /// at <c>callbackEntered.Wait(5s)</c> — a <c>Task.Run</c> body had not begun five seconds after
    /// being queued, on net48 only, while net10.0 passed 403/403 in the same run, and the same
    /// commit passed on re-run. It has never been reproduced locally, including with the pool
    /// deliberately starved, because this machine is fast and uncontended. So: the signature matches
    /// thread injection and nothing else explains a queued item failing to start when the pool is
    /// merely blocked, but this is a fix for the one mechanism that fits, not a verified repro.
    ///
    /// A floor is not an allocation — threads are still only created on demand. Doing it once here
    /// beats rewriting 36 call sites. Converting those to async is still the real fix; this stops
    /// the scheduler from deciding whether CI is green in the meantime.
    /// </summary>
    internal static class TestThreadPool
    {
        /// <summary>Comfortably above the worst concurrent blocked-thread count: xUnit runs about
        /// one collection per core, each blocking two or three threads.</summary>
        internal const int Floor = 64;

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void RaiseFloor()
        {
            ThreadPool.GetMinThreads(out int worker, out int completionPort);
            // Only ever raise. Never lower whatever the runtime chose for this machine.
            ThreadPool.SetMinThreads(Math.Max(worker, Floor), Math.Max(completionPort, Floor));
        }
    }

    /// <summary>Guards the fix against becoming a silent no-op. A module initializer that stops
    /// running — the net48 attribute polyfill dropped, the feature turned off — would let the flake
    /// back in with nothing pointing at why, because everything still compiles and still passes.
    /// This fails instead. Top-level and public on purpose: xUnit does not discover a public class
    /// nested inside an internal one.</summary>
    public sealed class TestThreadPoolTests
    {
        [Fact]
        public void TheModuleInitializerActuallyRan()
        {
            ThreadPool.GetMinThreads(out int worker, out int completionPort);
            Assert.True(worker >= TestThreadPool.Floor,
                $"min worker threads {worker} < {TestThreadPool.Floor}");
            Assert.True(completionPort >= TestThreadPool.Floor,
                $"min IOCP threads {completionPort} < {TestThreadPool.Floor}");
        }
    }
}
