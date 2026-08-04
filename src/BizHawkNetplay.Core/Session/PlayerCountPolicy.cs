namespace BizHawkNetplay.Core.Session;

/// <summary>
/// The difference between how many players the code permits and how many have been proven.
///
/// <see cref="HandshakeCodec.MaxPlayers"/> is 8, and it is a real bound: every array, every mesh
/// route, every pair key and every partition description is written for N and holds at 8. What has
/// never happened is a session with more than four people in it. A PSX with two multitaps, or a
/// Saturn, reaches six or eight seats from the core's own port count, and until now the tool went
/// there without remarking on it while the README said "2–4 players".
///
/// One of those two had to change, and the honest change is not to cap. Nothing about five players
/// is known to be broken — capping would remove a capability on suspicion. What was wrong is that
/// the claim and the behaviour disagreed, and that a host crossing the line got no signal.
///
/// So: the documented range is what has been run, the permitted range is what the code supports,
/// and crossing between them says so once, naming what actually gets harder rather than waving at
/// "untested". Cost is what scales — the mesh is every pair, so eight players is 28 edges against
/// four players' six.
/// </summary>
public static class PlayerCountPolicy
{
    /// <summary>The largest session this project has actually run and logged. Sessions at or below
    /// this are ordinary; above it the code is believed to work and has not been watched doing it.</summary>
    public const int VerifiedPlayers = 4;

    /// <summary>True when a session of this size goes beyond what has been exercised.</summary>
    public static bool IsBeyondVerified(int players) => players > VerifiedPlayers;

    /// <summary>
    /// What to tell a host who has asked for more seats than have ever been tested, or null when
    /// they have not.
    ///
    /// Deliberately specific about the three things that actually change, because "untested" on its
    /// own invites either ignoring the warning or abandoning a session that would have been fine.
    /// </summary>
    public static string? Advisory(int players)
    {
        if (!IsBeyondVerified(players)) return null;
        int edges = players * (players - 1) / 2;
        int verifiedEdges = VerifiedPlayers * (VerifiedPlayers - 1) / 2;
        return $"{players} players is beyond the {VerifiedPlayers} this has been tested at — it is " +
               "expected to work and has never been watched doing it. Three things get harder: the " +
               $"mesh is every pair, so that is {edges} direct connections to open instead of " +
               $"{verifiedEdges}; each machine sends its input to {players - 1} peers every frame " +
               "instead of " + $"{VerifiedPlayers - 1}; and if any of those connections fail to open, " +
               "the host carries them, so its upload does the work. Rollback also predicts more " +
               "seats, so a repair costs more. If it stutters, try lockstep or fewer players.";
    }
}
