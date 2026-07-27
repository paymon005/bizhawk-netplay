using System;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The attempt-token + tracked-resource lifecycle. The properties under test are the races it
    /// exists to close: a canceled worker must not be able to register a socket after teardown's
    /// close-all sweep (it would leak, unclosed, forever), and stale tokens must stay stale.
    /// </summary>
    public class ConnectionLifecycleTests
    {
        private sealed class FakeSocket : IDisposable
        {
            public int Disposals;
            public void Dispose() => Disposals++;
        }

        [Fact]
        public void Tokens_StaleAfterBeginOrInvalidate()
        {
            var lifecycle = new ConnectionLifecycle();
            int first = lifecycle.Begin();
            Assert.True(lifecycle.IsCurrent(first));
            int second = lifecycle.Begin();
            Assert.False(lifecycle.IsCurrent(first));
            Assert.True(lifecycle.IsCurrent(second));
            lifecycle.Invalidate();
            Assert.False(lifecycle.IsCurrent(second));
            Assert.True(lifecycle.IsCurrent(lifecycle.Current));
        }

        [Fact]
        public void Track_RefusesAStaleToken()
        {
            // The worker captured its token, then Disconnect started a new attempt. Whatever the
            // worker accepted must not enter the registry — the caller closes it itself.
            var lifecycle = new ConnectionLifecycle();
            int stale = lifecycle.Begin();
            lifecycle.Begin();
            Assert.False(lifecycle.Track(new FakeSocket(), stale));
            Assert.False(lifecycle.HasTracked);
        }

        [Fact]
        public void Track_RefusesAfterTeardown_UntilANewAttemptReopens()
        {
            var lifecycle = new ConnectionLifecycle();
            int attempt = lifecycle.Begin();
            lifecycle.RejectAndCloseAll();
            // The token may even still be current (teardown may bump separately) — the closed
            // registry alone must refuse: nothing may slip in after the close-all sweep.
            Assert.False(lifecycle.Track(new FakeSocket(), attempt));

            lifecycle.AcceptNew();
            int next = lifecycle.Begin();
            Assert.True(lifecycle.Track(new FakeSocket(), next));
        }

        [Fact]
        public void RejectAndCloseAll_DisposesEverythingTracked_Once()
        {
            var lifecycle = new ConnectionLifecycle();
            int attempt = lifecycle.Begin();
            var a = new FakeSocket();
            var b = new FakeSocket();
            Assert.True(lifecycle.Track(a, attempt));
            Assert.True(lifecycle.Track(b, attempt));

            lifecycle.RejectAndCloseAll();
            Assert.Equal(1, a.Disposals);
            Assert.Equal(1, b.Disposals);
            Assert.False(lifecycle.HasTracked);

            // A second sweep is a no-op, not a double dispose.
            lifecycle.RejectAndCloseAll();
            Assert.Equal(1, a.Disposals);
        }

        [Fact]
        public void UntrackedResource_IsNotClosedByTeardown()
        {
            // Ownership handoff: once the handshake completes, the session link owns the socket —
            // teardown of the *registry* must not close it out from under the live session path.
            var lifecycle = new ConnectionLifecycle();
            int attempt = lifecycle.Begin();
            var handedOff = new FakeSocket();
            lifecycle.Track(handedOff, attempt);
            lifecycle.Untrack(handedOff);

            lifecycle.RejectAndCloseAll();
            Assert.Equal(0, handedOff.Disposals);
        }

        [Fact]
        public void NullResource_IsRefusedAndHarmless()
        {
            var lifecycle = new ConnectionLifecycle();
            Assert.False(lifecycle.Track(null, lifecycle.Begin()));
            lifecycle.Untrack(null);
            Assert.False(lifecycle.HasTracked);
        }
    }
}
