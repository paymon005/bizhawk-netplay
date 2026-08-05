using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// A reliable, ordered, bidirectional byte <see cref="Stream"/> over an unreliable datagram link to
/// a single peer — the piece that lets the punch-through path reuse the TCP control channel wholesale.
/// The normal (TCP) session gets reliability for free from the kernel; a NAT-punched session has only
/// one UDP socket, so this re-implements the essential slice of TCP (sequencing, cumulative ACKs,
/// timeout retransmit, a flow-control window) needed to ship the ~1 MiB savestate and the periodic
/// checksums intact. Wrap it in a <see cref="Session.ControlChannel"/> and the handshake, state
/// transfer, ping/pong and resync machinery run unmodified.
///
/// It owns no socket: the caller supplies a thread-safe <c>send</c> for one segment and feeds inbound
/// segments to <see cref="OnDatagram"/> from its receive loop — so the same UDP socket also carries
/// the unreliable input hot path, demultiplexed by the owner. Fully testable by cross-wiring two
/// instances in-process with injected loss and reordering.
/// </summary>
public sealed class ReliableUdpStream : Stream
{
    private const byte SegData = 0x01;
    private const byte SegAck = 0x02;
    private const byte SegFin = 0x03;

    private const int Mss = 1024;         // payload bytes per DATA segment (outer framing kept < MTU)
    private const int Window = 128;       // segments in flight, ceiling (flow control): 128 KiB
    private const int RtoStartMs = 500;   // initial retransmit timeout
    private const int RtoMaxMs = 1500;    // backoff ceiling
    private const int DeadRetries = 40;   // consecutive fruitless retransmits of the base -> fault

    /// <summary>
    /// Floor for the loss-adapted window below. Matches <see cref="RtoBurst"/>: no point offering
    /// the link fewer segments than a single timer burst re-drives anyway. The equality is a
    /// COUPLING, not a coincidence — lower the burst without lowering this floor and a timeout
    /// stops being able to re-drive the whole window it just shrank to, so the two must move
    /// together or the floor must stay the larger of them.
    /// </summary>
    private const int MinWindow = RtoBurst;

    /// <summary>
    /// How much reassembled data may wait for a reader before the sender is made to wait instead.
    ///
    /// Well above anything a real transfer parks here — the consumers read continuously, and the
    /// window ahead of this is 64 KiB — so it costs a healthy resync nothing. It is here because
    /// "the reader will keep up" was an assumption rather than a rule, and the party it was trusting
    /// is the remote one.
    /// </summary>
    public const int DefaultMaxReadableBytes = 8 * 1024 * 1024;

    private readonly Action<byte[]> _send;
    private readonly object _gate = new();

    // Sender state (guarded by _gate).
    private readonly SortedDictionary<uint, byte[]> _unacked = new();
    private uint _nextSeq;                // next DATA seq to assign
    private uint _sendBase;               // lowest unacked seq
    private long _baseSentTicks;          // when _sendBase was last (re)sent
    private int _rtoMs = RtoStartMs;
    private int _baseRetries;             // consecutive retransmits of _sendBase with no progress
    // Loss-adapted window (guarded by _gate): Write may burst a full 128-segment window at line
    // rate, and on a constrained uplink that burst IS the loss — the queue overflows, the tail
    // drops, and repair proceeds at RtoBurst per timeout. Halve what may be in flight when the
    // timer fires (evidence of sustained loss, unlike a fast retransmit's single hole), grow it
    // back one segment per progressing ACK. TCP's shape, at this protocol's scale.
    private int _effectiveWindow = Window;
    private bool _finSent;
    private uint _finSeq;                 // seq marking end-of-stream (all data seq < _finSeq)

    // Receiver state (guarded by _gate).
    private readonly SortedDictionary<uint, byte[]> _reorder = new();
    private uint _rcvBase;                // next in-order seq to deliver
    // Reassembled bytes waiting for Read, held as the 1KiB SEGMENTS they arrived in rather than as
    // individual bytes. A Queue<byte> meant one enqueue per byte and one dequeue per byte: shipping
    // a multi-megabyte savestate through it was millions of operations plus repeated doubling of a
    // backing array well past the large-object threshold — all to move bytes that arrived in
    // ready-made blocks. This queues the blocks and block-copies out of the head.
    private readonly Queue<byte[]> _readable = new();
    private int _readableHeadOffset;   // bytes already handed out of the block at the head
    private int _readableBytes;        // total bytes across the queue, minus the head offset
    private bool _finReceived;
    private uint _rcvFinSeq;

    private volatile bool _faulted;
    private volatile bool _closed;
    private readonly Thread _timer;

    private readonly int _maxReadableBytes;

    /// <param name="maxReadableBytes">Reassembled bytes that may wait for a reader before the
    /// sender is held off; see <see cref="DefaultMaxReadableBytes"/>.</param>
    public ReliableUdpStream(Action<byte[]> send, int maxReadableBytes = DefaultMaxReadableBytes)
    {
        if (maxReadableBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxReadableBytes));
        _maxReadableBytes = maxReadableBytes;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _baseSentTicks = NowMs();
        _timer = new Thread(RetransmitLoop) { IsBackground = true, Name = "BizHawkNetplay-reliable-udp" };
        _timer.Start();
    }

    // ---------------------------------------------------------------- Stream face

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override bool CanTimeout => true;

    private volatile int _readTimeoutMs = Timeout.Infinite;

    /// <summary>
    /// Bounds how long <see cref="Read"/> may wait for the FIRST available byte (milliseconds,
    /// or <see cref="Timeout.Infinite"/>). Mirrors a NetworkStream: data trickling in resets
    /// nothing — each Read call gets the full window — but total silence past the window throws
    /// <see cref="IOException"/>. This is what lets the punch path put a deadline on a peer that
    /// keeps the reliable layer ACKed while never sending the application bytes we're blocked on.
    /// </summary>
    public override int ReadTimeout
    {
        get => _readTimeoutMs;
        set
        {
            if (value <= 0 && value != Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be positive or Timeout.Infinite");
            _readTimeoutMs = value;
        }
    }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { /* Write transmits immediately; nothing buffered above the segment layer. */ }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        int end = offset + count;
        int i = offset;
        while (i < end)
        {
            var toSend = new List<byte[]>();
            lock (_gate)
            {
                // Flow control: block while the window is full, until ACKs free it (or the link faults).
                while (!_faulted && !_closed && _unacked.Count >= _effectiveWindow)
                    Monitor.Wait(_gate, 1000);
                ThrowIfBroken();

                while (i < end && _unacked.Count < _effectiveWindow)
                {
                    int n = Math.Min(Mss, end - i);
                    var seg = new byte[5 + n];
                    seg[0] = SegData;
                    WriteU32(seg, 1, _nextSeq);
                    Buffer.BlockCopy(buffer, i, seg, 5, n);
                    _unacked[_nextSeq] = seg;
                    if (_nextSeq == _sendBase) { _baseSentTicks = NowMs(); _baseRetries = 0; }
                    _nextSeq++;
                    i += n;
                    toSend.Add(seg);
                }
            }
            foreach (var s in toSend) _send(s);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (count == 0) return 0;
        int timeoutMs = _readTimeoutMs; // snapshot: one window per Read call
        long deadline = timeoutMs > 0 ? NowMs() + timeoutMs : long.MaxValue;
        int result;
        byte[]? ack = null;
        lock (_gate)
        {
            while (true)
            {
                if (_readableBytes > 0)
                {
                    // Copies out of however many queued blocks the request spans, leaving a
                    // partly-consumed head block in place rather than splitting it.
                    int written = 0;
                    while (written < count && _readable.Count > 0)
                    {
                        var head = _readable.Peek();
                        int chunk = Math.Min(count - written, head.Length - _readableHeadOffset);
                        Buffer.BlockCopy(head, _readableHeadOffset, buffer, offset + written, chunk);
                        written += chunk;
                        _readableHeadOffset += chunk;
                        if (_readableHeadOffset == head.Length)
                        {
                            _readable.Dequeue();
                            _readableHeadOffset = 0;
                        }
                    }
                    _readableBytes -= written;
                    // Space just came free: take whatever the cap was holding back, so a sender
                    // stalled by backpressure is released on this read rather than on a timeout.
                    // And if that advanced the base, SAY so — the acknowledgement is the only
                    // channel the sender is told through, and without one here its next progress
                    // waits for its own RTO retransmit to provoke a fresh cumulative ACK,
                    // collapsing a capped transfer to ~Window per RTO.
                    uint baseBefore = _rcvBase;
                    DrainToReadable();
                    if (_rcvBase != baseBefore) ack = MakeAck();
                    result = written;
                    break;
                }
                if (_finReceived && _rcvBase >= _rcvFinSeq) { result = 0; break; } // clean EOF
                ThrowIfBroken();
                long remaining = deadline - NowMs();
                if (remaining <= 0) throw new IOException("reliable UDP read timed out");
                Monitor.Wait(_gate, (int)Math.Min(1000, remaining));
            }
        }
        if (ack != null) _send(ack); // outside the lock, as every other send here is
        return result;
    }

    // ---------------------------------------------------------------- datagram plumbing

    /// <summary>Feed one inbound reliable segment (already stripped of the owner's outer framing).</summary>
    public void OnDatagram(byte[] seg)
    {
        if (seg == null || seg.Length < 1) return;
        byte type = seg[0];
        // At most one reply per segment (an ACK), plus at most one fast retransmit. A List per
        // inbound segment was ~16k allocations per state transfer to carry a single element.
        byte[]? reply = null;
        byte[]? fastRetransmit = null;
        lock (_gate)
        {
            if (type == SegAck && seg.Length >= 5)
            {
                uint ack = ReadU32(seg, 1);
                fastRetransmit = OnAck(ack);
            }
            else if (type == SegData && seg.Length >= 5)
            {
                uint s = ReadU32(seg, 1);
                var payload = new byte[seg.Length - 5];
                Buffer.BlockCopy(seg, 5, payload, 0, payload.Length);
                AcceptData(s, payload);
                reply = MakeAck();
            }
            else if (type == SegFin && seg.Length >= 5)
            {
                uint s = ReadU32(seg, 1);
                if (!_finReceived) { _finReceived = true; _rcvFinSeq = s; }
                reply = MakeAck();
                Monitor.PulseAll(_gate);
            }
        }
        // Outside the lock, as every other send here is.
        if (reply != null) _send(reply);
        if (fastRetransmit != null) _send(fastRetransmit);
    }

    /// <summary>
    /// Duplicate ACKs before a retransmit is believed. The receiver ACKs every DATA segment, so a
    /// lost segment at the base produces a train of identical cumulative ACKs while later segments
    /// keep arriving; three of them is the long-standing signal that it was loss rather than
    /// reordering, and it is what stops a two-segment swap from triggering a pointless resend.
    /// </summary>
    private const int DuplicateAcksBeforeFastRetransmit = 3;

    // The zero DEFAULT is load-bearing, not an oversight: an ack of 0 arriving while data is
    // unacked means the receiver is taking later segments and still missing seq 0 — a genuine
    // loss signal that must count toward fast retransmit from the very first ack of a stream,
    // which is exactly what losing the opening segment produces. The case where an ack of 0
    // carries no information (nothing sent yet) is already excluded by the _unacked.Count == 0
    // guard below. A "fix" that made the first ack establish-only was tried and reverted: it
    // cost the lost-first-segment repair one of its three duplicates and pushed that repair
    // onto the 500ms timer.
    private uint _lastAckValue;
    private int _duplicateAcks;

    /// <summary>
    /// Handle a cumulative ACK; returns a segment to retransmit immediately, or null.
    ///
    /// Without this the only repair mechanism is the retransmit timer, which resends the sixteen
    /// lowest unacked segments per timeout — so with several holes spread through a window, each
    /// one waits its own full RTO (500ms, doubling to 1500) while fifteen duplicates go out
    /// alongside it. On a 16MiB state that is thousands of holes at up to 1.5s each: a resync that
    /// fails on a link which was merely lossy. A single loss now costs about one round trip.
    ///
    /// Deliberately does NOT touch the backoff: a fast retransmit is evidence of one lost segment,
    /// not of a congested path, so doubling the RTO or spending a dead-retry on it would punish
    /// exactly the case this handles well. _baseSentTicks moves so the timer does not immediately
    /// resend the same segment on top of this one.
    /// </summary>
    private byte[]? OnAck(uint ack)
    {
        // An ACK can only acknowledge data we've actually queued. A corrupt or hostile segment
        // carrying an ack beyond _nextSeq would otherwise spin this loop up to ~4 billion times
        // while holding _gate (a hang) and advance _sendBase past _nextSeq, permanently corrupting
        // the sender. Anything at/below _sendBase is a stale duplicate the while-loop already ignores.
        if (ack > _nextSeq) return null;
        bool progressed = false;
        while (_sendBase < ack)
        {
            if (_unacked.Remove(_sendBase)) progressed = true;
            _sendBase++;
        }
        if (progressed)
        {
            _baseSentTicks = NowMs();
            _baseRetries = 0;
            _rtoMs = RtoStartMs;
            _duplicateAcks = 0;
            _lastAckValue = ack;
            if (_effectiveWindow < Window) _effectiveWindow++; // recover the halved window gradually
            Monitor.PulseAll(_gate); // wake a Write blocked on the window
            return null;
        }

        // No progress: either a stale duplicate, or the receiver telling us the base is missing
        // while it keeps taking what follows.
        if (ack != _lastAckValue) { _lastAckValue = ack; _duplicateAcks = 0; return null; }
        if (_unacked.Count == 0) return null;
        if (++_duplicateAcks < DuplicateAcksBeforeFastRetransmit) return null;
        _duplicateAcks = 0;
        if (!_unacked.TryGetValue(_sendBase, out var missing)) return null;
        _baseSentTicks = NowMs();   // the timer must not pile another copy on top of this one
        return missing;
    }

    private void AcceptData(uint s, byte[] payload)
    {
        if (s < _rcvBase) return;                       // duplicate already delivered; re-ACK below
        if (s >= _rcvBase + Window) return;             // outside the receive window; drop
        if (!_reorder.ContainsKey(s)) _reorder[s] = payload;
        DrainToReadable();
        Monitor.PulseAll(_gate); // wake a blocked Read
    }

    /// <summary>
    /// Move the contiguous run at the base into the readable queue, and stop at the buffer cap.
    ///
    /// The reorder buffer was bounded by the receive window; what sat behind it was not. A sender
    /// that keeps sending while nothing reads had its bytes accumulate here without limit, because
    /// every segment delivered advanced the base and so widened the window again. Holding the run
    /// in place instead leaves the base where it is, which leaves the acknowledgement where it is,
    /// which is how a sender is told to wait — flow control the protocol already had the machinery
    /// for and simply never applied.
    ///
    /// Called from Read as well as from AcceptData, so space freed by a reader is used at once
    /// rather than on whatever retransmit happens to arrive next.
    /// </summary>
    private void DrainToReadable()
    {
        while (_readableBytes < _maxReadableBytes && _reorder.TryGetValue(_rcvBase, out var p))
        {
            _reorder.Remove(_rcvBase);
            if (p.Length > 0) { _readable.Enqueue(p); _readableBytes += p.Length; }
            _rcvBase++;
        }
    }

    private byte[] MakeAck()
    {
        var a = new byte[5];
        a[0] = SegAck;
        WriteU32(a, 1, _rcvBase); // cumulative: everything below _rcvBase is delivered
        return a;
    }

    private const int RtoBurst = 16; // segments resent from the base per timeout (covers a run of losses)

    /// <summary>
    /// How long the retransmit thread keeps re-driving unacked tail data and the FIN after Dispose.
    ///
    /// This was 2000ms, with a comment claiming room for "a burst per RTO plus a few FIN re-drives".
    /// The arithmetic never supported it. The FIN re-drive shared <see cref="_baseSentTicks"/> with
    /// data retransmission and waited out the same <see cref="_rtoMs"/>, which backs off to
    /// <see cref="RtoMaxMs"/> — so a tail needing two RTO rounds spent 1500ms of the linger and then
    /// wanted 1500ms more for the FIN, out of 2000. The FIN went out exactly once, and if that copy
    /// was lost the peer never got its EOF: it blocked until its own read timeout and reported a
    /// network fault for a stream that had closed cleanly. On a lossy link that is a session ending
    /// with the wrong reason.
    ///
    /// Sized against the schedule now rather than rounded: <see cref="FinRedriveMs"/> apart, with
    /// room for the tail repair to finish first, gives ten attempts. Dispose still never blocks —
    /// the thread is a background one and the linger runs out behind it.
    /// </summary>
    private const int CloseLingerMs = 4000;
    /// <summary>Cadence for re-driving the FIN, on its OWN clock. Independent of the data RTO
    /// because they are different questions: the data backoff is a congestion response, while a
    /// lost 5-byte FIN says nothing about the path and its repair should not be rationed by one.</summary>
    private const int FinRedriveMs = 400;
    private long _lingerDeadline;         // guarded by _gate; 0 until Close arms it
    private long _finSentTicks;           // guarded by _gate; when the FIN last went out

    private void RetransmitLoop()
    {
        var batch = new List<byte[]>(RtoBurst);
        while (!_faulted)
        {
            if (_closed)
            {
                // Closed is not done: Close() queues a FIN, and "sent once" is not "delivered" on
                // the link this exists for. Keep the timer alive within the linger so a lost tail
                // or lost FIN is re-driven instead of stranding the peer on a read timeout it
                // will misreport as a network fault.
                lock (_gate)
                {
                    bool lingering = _finSent && _lingerDeadline != 0 && NowMs() < _lingerDeadline;
                    if (!lingering) break;
                }
            }
            Thread.Sleep(50);
            batch.Clear();
            byte[]? refin = null;
            lock (_gate)
            {
                long now = NowMs();
                if (_unacked.Count > 0 && now - _baseSentTicks >= _rtoMs)
                {
                    // Resend the oldest window of unacked segments so several consecutive drops
                    // recover in a single RTO instead of one-per-timeout.
                    foreach (var kv in _unacked)
                    {
                        batch.Add(kv.Value);
                        if (batch.Count >= RtoBurst) break;
                    }
                    _baseSentTicks = now;
                    _rtoMs = Math.Min(RtoMaxMs, _rtoMs * 2);
                    _effectiveWindow = Math.Max(MinWindow, _effectiveWindow / 2); // sustained loss: back off
                    if (++_baseRetries >= DeadRetries) { _faulted = true; Monitor.PulseAll(_gate); }
                }
                // Re-drive a pending FIN, on its own clock and regardless of unacked data.
                //
                // The "only once data is all acked" gate this used to carry was unnecessary
                // conservatism that cost the FIN most of its attempts: an early FIN is already
                // harmless, because the receiver records _rcvFinSeq and reports EOF only once
                // _rcvBase reaches it (see AcceptData/Read). So a FIN arriving mid-repair simply
                // waits there for the data — and arriving is the whole difficulty.
                if (_finSent && now - _finSentTicks >= FinRedriveMs)
                {
                    refin = MakeFin();
                    _finSentTicks = now;
                }
            }
            foreach (var s in batch) _send(s);
            if (refin != null) _send(refin);
        }
    }

    private byte[] MakeFin()
    {
        var f = new byte[5];
        f[0] = SegFin;
        WriteU32(f, 1, _finSeq);
        return f;
    }

    private void ThrowIfBroken()
    {
        if (_faulted) throw new IOException("reliable UDP link timed out");
        if (_closed) throw new IOException("reliable UDP link closed");
    }

    // ---------------------------------------------------------------- lifecycle

    public override void Close()
    {
        byte[]? fin = null;
        lock (_gate)
        {
            if (!_finSent && !_faulted)
            {
                _finSent = true;
                _finSeq = _nextSeq; // ends after all data queued so far
                _finSentTicks = NowMs();
                fin = MakeFin();
            }
            // Arm the retransmit thread's linger BEFORE _closed is set (base.Close() below runs
            // Dispose), so there is no instant where the loop can see closed-without-deadline
            // and exit with the FIN unrepaired.
            if (_lingerDeadline == 0) _lingerDeadline = NowMs() + CloseLingerMs;
        }
        if (fin != null) { try { _send(fin); } catch { /* best effort */ } }
        base.Close();
    }

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        lock (_gate) Monitor.PulseAll(_gate);
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- helpers

    // netstandard2.0 has no Environment.TickCount64; a shared monotonic stopwatch stands in.
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static long NowMs() => Clock.ElapsedMilliseconds;
    private static void WriteU32(byte[] b, int o, uint v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static uint ReadU32(byte[] b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
}
