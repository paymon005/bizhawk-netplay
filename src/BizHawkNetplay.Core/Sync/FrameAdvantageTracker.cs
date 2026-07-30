using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Sync;

/// <summary>
/// Aggregates the signed clock-skew samples from several peers without letting a fresh report from
/// one peer replay another peer's cached advantage. Callers provide samples only for one session
/// generation and call <see cref="Reset"/> whenever that generation changes.
/// </summary>
public sealed class FrameAdvantageTracker
{
    private sealed class PeerSample
    {
        public int Sequence;
        public int AppliedSequence;
        public int LocalAdvantage;
        public int RemoteAdvantage;
        public bool Known;
    }

    private readonly Dictionary<int, PeerSample> _samples = new Dictionary<int, PeerSample>();
    private int _lastPublishedAdvantage;
    private int _revision;

    public void Reset()
    {
        _samples.Clear();
        _lastPublishedAdvantage = 0;
        _revision = 0;
    }

    /// <summary>Record the newest wire sample for one logical peer. Returns false for a duplicate
    /// or reordered sequence number.</summary>
    public bool Record(int peerPort, int sequence, int localAdvantage, int remoteAdvantage, bool known)
    {
        if (peerPort < 0) throw new ArgumentOutOfRangeException(nameof(peerPort));
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!_samples.TryGetValue(peerPort, out var sample))
        {
            sample = new PeerSample();
            _samples[peerPort] = sample;
        }
        if (sequence <= sample.Sequence) return false;
        sample.Sequence = sequence;
        sample.LocalAdvantage = localAdvantage;
        sample.RemoteAdvantage = remoteAdvantage;
        sample.Known = known;
        return true;
    }

    /// <summary>
    /// Consume the latest aggregate. Positive debt is fresh only when a peer currently defining
    /// the worst advantage supplied a new sample; a cached runner-up becoming worst cannot replay
    /// its already-consumed sample. A genuinely new all-caught-up aggregate may still clear debt.
    /// </summary>
    public int Consume(out bool known, out int revision, out bool fresh)
    {
        known = false;
        int worst = 0;
        bool anyNewSample = false;
        bool worstSourceIsFresh = false;

        foreach (var sample in _samples.Values)
        {
            if (!sample.Known) continue;
            known = true;
            bool sampleFresh = sample.Sequence > sample.AppliedSequence;
            if (sampleFresh)
            {
                anyNewSample = true;
                sample.AppliedSequence = sample.Sequence;
            }

            int advantage = (sample.LocalAdvantage - sample.RemoteAdvantage) / 2;
            if (advantage > worst)
            {
                worst = advantage;
                worstSourceIsFresh = sampleFresh;
            }
            else if (advantage == worst && sampleFresh)
            {
                worstSourceIsFresh = true;
            }
        }

        fresh = known && anyNewSample
            && (worstSourceIsFresh || (worst == 0 && _lastPublishedAdvantage != 0));
        if (fresh)
        {
            _lastPublishedAdvantage = worst;
            _revision++;
        }
        revision = _revision;
        return known ? worst : 0;
    }
}
