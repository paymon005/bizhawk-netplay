namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Whether the host should hand recovery over to the machines that outvoted it.
///
/// <b>The problem.</b> Recovery distributes the host's state to everyone. When the host is the one
/// machine that diverged — local Lua, a cheat, a stray savestate load, a GPU-touched region — that
/// overwrites three correct machines with the one wrong one. v0.32.1 made the case visible;
/// <see cref="DesyncPartition.ChooseDonor"/> names who should have been asked instead.
///
/// <b>Why it is off by default, and this is the interesting part.</b> Fixing it is not free, and
/// the cost is not performance. Correctness requires the wrong machine to adopt the right state, so
/// if the host is wrong, the host imports — and a savestate is a trusted-input format all the way
/// into the core (see <see cref="StateImportTrust"/>). Until now no path existed by which a host
/// ran a peer's bytes; every state-bearing handler was joiner-side, and that was a real property
/// worth naming. Deferring to a majority gives it up.
///
/// The exposure needs a colluding majority rather than one bad peer — two of three players, or
/// three of four, all reporting a matching false checksum — which is a much higher bar than the
/// joiner-side case, where the single host suffices. But it is a trade rather than an improvement,
/// and a trade belongs to whoever is running the session.
///
/// So: off, and when it is on the host says what it is about to do and why. A host that leaves it
/// off keeps today's behaviour, which is loud about being wrong rather than quiet about it.
/// </summary>
public static class MajorityRecovery
{
    /// <summary>
    /// The port to ask for a state, or -1 to keep the host authoritative.
    ///
    /// Returns -1 whenever the host has not opted in, when the partition gives no clear majority
    /// (a tie is not one), and when the donor would be the host itself — the last of which cannot
    /// happen through <see cref="DesyncPartition.ChooseDonor"/> but is checked because the value
    /// ends up addressing a control message.
    /// </summary>
    public static int SelectDonor(DesyncPartition? partition, bool optedIn)
    {
        if (!optedIn || partition == null) return -1;
        int donor = partition.ChooseDonor();
        return donor == partition.HostPort ? -1 : donor;
    }

    /// <summary>What the host logs when it is about to defer — the state about to be distributed is
    /// not its own, which is a thing a reader of the log needs told.</summary>
    public static string Describe(DesyncPartition partition, int donor) =>
        $"deferring to the majority: {partition.HostGroupSize} of {partition.ReportCount} machines " +
        $"disagree with this one, so P{donor + 1}'s state becomes the session's rather than this " +
        "machine's. Everyone including the host adopts it. If this repeats, the cause is local to " +
        "the host — a Lua script, a cheat, a savestate load, or a differing core setting.";

    /// <summary>What the host logs when it is outvoted and has NOT opted in — the same evidence,
    /// the opposite action, and the setting that would change it.</summary>
    public static string DescribeDeclined(DesyncPartition partition) =>
        $"this machine is the ONLY one reporting its checksum — {partition.HostGroupSize} of " +
        $"{partition.ReportCount} players agree with each other and not with the host. The resync " +
        "about to run makes everyone adopt THIS machine's state, which on this evidence is the " +
        "wrong one. Suspect something local here: a Lua script, a cheat, a savestate load, or a " +
        "core setting that differs. If it repeats, host from another machine — or tick \"Defer to " +
        "the majority on a desync\" on the Diagnostics tab, which makes the host adopt their state " +
        "instead (it is off by default because accepting a peer's savestate is a trust decision).";
}
