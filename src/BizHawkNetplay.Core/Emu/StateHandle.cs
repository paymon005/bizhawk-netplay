using System;

namespace BizHawkNetplay.Core.Emu
{
    /// <summary>
    /// Opaque reference to an in-memory whole-core savestate taken at a specific frame.
    /// The rollback ring buffer holds these; only the <see cref="IEmuAdapter"/> that produced
    /// a handle knows how to load it. On BizHawk the token is a pooled <c>MemoryStream</c> the adapter
    /// serializes the core into and hands back on release — the API that returns a GUID per state
    /// allocates a fresh array every time, which is what put the rollback ring on the large object heap.
    /// A test fake may stash raw bytes instead.
    /// Carrying the frame number lets a strategy assert it reloaded the state it meant to.
    /// </summary>
    public sealed class StateHandle
    {
        public StateHandle(int frame, object token)
        {
            Frame = frame;
            Token = token ?? throw new ArgumentNullException(nameof(token));
        }

        /// <summary>Simulation frame at which this state was captured.</summary>
        public int Frame { get; }

        /// <summary>Adapter-private identifier for the stored state (never interpreted here).</summary>
        public object Token { get; }
    }
}
