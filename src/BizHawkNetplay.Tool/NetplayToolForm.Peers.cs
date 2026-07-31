using System;
using System.Drawing;
using System.Threading;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    private void StartPeerIo(PeerLink link)
    {
        link.Attempt = CurrentConnectionAttempt;
        link.LastRecvTicks = MonotonicNow();
        link.WriterRunning = true;
        link.Writer = new Thread(() => PeerWriterLoop(link))
        { IsBackground = true, Name = "BizHawkNetplay-control-writer" };
        link.Reader = new Thread(() => PeerReaderLoop(link))
        { IsBackground = true, Name = "BizHawkNetplay-control-reader" };
        link.Writer.Start();
        link.Reader.Start();
    }

    private const long MaxQueuedControlBytes = 80L * 1024 * 1024;

    /// <summary>Queue one reliable control frame without ever waiting for socket flow control on
    /// EmuHawk's UI thread. A per-peer writer preserves ControlChannel ordering.</summary>
    private bool QueueControl(PeerLink link, ControlMessageType type, byte[] body, Action<bool>? completed = null)
    {
        if (body == null) body = [];
        if (!link.WriterRunning) { completed?.Invoke(false); return false; }
        long bytes = body.LongLength + 5;
        long queued = Interlocked.Add(ref link.QueuedBytes, bytes);
        if (queued > MaxQueuedControlBytes)
        {
            Interlocked.Add(ref link.QueuedBytes, -bytes);
            completed?.Invoke(false);
            return false;
        }
        link.Outbound.Enqueue(new OutboundMessage(type, body, completed));
        link.OutboundSignal.Set();
        // Re-check AFTER the enqueue. If the writer exited between the check at the top and the
        // Enqueue — its finally-drain having already run — the message would sit in the queue
        // forever: the completion never fires either way and QueuedBytes never comes back down. A
        // resync whose Resync frame hit this window would wait out the full apply deadline instead
        // of failing fast, and EndSession's Bye barrier would always time out. Draining here races
        // benignly with the writer's own drain: TryDequeue hands each item to exactly one caller,
        // so a completion still fires exactly once. False only when something was actually
        // abandoned — an empty drain means the message was handled (sent, or failed through its
        // own callback), and calling that a failure would be inventing one.
        if (!link.WriterRunning)
        {
            bool abandoned = false;
            while (link.Outbound.TryDequeue(out var orphan))
            {
                abandoned = true;
                Interlocked.Add(ref link.QueuedBytes, -(orphan.Body.LongLength + 5));
                try { orphan.Completed?.Invoke(false); } catch { }
            }
            if (abandoned) return false;
        }
        return true;
    }

    private void PeerWriterLoop(PeerLink link)
    {
        Exception? failure = null;
        try
        {
            while (link.WriterRunning)
            {
                if (!link.Outbound.TryDequeue(out var msg))
                {
                    link.OutboundSignal.WaitOne(250);
                    continue;
                }
                try
                {
                    link.Control.Send(msg.Type, msg.Body);
                    msg.Completed?.Invoke(true);
                }
                catch
                {
                    try { msg.Completed?.Invoke(false); } catch { }
                    throw;
                }
                finally { Interlocked.Add(ref link.QueuedBytes, -(msg.Body.LongLength + 5)); }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            link.WriterRunning = false;
            while (link.Outbound.TryDequeue(out var pending))
            {
                Interlocked.Add(ref link.QueuedBytes, -(pending.Body.LongLength + 5));
                try { pending.Completed?.Invoke(false); } catch { }
            }
            int attempt = link.Attempt;
            if (failure != null && _phase.IsActive && IsConnectionAttemptCurrent(attempt))
                BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt))
                        OnPeerLinkLost(link, "control send failed: " + failure.Message);
                });
        }
    }

    /// <summary>Reader loop for one control link. Dispatch depends on our role.</summary>
    private void PeerReaderLoop(PeerLink link)
    {
        try
        {
            while (_phase.IsActive && IsConnectionAttemptCurrent(link.Attempt))
            {
                var (type, body) = link.Control.Receive();
                Interlocked.Exchange(ref link.LastRecvTicks, MonotonicNow()); // liveness heartbeat
                if (type == ControlMessageType.Checksum)
                {
                    // Only the host aggregates; a joiner never receives checksums.
                    var generation = CurrentGeneration;
                    if (_isHost && ControlMessageCodec.TryDecodeChecksum(body, generation, out int frame, out uint hash))
                        RecordChecksum(link.Attempt, generation, link.RemotePort, frame, hash);
                }
                else if (type == ControlMessageType.Ping && body.Length == 8)
                {
                    if (!_simUnresponsive) // diagnostic: a "frozen" peer stops answering pings
                        QueueControl(link, ControlMessageType.Pong, body);
                }
                else if (type == ControlMessageType.Pong && body.Length == 8)
                {
                    double t0 = BitConverter.ToDouble(body, 0);
                    double rtt = _pingClock.Elapsed.TotalMilliseconds - t0;
                    if (rtt >= 0)
                    {
                        lock (_pingLock)
                        {
                            link.PingMs = link.PingMs < 0 ? rtt : 0.8 * link.PingMs + 0.2 * rtt;
                            link.PingCount++;
                        }
                        BeginInvokePeer(link, MaybeHintDelay);
                    }
                }
                else if (type == ControlMessageType.Pacing)
                {
                    if (!ControlMessageCodec.TryDecodePacing(body, out var generation,
                        out int sequence, out int acknowledges, out int theirFrame, out int theirAdvantage))
                        continue;
                    if (sequence <= 0) continue;
                    lock (_generationLock)
                    {
                        var driver = _driver;
                        if (generation != _generation || driver == null
                            || driver.Generation != generation) continue;
                        int myFrame = driver.CurrentFrame;
                        lock (_pingLock)
                        {
                            if (sequence <= link.LastReceivedPacingSequence) continue;
                            link.LocalAdvantage = myFrame - theirFrame;
                            link.RemoteAdvantage = theirAdvantage;
                            link.LastReceivedPacingSequence = sequence;
                            // The peer's advantage is initialized only after it acknowledges one of
                            // our reports. This prevents both high-latency peers treating the other's
                            // startup zero as a real measurement and both deciding they are ahead.
                            link.AdvantageKnown = acknowledges > 0
                                && acknowledges <= link.PacingSendSequence;
                            _frameAdvantage.Record(link.RemotePort, sequence,
                                link.LocalAdvantage, link.RemoteAdvantage, link.AdvantageKnown);
                        }
                    }
                }
                else if (type == ControlMessageType.PeerList)
                {
                    // Host reshuffled the mesh (e.g. someone rejoined) — update who we send to.
                    if (!_isHost)
                    {
                        var routes = HandshakeCodec.DecodeRoutes(body);
                        BeginInvokePeer(link, () =>
                        {
                            _meshOthers = routes;
                            ApplyJoinerMesh();
                            if (Verbose) Log($"mesh updated: {routes.Count} other peer(s)");
                        });
                    }
                }
                else if (type == ControlMessageType.Candidate)
                {
                    // A joiner reported its public (reflexive) endpoint; record it and re-share the
                    // candidate lists so everyone can reach it across NAT.
                    if (_isHost)
                    {
                        var eps = HandshakeCodec.DecodeEndpoints(body);
                        if (eps.Count > 0)
                            BeginInvokePeer(link, () => OnJoinerCandidate(link, eps[0]));
                    }
                }
                else if (type == ControlMessageType.ResyncBegin)
                {
                    if (!_isHost && ControlMessageCodec.TryDecodeResyncBegin(body, out var generation, out int stateBytes,
                        out int waitSeconds, out int resyncDelay, out var resyncMode, out bool settingsChange)
                        && generation == CurrentGeneration.Next())
                    {
                        link.ReceivingResyncEpoch = generation.Epoch;
                        link.ReceivingResyncBytes = stateBytes;
                        link.ReceivingResyncDelay = resyncDelay;
                        link.ReceivingResyncMode = resyncMode;
                        link.ReceivingResyncIsSettingsChange = settingsChange;
                        Interlocked.Exchange(ref link.ResyncReceiveDeadlineTicks,
                            StateReceiveDeadlineTicks(stateBytes, waitSeconds));
                        link.ResyncReceiving = true; // publish only after the deadline fields are complete
                        BeginInvokePeer(link, () =>
                        {
                            if (!_phase.IsActive || generation != CurrentGeneration.Next()) return;
                            _phase.BeginRebuild(settingsChange
                                ? RebuildReason.SettingsChange : RebuildReason.Desync);
                            Status($"receiving authoritative resync epoch {generation.Epoch} state…",
                                Color.DarkOrange);
                        });
                    }
                }
                else if (type == ControlMessageType.Resync)
                {
                    if (!_isHost && link.ResyncReceiving)
                    {
                        int expectedEpoch = link.ReceivingResyncEpoch;
                        int expectedBytes = link.ReceivingResyncBytes;
                        int announcedDelay = link.ReceivingResyncDelay;
                        var announcedMode = link.ReceivingResyncMode;
                        bool announcedSettingsChange = link.ReceivingResyncIsSettingsChange;
                        link.ResyncReceiving = false;
                        link.ReceivingResyncEpoch = 0;
                        link.ReceivingResyncBytes = 0;
                        Interlocked.Exchange(ref link.ResyncReceiveDeadlineTicks, 0);
                        if (ControlMessageCodec.TryDecodeStatePayload(body, out var generation, out var state)
                            && generation.Epoch == expectedEpoch && generation == CurrentGeneration.Next()
                            && state.Length == expectedBytes)
                            BeginInvokePeer(link, () => ApplyResyncAsJoiner(generation, state,
                                announcedDelay, announcedMode, announcedSettingsChange));
                        else
                            BeginInvokePeer(link, () => EndSession("host sent an invalid or incomplete resync state"));
                    }
                }
                else if (type == ControlMessageType.ResyncApplied)
                {
                    if (_isHost && ControlMessageCodec.TryDecodeGeneration(body, out var generation)
                        && generation == CurrentGeneration)
                        BeginInvokePeer(link, () => OnPeerResyncApplied(link, generation));
                }
                else if (type == ControlMessageType.ResyncResume)
                {
                    if (!_isHost && ControlMessageCodec.TryDecodeGeneration(body, out var generation)
                        && generation == CurrentGeneration)
                        BeginInvokePeer(link, () => ResumeResyncAsJoiner(generation));
                }
                else if (type == ControlMessageType.Bye)
                {
                    int attempt = link.Attempt;
                    BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt) && _peers.Contains(link))
                            EndSession($"{link.Label} left the session");
                    });
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            int attempt = link.Attempt;
            if (_phase.IsActive && IsConnectionAttemptCurrent(attempt)) BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt)) OnPeerLinkLost(link, ex.Message);
            });
        }
    }

}
