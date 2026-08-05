using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session;

/// <summary>Which way a control message legitimately travels.</summary>
public enum MessageDirection
{
    /// <summary>The host sends it; a joiner receives it.</summary>
    HostToJoiner,
    /// <summary>A joiner sends it; the host receives it.</summary>
    JoinerToHost,
    /// <summary>Both ends send it — a ping, a proof, an error, a goodbye.</summary>
    Either,
}

/// <summary>How large a message may legitimately be.</summary>
public enum MessageSize
{
    /// <summary>Bounded by the small-frame cap. Nearly everything: a kilobyte in practice.</summary>
    Small,
    /// <summary>May carry a whole-core savestate, so the large cap applies once authenticated.</summary>
    State,
}

/// <summary>
/// The rules for every control message in one table: which way it travels, how large it may be, and
/// whether the session reader dispatches it.
///
/// <b>Why this exists.</b> Those three facts used to live in three places — a <c>CarriesState</c>
/// predicate, a direction check beside it, and fourteen hand-written <c>_isHost</c> guards in the
/// reader loop — and a new message type had to be added to all three by someone who remembered all
/// three. <see cref="ControlMessageType.StateOffer"/> was added to none of them: it went out capped
/// at the small-frame limit, its sender's writer treated the resulting throw as a link fault, and
/// the peer whose state the session had just chosen got dropped for having been asked for it.
/// Majority recovery could not have worked once, on any core.
///
/// So the table is the source and the callers read it. The test that makes it worth having asserts
/// every <see cref="ControlMessageType"/> is registered here — a type added without deciding its
/// direction and size fails the suite instead of shipping.
///
/// <b>What it deliberately does not encode.</b> Not whether a message is expected in a given phase,
/// nor what any of them mean. Ping and Pong ride both the lobby and the live session; Error can
/// arrive at any point. Encoding "expected right now" would be a state machine, and a wrong one
/// would refuse legitimate traffic — the failure this is meant to prevent, arriving from the other
/// side.
/// </summary>
public static class ControlMessageRouting
{
    private readonly struct Rule
    {
        public Rule(MessageDirection direction, MessageSize size, bool readerDispatched)
        {
            Direction = direction;
            Size = size;
            ReaderDispatched = readerDispatched;
        }
        public MessageDirection Direction { get; }
        public MessageSize Size { get; }
        public bool ReaderDispatched { get; }
    }

    // Ordered as the enum is, so a reader can diff the two by eye.
    private static readonly Dictionary<ControlMessageType, Rule> Rules = new()
    {
        // --- handshake, consumed synchronously by Handshake.cs -----------------------------------
        [ControlMessageType.Hello] = new(MessageDirection.Either, MessageSize.Small, false),
        [ControlMessageType.Welcome] = new(MessageDirection.HostToJoiner, MessageSize.Small, false),
        [ControlMessageType.State] = new(MessageDirection.HostToJoiner, MessageSize.State, false),
        [ControlMessageType.Start] = new(MessageDirection.HostToJoiner, MessageSize.Small, false),
        [ControlMessageType.Error] = new(MessageDirection.Either, MessageSize.Small, false),
        [ControlMessageType.Ready] = new(MessageDirection.JoinerToHost, MessageSize.Small, false),
        [ControlMessageType.Go] = new(MessageDirection.HostToJoiner, MessageSize.Small, false),
        [ControlMessageType.Auth] = new(MessageDirection.Either, MessageSize.Small, false),
        [ControlMessageType.MeshRtt] = new(MessageDirection.Either, MessageSize.Small, false),
        [ControlMessageType.InputDelay] = new(MessageDirection.HostToJoiner, MessageSize.Small, false),
        // Reserved and never shipped — registered so the completeness test stays meaningful rather
        // than being taught to skip members. See its declaration for why the slot is not reused.
        [ControlMessageType.ResyncRequest] = new(MessageDirection.JoinerToHost, MessageSize.Small, false),

        // --- session, dispatched by the reader loop ----------------------------------------------
        [ControlMessageType.Checksum] = new(MessageDirection.JoinerToHost, MessageSize.Small, true),
        [ControlMessageType.Bye] = new(MessageDirection.Either, MessageSize.Small, true),
        [ControlMessageType.Ping] = new(MessageDirection.Either, MessageSize.Small, true),
        [ControlMessageType.Pong] = new(MessageDirection.Either, MessageSize.Small, true),
        [ControlMessageType.Resync] = new(MessageDirection.HostToJoiner, MessageSize.State, true),
        [ControlMessageType.PeerList] = new(MessageDirection.HostToJoiner, MessageSize.Small, true),
        [ControlMessageType.Candidate] = new(MessageDirection.JoinerToHost, MessageSize.Small, true),
        [ControlMessageType.ResyncBegin] = new(MessageDirection.HostToJoiner, MessageSize.Small, true),
        [ControlMessageType.Pacing] = new(MessageDirection.Either, MessageSize.Small, true),
        [ControlMessageType.ResyncApplied] = new(MessageDirection.JoinerToHost, MessageSize.Small, true),
        [ControlMessageType.ResyncResume] = new(MessageDirection.HostToJoiner, MessageSize.Small, true),
        [ControlMessageType.SeatVacated] = new(MessageDirection.HostToJoiner, MessageSize.Small, true),
        [ControlMessageType.InputOutage] = new(MessageDirection.JoinerToHost, MessageSize.Small, true),
        [ControlMessageType.DivergenceReport] = new(MessageDirection.JoinerToHost, MessageSize.Small, true),
        [ControlMessageType.ExclusionMask] = new(MessageDirection.HostToJoiner, MessageSize.Small, true),
        [ControlMessageType.StateRequest] = new(MessageDirection.HostToJoiner, MessageSize.Small, true),
        [ControlMessageType.StateOffer] = new(MessageDirection.JoinerToHost, MessageSize.State, true),
    };

    /// <summary>Every registered type — what the completeness test compares the enum against.</summary>
    public static IEnumerable<ControlMessageType> All => Rules.Keys;

    /// <summary>False for a type nobody has decided the rules for. The test turns that into a
    /// failure; the callers below treat it as "assume nothing and cap small".</summary>
    public static bool IsRegistered(ControlMessageType type) => Rules.ContainsKey(type);

    public static MessageDirection Direction(ControlMessageType type) =>
        Rules.TryGetValue(type, out var rule) ? rule.Direction : MessageDirection.Either;

    /// <summary>Unregistered types are <see cref="MessageSize.Small"/>: an unknown message may not
    /// buy itself a 64 MiB allocation by being unknown.</summary>
    public static MessageSize Size(ControlMessageType type) =>
        Rules.TryGetValue(type, out var rule) ? rule.Size : MessageSize.Small;

    /// <summary>Whether the live session's reader loop handles this type, as opposed to the
    /// handshake consuming it synchronously. Pins the reader's arms against this table.</summary>
    public static bool ReaderDispatched(ControlMessageType type) =>
        Rules.TryGetValue(type, out var rule) && rule.ReaderDispatched;

    /// <summary>
    /// May a peer in this role RECEIVE this type? A joiner receives what the host sends and vice
    /// versa; <see cref="MessageDirection.Either"/> is accepted by both.
    ///
    /// Unregistered types are accepted, so an older peer sending something this build has not heard
    /// of is ignored by the dispatch rather than treated as an attack. The size cap is where an
    /// unknown type is actually constrained.
    /// </summary>
    public static bool Accepts(ControlMessageType type, bool weAreHost) =>
        Direction(type) switch
        {
            MessageDirection.HostToJoiner => !weAreHost,
            MessageDirection.JoinerToHost => weAreHost,
            _ => true,
        };

    /// <summary>May a peer in this role SEND this type? The mirror of <see cref="Accepts"/>.</summary>
    public static bool MaySend(ControlMessageType type, bool weAreHost) =>
        Direction(type) switch
        {
            MessageDirection.HostToJoiner => weAreHost,
            MessageDirection.JoinerToHost => !weAreHost,
            _ => true,
        };
}
