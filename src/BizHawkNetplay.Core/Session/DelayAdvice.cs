namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// What to tell a player to do about input delay, and why the lobby did not already do it.
    ///
    /// This is phrasing, not policy — <see cref="LobbyDelayPolicy"/> decides the number. It lives in
    /// Core anyway because it has been wrong twice in ways a test would have caught instantly. It once
    /// named a control that could not change the outcome (Auto max, while Auto from ping was
    /// unticked), through a session that stalled 30-70% of its ticks start to finish. Then, after the
    /// host gained a live Apply, every branch went on telling players to reconnect — advising them to
    /// end a session that a button press now fixes, in the single most-read line the log produces when
    /// a link is bad. Both survived review because advice strings assembled inside a UI callback are
    /// not reachable by anything that runs.
    /// </summary>
    public static class DelayAdvice
    {
        /// <summary>
        /// The one action that changes a running session's delay, addressed to whoever can take it.
        /// A joiner cannot touch the control, so it is told what to ask for.
        /// </summary>
        public static string ApplyNow(bool isHost, int suggested) => isHost
            ? $"Set Input delay to {suggested} and press \"Apply changes\" — everyone stays connected " +
              "through a brief pause."
            : $"Ask the host to set Input delay to {suggested} and press \"Apply changes\" — everyone " +
              "stays connected through a brief pause, nobody rejoins.";

        /// <summary>
        /// <see cref="ApplyNow"/> plus, for a host, why the lobby started lower than this. That tail is
        /// about the NEXT session — Auto from ping and Auto max are lobby-only and disabled during play
        /// — so it is worth saying and worth keeping separate from the action.
        ///
        /// Only ever attach this to advice about being UNDER-delayed. The tail explains a number that
        /// came out too low; on the over-delayed warning it reads as nonsense.
        /// </summary>
        public static string Remedy(bool isHost, int suggested, bool autoFromPing, int autoMax)
        {
            string apply = ApplyNow(isHost, suggested);

            // A joiner cannot see the host's auto-delay settings, so the rest would be noise to it.
            if (!isHost) return apply;

            if (!autoFromPing)
                return $"{apply} \"Auto from ping\" is off, which is why nothing measured this for you " +
                    "at the start; tick it and the next session will.";

            if (suggested > autoMax)
                return $"{apply} \"Auto from ping\" is on but capped at {autoMax}, so it could never " +
                    $"have chosen {suggested} — raise Auto max for the next session.";

            // Auto was on and had room: the lobby measurement simply caught the link at a better
            // moment than the session went on to see.
            return $"{apply} \"Auto from ping\" measured a faster link at connect than this session has " +
                "seen, which is why it started lower.";
        }
    }
}
