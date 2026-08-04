using System.Collections.Generic;
using System.Text;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Who reported what, when a checksum disagreed — the fact the verdict alone threw away.
///
/// The host is authoritative for recovery: it captures its own state and every peer adopts it.
/// That is a policy, not evidence, and it has a failure mode worth naming. In a four-player
/// session P2, P3 and P4 can agree on one hash while the host alone reports another; the host then
/// overwrites three mutually agreeing machines with the one state that is probably wrong. Local
/// Lua, a cheat, a stray state load, a GPU-touched region — any of them can put the host in the
/// minority.
///
/// This does not change the policy. It makes the case where the policy is wrong VISIBLE, so a log
/// says "the host was the only machine reporting its value" instead of "DESYNC at frame N" — the
/// difference between a report someone can act on and one they cannot. Choosing a different
/// authority needs a majority-reconstruction protocol and belongs in a wire change; being able to
/// see that it is needed does not.
/// </summary>
public sealed class DesyncPartition
{
    private DesyncPartition(int frame, IReadOnlyList<KeyValuePair<uint, List<int>>> groups, int hostPort)
    {
        Frame = frame;
        Groups = groups;
        HostPort = hostPort;
    }

    /// <summary>The checksum boundary that disagreed.</summary>
    public int Frame { get; }

    /// <summary>Reported hash → the ports that reported it, largest group first.</summary>
    public IReadOnlyList<KeyValuePair<uint, List<int>>> Groups { get; }

    /// <summary>The host's own seat, always 0 — named rather than assumed at the call sites.</summary>
    public int HostPort { get; }

    /// <summary>How many machines agreed with the host, host included.</summary>
    public int HostGroupSize
    {
        get
        {
            foreach (var group in Groups)
                if (group.Value.Contains(HostPort)) return group.Value.Count;
            return 0;
        }
    }

    /// <summary>Machines that reported this boundary at all.</summary>
    public int ReportCount
    {
        get
        {
            int total = 0;
            foreach (var group in Groups) total += group.Value.Count;
            return total;
        }
    }

    /// <summary>
    /// True when the host is NOT in the largest group — the case where recovery is about to
    /// overwrite a majority with the minority's state. A tie is not a minority: with two groups of
    /// two there is no majority to be outside of, and calling that "the host is wrong" would be
    /// inventing a verdict the evidence does not support.
    /// </summary>
    public bool HostIsOutvoted
    {
        get
        {
            int host = HostGroupSize;
            if (host == 0) return false;
            foreach (var group in Groups)
                if (!group.Value.Contains(HostPort) && group.Value.Count > host) return true;
            return false;
        }
    }

    /// <summary>One line naming the split, for the session log.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Groups.Count; i++)
        {
            if (i > 0) sb.Append(" vs ");
            var ports = Groups[i].Value;
            for (int p = 0; p < ports.Count; p++)
            {
                if (p > 0) sb.Append('+');
                sb.Append('P').Append(ports[p] + 1);
            }
            sb.Append('=').Append(Groups[i].Key.ToString("X8"));
        }
        return sb.ToString();
    }

    /// <summary>Group one frame's reports by reported hash, largest group first (ties keep the
    /// lowest port first, so two runs of the same split describe it identically).</summary>
    public static DesyncPartition FromReports(int frame, IReadOnlyDictionary<int, uint> reports,
        int hostPort = 0)
    {
        var byHash = new Dictionary<uint, List<int>>();
        foreach (var report in reports)
        {
            if (!byHash.TryGetValue(report.Value, out var ports))
                byHash[report.Value] = ports = new List<int>();
            ports.Add(report.Key);
        }
        var groups = new List<KeyValuePair<uint, List<int>>>();
        foreach (var group in byHash)
        {
            group.Value.Sort();
            groups.Add(group);
        }
        groups.Sort((a, b) =>
        {
            int bySize = b.Value.Count.CompareTo(a.Value.Count);
            return bySize != 0 ? bySize : a.Value[0].CompareTo(b.Value[0]);
        });
        return new DesyncPartition(frame, groups, hostPort);
    }
}
