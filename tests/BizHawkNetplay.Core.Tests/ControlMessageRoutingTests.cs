using System;
using System.Collections.Generic;
using System.Linq;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The table every control message must be registered in.
///
/// This file exists because of one defect. <c>StateOffer</c> was added to the protocol without
/// being added to the predicate that decides which messages may be large, so it shipped capped at
/// the small-frame limit — its sender threw, its writer treated the throw as a link fault, and the
/// peer whose state the session had just chosen was dropped for having been asked for it. Majority
/// recovery could not have worked once, on any core.
///
/// Nothing about that was hard to see; it was just in a place nobody had to look. The completeness
/// test below is the fix for that shape of mistake: the next person to add a message type is told
/// by a failing test that they have not said which way it travels or how large it may be.
/// </summary>
public class ControlMessageRoutingTests
{
    private static IEnumerable<ControlMessageType> AllEnumValues =>
        Enum.GetValues(typeof(ControlMessageType)).Cast<ControlMessageType>();

    [Fact]
    public void EveryMessageTypeIsRegistered()
    {
        var missing = AllEnumValues.Where(t => !ControlMessageRouting.IsRegistered(t)).ToList();
        Assert.True(missing.Count == 0,
            "These ControlMessageType members have no entry in ControlMessageRouting, so nothing " +
            "has decided which direction they travel or whether they may carry a savestate: " +
            string.Join(", ", missing) + ". Add them to the table — see StateOffer for what " +
            "shipping without an entry cost.");
    }

    [Fact]
    public void TheTableRegistersNothingThatIsNotAMessageType()
    {
        // The other direction: a stale entry for a type that was removed would silently grant rules
        // to a value nobody sends, and would make the count checks below meaningless.
        var known = new HashSet<ControlMessageType>(AllEnumValues);
        Assert.All(ControlMessageRouting.All, t => Assert.Contains(t, known));
    }

    [Fact]
    public void SendAndReceivePermissionsAreExactMirrors()
    {
        // If these ever disagree, one end can send something the other refuses to accept — which
        // presents as a link that dies for no stated reason.
        foreach (var type in ControlMessageRouting.All)
        {
            Assert.Equal(ControlMessageRouting.MaySend(type, weAreHost: true),
                ControlMessageRouting.Accepts(type, weAreHost: false));
            Assert.Equal(ControlMessageRouting.MaySend(type, weAreHost: false),
                ControlMessageRouting.Accepts(type, weAreHost: true));
        }
    }

    [Fact]
    public void ExactlyTheStateBearingTypesMayBeLarge()
    {
        // Pinned by name rather than counted: the 64 MiB ceiling is the one permission worth
        // spelling out, and a fourth member joining this set should be a deliberate edit here.
        var large = ControlMessageRouting.All
            .Where(t => ControlMessageRouting.Size(t) == MessageSize.State)
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { ControlMessageType.Resync, ControlMessageType.State, ControlMessageType.StateOffer },
            large);
    }

    [Fact]
    public void TheStateBearingTypesTravelInTheDirectionsTheirHandlersExpect()
    {
        // A state and a resync are the host distributing its baseline; an offer is a donor
        // answering the host's majority request. Reversing any of them would hand the wrong end a
        // 64 MiB allocation, which is the fault the direction rule was added for.
        Assert.Equal(MessageDirection.HostToJoiner, ControlMessageRouting.Direction(ControlMessageType.State));
        Assert.Equal(MessageDirection.HostToJoiner, ControlMessageRouting.Direction(ControlMessageType.Resync));
        Assert.Equal(MessageDirection.JoinerToHost, ControlMessageRouting.Direction(ControlMessageType.StateOffer));
    }

    [Fact]
    public void TheReaderDispatchedSetIsExactlyWhatTheSessionLoopHandles()
    {
        // Mirrors the arms of NetplayToolForm.PeerReaderLoop. The Tool cannot be referenced from
        // here, so this pins the intent: anything else is consumed synchronously by the handshake,
        // and a type marked dispatched with no arm would simply be dropped in silence.
        var dispatched = ControlMessageRouting.All
            .Where(ControlMessageRouting.ReaderDispatched)
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[]
        {
            ControlMessageType.Bye,
            ControlMessageType.Candidate,
            ControlMessageType.Checksum,
            ControlMessageType.DivergenceReport,
            ControlMessageType.ExclusionMask,
            ControlMessageType.InputOutage,
            ControlMessageType.Pacing,
            ControlMessageType.PeerList,
            ControlMessageType.Ping,
            ControlMessageType.Pong,
            ControlMessageType.Resync,
            ControlMessageType.ResyncApplied,
            ControlMessageType.ResyncBegin,
            ControlMessageType.ResyncResume,
            ControlMessageType.SeatVacated,
            ControlMessageType.StateOffer,
            ControlMessageType.StateRequest,
        }, dispatched);
    }

    [Fact]
    public void AnUnregisteredTypeIsCappedSmallRatherThanTrusted()
    {
        // A value off the wire that this build has never heard of must not buy itself the large
        // ceiling by being unrecognised. Accepts stays permissive so an older peer's unknown
        // message is ignored by the dispatch rather than treated as an attack.
        var unknown = (ControlMessageType)200;
        Assert.False(ControlMessageRouting.IsRegistered(unknown));
        Assert.Equal(MessageSize.Small, ControlMessageRouting.Size(unknown));
        Assert.True(ControlMessageRouting.Accepts(unknown, weAreHost: true));
        Assert.True(ControlMessageRouting.Accepts(unknown, weAreHost: false));
    }
}
