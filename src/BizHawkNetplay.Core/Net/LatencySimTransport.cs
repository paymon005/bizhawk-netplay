using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// A diagnostic <see cref="ITransport"/> decorator that holds each inbound datagram for a fixed
/// delay before delivering it, simulating one-way network latency. Both peers enabling the same
/// delay yields a round-trip of roughly twice it. Purely a testing aid: on a single machine there
/// is no real latency or clock skew, so this is how rollback (and its time-sync valve) can be
/// exercised without a second computer. Sends pass straight through; only receives are delayed.
/// Single-consumer (the FrameDriver drains on the UI thread), matching how the real transports
/// are used.
/// </summary>
public sealed class LatencySimTransport : ITransport, IDisposable
{
    private readonly ITransport _inner;
    private readonly int _delayMs;
    private readonly Func<long> _nowMs;
    private readonly Queue<Pending> _buffer = new();

    private readonly struct Pending
    {
        public Pending(long releaseMs, byte[] data) { ReleaseMs = releaseMs; Data = data; }
        public long ReleaseMs { get; }
        public byte[] Data { get; }
    }

    public LatencySimTransport(ITransport inner, int delayMs) : this(inner, delayMs, null) { }

    /// <summary>Overload with an injectable clock (milliseconds) so tests can drive delivery deterministically.</summary>
    internal LatencySimTransport(ITransport inner, int delayMs, Func<long>? nowMs)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (delayMs < 0) throw new ArgumentOutOfRangeException(nameof(delayMs));
        _delayMs = delayMs;
        var clock = Stopwatch.StartNew();
        _nowMs = nowMs ?? (() => clock.ElapsedMilliseconds);
    }

    public void Send(byte[] datagram) => _inner.Send(datagram);

    public bool TryReceive(out byte[] datagram)
    {
        // Pull at most one inner datagram per call. FrameDriver bounds calls per UI callback, so
        // this decorator must not hide an unbounded inner drain behind a single TryReceive.
        long now = _nowMs();
        if (_inner.TryReceive(out var d))
            _buffer.Enqueue(new Pending(now + _delayMs, d));

        if (_buffer.Count > 0 && _buffer.Peek().ReleaseMs <= now)
        {
            datagram = _buffer.Dequeue().Data;
            return true;
        }
        datagram = null!;
        return false;
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
