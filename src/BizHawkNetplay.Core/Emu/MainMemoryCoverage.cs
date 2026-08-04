using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// Whether hashing the core's <c>MainMemory</c> actually covers the machine the session is
/// synchronizing — asked because on some cores it provably does not.
///
/// <b>The failure this exists to refuse.</b> BizHawk's <c>MemoryDomainList.MainMemory</c> falls
/// back to the FIRST registered domain when a core does not nominate one. The link cores —
/// GBHawkLink, GBHawkLink3x, GBHawkLink4x, GGHawkLink — emulate two to four whole Game Boys and
/// register <c>Main RAM A/B/C/D</c> (or <c>L</c>/<c>R</c>) without nominating one, so the desync
/// checksum reads machine A and nothing else. Divergence confined to P2's, P3's or P4's machine is
/// then completely invisible: every checksum agrees while the session is already broken, and the
/// resync that should have fired never does. On a 4-player link cable that is three quarters of
/// the emulated state unwatched.
///
/// Bucketing memory does not help — it slices the same one domain more finely. The honest fix is a
/// checksum over every state-bearing domain, which is a wire change; until then a session on such
/// a core is refused rather than run with detection that cannot see most of the machine.
///
/// <b>Detected by shape, not by a core list.</b> A machine-suffixed name is one whose last
/// whitespace-separated token is a single letter, with at least one sibling sharing the prefix and
/// differing only in that letter. That is exactly the convention these cores use, and it catches a
/// link core this project has never heard of — which matters, because any statable core with two
/// contiguous controller layouts is admitted today.
/// </summary>
public static class MainMemoryCoverage
{
    /// <summary>
    /// True when <paramref name="mainMemoryName"/> is one machine's slice of a multi-machine core,
    /// leaving its siblings unhashed. False for every ordinary core (no siblings, so nothing is
    /// being missed) and when the name carries no machine suffix at all.
    /// </summary>
    public static bool IsSingleMachineSlice(string? mainMemoryName, IEnumerable<string>? domainNames)
    {
        if (!TrySplitMachineSuffix(mainMemoryName, out string prefix, out char machine)) return false;
        if (domainNames == null) return false;
        foreach (var name in domainNames)
        {
            if (!TrySplitMachineSuffix(name, out string otherPrefix, out char otherMachine)) continue;
            if (otherMachine != machine
                && string.Equals(prefix, otherPrefix, StringComparison.Ordinal))
                return true;   // a sibling machine exists and is not being hashed
        }
        return false;
    }

    /// <summary>The sibling machines whose memory a single-machine checksum is missing, for the
    /// refusal message — naming them is what makes the refusal actionable rather than cryptic.</summary>
    public static List<string> SiblingMachines(string? mainMemoryName, IEnumerable<string>? domainNames)
    {
        var siblings = new List<string>();
        if (!TrySplitMachineSuffix(mainMemoryName, out string prefix, out char machine)
            || domainNames == null) return siblings;
        foreach (var name in domainNames)
        {
            if (!TrySplitMachineSuffix(name, out string otherPrefix, out char otherMachine)) continue;
            if (otherMachine == machine
                || !string.Equals(prefix, otherPrefix, StringComparison.Ordinal)) continue;
            if (!siblings.Contains(name)) siblings.Add(name);
        }
        return siblings;
    }

    /// <summary>"Main RAM A" -> ("Main RAM", 'A'). False when the trailing token is not a single
    /// letter, which is every ordinary domain name ("RDRAM", "WRAM", "Main RAM", "System Bus").</summary>
    private static bool TrySplitMachineSuffix(string? name, out string prefix, out char machine)
    {
        prefix = "";
        machine = '\0';
        if (string.IsNullOrEmpty(name)) return false;
        int space = name!.LastIndexOf(' ');
        if (space <= 0 || space != name.Length - 2) return false;
        char last = name[name.Length - 1];
        if (!char.IsLetter(last)) return false;
        prefix = name.Substring(0, space);
        machine = char.ToUpperInvariant(last);
        return true;
    }
}
