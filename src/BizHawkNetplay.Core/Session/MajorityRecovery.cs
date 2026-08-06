namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Whether the host should hand recovery over to the machines that outvoted it.
///
/// <b>The problem.</b> Recovery distributes the host's state to everyone. When the host is the one
/// machine that diverged — local Lua, a cheat, a stray savestate load, a GPU-touched region — that
/// overwrites three correct machines with the one wrong one. v0.32.1 made the case visible;
/// <see cref="DesyncPartition.ChooseDonor"/> names who should have been asked instead.
///
/// <b>What it costs, which is not performance.</b> Correctness requires the wrong machine to adopt
/// the right state, so if the host is wrong, the host imports — and a savestate is a trusted-input
/// format all the way into the core (see <see cref="StateImportTrust"/>). Before this existed no
/// path let a host run a peer's bytes; every state-bearing handler was joiner-side, and that was a
/// real property. Deferring to a majority gives it up.
///
/// <b>On by default since v0.36.0, and the reasoning changed rather than the code.</b> It shipped
/// opt-in, on the argument that a trade belongs to whoever is running the session. Weigh the two
/// sides by what each requires and that reads differently: the exposure needs a colluding MAJORITY —
/// two of three players, or three of four, all reporting a matching false checksum — while the
/// failure it prevents needs nobody at all. A host with a Lua script running, or one that touched a
/// savestate hotkey, overwrites every player who was right, and that is a Tuesday rather than an
/// attack.
///
/// So: on, with the host saying what it is about to do and why, and a checkbox for a host that is
/// playing with strangers. Unticked, the behaviour is the old one — loud about being wrong rather
/// than quiet about it.
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

    /// <summary>
    /// What the host logs when it is outvoted and deferring is switched OFF — the same evidence, the
    /// opposite action, and the setting that would change it.
    ///
    /// Since deferring is on by default, reaching this means someone deliberately turned it off, so
    /// the message points at the setting they changed rather than selling them a feature.
    /// </summary>
    public static string DescribeDeclined(DesyncPartition partition) =>
        $"this machine is the ONLY one reporting its checksum — {partition.HostGroupSize} of " +
        $"{partition.ReportCount} players agree with each other and not with the host. The resync " +
        "about to run makes everyone adopt THIS machine's state, which on this evidence is the " +
        "wrong one. Suspect something local here: a Lua script, a cheat, a savestate load, or a " +
        "core setting that differs. \"Defer to the majority on a desync\" on the Diagnostics tab " +
        "would have made the host adopt their state instead; it is on by default and is currently " +
        "unticked on this machine. Re-tick it, or host from another machine.";
}
