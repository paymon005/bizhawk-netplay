namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// Byte-level datagram transport. Deliberately opaque: it moves datagrams and nothing more,
    /// so all serialization and sync logic stays testable off any real socket. A real UDP
    /// implementation does socket I/O and timestamping on a background thread and hands received
    /// datagrams to a thread-safe queue that <see cref="Sync.FrameDriver"/> drains at the top of
    /// each frame; the in-process <see cref="LoopbackTransport"/> stands in for tests and
    /// same-machine two-instance play.
    /// </summary>
    public interface ITransport
    {
        /// <summary>Send one datagram on the input channel (unreliable; loss is expected and tolerated).</summary>
        void Send(byte[] datagram);

        /// <summary>
        /// Take the next received datagram if one is queued. Non-blocking; returns false when
        /// the queue is empty. The FrameDriver calls this in a loop to drain the queue.
        /// </summary>
        bool TryReceive(out byte[] datagram);
    }
}
