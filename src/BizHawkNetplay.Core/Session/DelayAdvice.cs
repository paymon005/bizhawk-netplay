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
        ///
        /// <paramref name="maxSelectable"/> is the ceiling the control actually offers. Past it there
        /// is no action to name: the stalling hint asks for <c>sessionDelay + 1</c> and the lobby's
        /// recommendation is clamped to a protocol limit three times the UI's, so both could tell a
        /// host at the maximum to "set Input delay to 21" — a number the box will not accept, in a
        /// message whose whole job is to be followed literally. At the ceiling the honest answer is
        /// that delay is spent and the remaining causes are elsewhere.
        /// </summary>
        public static string ApplyNow(bool isHost, int suggested, int maxSelectable)
        {
            if (suggested > maxSelectable)
                return $"Input delay is already at its maximum of {maxSelectable}, so more of it isn't " +
                    "available — what's left is a link too slow or too jittery for this netcode, or a " +
                    "peer that can't hold full speed. Rollback absorbs far more latency than lockstep " +
                    "if you aren't already on it.";

            return isHost
                ? $"Set Input delay to {suggested} and press \"Apply changes\" — everyone stays " +
                  "connected through a brief pause."
                : $"Ask the host to set Input delay to {suggested} and press \"Apply changes\" — " +
                  "everyone stays connected through a brief pause, nobody rejoins.";
        }

        /// <summary>
        /// <see cref="ApplyNow"/> plus, for a host, why the lobby started lower than this. That tail is
        /// about the NEXT session — Auto from ping and Auto max are lobby-only and disabled during play
        /// — so it is worth saying and worth keeping separate from the action.
        ///
        /// Only ever attach this to advice about being UNDER-delayed. The tail explains a number that
        /// came out too low; on the over-delayed warning it reads as nonsense.
        /// </summary>
        public static string Remedy(bool isHost, int suggested, bool autoFromPing, int autoMax,
            int maxSelectable)
        {
            string apply = ApplyNow(isHost, suggested, maxSelectable);

            // Past the ceiling the tail contradicts the message: there is no point explaining why the
            // lobby didn't choose a number that the control could never have reached either.
            if (suggested > maxSelectable) return apply;

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
