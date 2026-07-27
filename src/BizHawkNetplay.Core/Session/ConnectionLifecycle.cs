using System;
using System.Collections.Generic;
using System.Threading;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// The connection-attempt token plus the tracked-resource registry that make cancellation safe,
    /// extracted from the tool form so the races they close are unit-testable.
    ///
    /// Two cooperating mechanisms:
    ///
    /// <b>Attempt tokens.</b> Every background worker (connect thread, accept loop, punch worker,
    /// reader/writer callback) captures <see cref="Current"/> when it starts and re-checks
    /// <see cref="IsCurrent"/> before every effect. <see cref="Begin"/>/<see cref="Invalidate"/>
    /// bump the counter, instantly orphaning every outstanding worker — a canceled or replaced
    /// worker can observe the world but no longer change it.
    ///
    /// <b>Tracked resources.</b> Handshake sockets live in a registry so teardown can abort a
    /// greet that is blocked mid-read. <see cref="Track"/> atomically refuses when the attempt is
    /// stale or teardown has begun — closing the accept-vs-teardown race where a socket accepted
    /// just as Disconnect runs would otherwise be registered after the close-all sweep and leak
    /// (the same race family as the stale-listener bug, review finding F7).
    /// </summary>
    public sealed class ConnectionLifecycle
    {
        private int _attempt;
        private readonly object _lock = new object();
        private readonly HashSet<IDisposable> _tracked = new HashSet<IDisposable>();
        private bool _rejectNew;

        /// <summary>Start a new attempt; every previously issued token is now stale.</summary>
        public int Begin() => Interlocked.Increment(ref _attempt);

        /// <summary>The live attempt token, for a worker starting now.</summary>
        public int Current => Volatile.Read(ref _attempt);

        /// <summary>Whether a captured token still names the live attempt.</summary>
        public bool IsCurrent(int attempt) => attempt == Volatile.Read(ref _attempt);

        /// <summary>Orphan every outstanding worker without starting a new attempt.</summary>
        public void Invalidate() => Interlocked.Increment(ref _attempt);

        /// <summary>Re-open the registry for a fresh attempt (after a prior teardown closed it).</summary>
        public void AcceptNew()
        {
            lock (_lock) _rejectNew = false;
        }

        /// <summary>
        /// Register a resource so teardown can close it. Atomically refuses — returning false, with
        /// the caller expected to close the resource itself — when the attempt token is stale or
        /// teardown has already begun. The check and the registration share one lock: there is no
        /// window where a doomed resource can slip in after the close-all sweep.
        /// </summary>
        public bool Track(IDisposable? resource, int attempt)
        {
            if (resource == null) return false;
            lock (_lock)
            {
                if (_rejectNew || !IsCurrent(attempt)) return false;
                _tracked.Add(resource);
                return true;
            }
        }

        /// <summary>Remove a resource whose ownership moved on (e.g. the handshake completed and a
        /// session link now owns the socket). It will no longer be closed by teardown.</summary>
        public void Untrack(IDisposable? resource)
        {
            if (resource == null) return;
            lock (_lock) _tracked.Remove(resource);
        }

        /// <summary>Whether any tracked resource is still registered (diagnostics/tests).</summary>
        public bool HasTracked
        {
            get { lock (_lock) return _tracked.Count != 0; }
        }

        /// <summary>
        /// Teardown: refuse all new registrations, then dispose everything tracked. Disposal runs
        /// outside the lock (a close can block or reenter); the registry is emptied first so a
        /// concurrent <see cref="Untrack"/> is harmless.
        /// </summary>
        public void RejectAndCloseAll()
        {
            List<IDisposable> snapshot;
            lock (_lock)
            {
                _rejectNew = true;
                snapshot = new List<IDisposable>(_tracked);
                _tracked.Clear();
            }
            foreach (var resource in snapshot)
            {
                try { resource.Dispose(); } catch { /* teardown is best-effort */ }
            }
        }
    }
}
