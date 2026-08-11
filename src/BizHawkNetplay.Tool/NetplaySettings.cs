using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BizHawkNetplay.Tool;

/// <summary>
/// Small persisted UI preferences for the netplay tool: the toggles worth remembering between
/// sessions (UPnP, port, manual/automatic delay, netcode) plus a most-recently-used list of join IPs surfaced as a
/// dropdown on the Host IP box. Stored as a tiny <c>key=value</c> file under <c>%AppData%</c> rather
/// than beside the DLL, which a redeploy overwrites. Loading and saving are best-effort — a missing
/// or corrupt file just yields defaults, never an error that could disrupt play.
/// </summary>
internal sealed class NetplaySettings
{
    private const int MaxRecentIps = 10;

    public bool Upnp = true;
    public int Port = 47800;
    public int Players = 2;      // host: how many controller ports to fill
    public int Delay = 1;        // fixed delay, or the manual floor when lobby auto-selection is enabled
    public bool AutoDelay = true; // host measures lobby RTT and raises Delay before GO
    public int AutoDelayMax = 8; // cap automatic increases without overriding an explicit peer request
    public int Netcode = 0;     // index into the netcode dropdown (Automatic/Rollback/Lockstep)
    public int InputSource = 0; // index into the "My controls" dropdown (P1..P4, or Assigned port)
    // Host: fold every seat onto controller 1, for alternating games where the players take turns on
    // one joystick. Off by default — it is wrong for anything two-player-simultaneous.
    public bool SharedControls = false;
    // Frame periods one tick may run and one rollback repair may spend. 2 is what shipped through
    // v0.38.x; 3 is what a heavy core needs to reach a usable prediction depth — see
    // NetplayToolForm.RepairBudgetFrames for the measured arithmetic and the trade.
    public int FramePeriodsPerTick = 2;
    public readonly List<string> RecentIps = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BizHawkNetplay", "settings.txt");

    public static NetplaySettings Load()
    {
        var s = new NetplaySettings();
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return s;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "upnp": s.Upnp = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase); break;
                    case "port": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) s.Port = p; break;
                    case "players": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pl)) s.Players = pl; break;
                    case "delay": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) s.Delay = d; break;
                    case "autodelay": s.AutoDelay = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase); break;
                    case "autodelaymax": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var adm)) s.AutoDelayMax = adm; break;
                    case "netcode": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) s.Netcode = n; break;
                    case "inputsrc": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ins)) s.InputSource = ins; break;
                    case "sharedctl": s.SharedControls = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase); break;
                    case "frameperiods": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fp)) s.FramePeriodsPerTick = fp; break;
                    case "ip": if (val.Length > 0 && s.RecentIps.Count < MaxRecentIps && !s.RecentIps.Contains(val)) s.RecentIps.Add(val); break;
                }
            }
        }
        catch { /* defaults on any read/parse trouble */ }
        return s;
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>
            {
                "upnp=" + (Upnp ? "1" : "0"),
                "port=" + Port.ToString(CultureInfo.InvariantCulture),
                "players=" + Players.ToString(CultureInfo.InvariantCulture),
                "delay=" + Delay.ToString(CultureInfo.InvariantCulture),
                "autodelay=" + (AutoDelay ? "1" : "0"),
                "autodelaymax=" + AutoDelayMax.ToString(CultureInfo.InvariantCulture),
                "netcode=" + Netcode.ToString(CultureInfo.InvariantCulture),
                "inputsrc=" + InputSource.ToString(CultureInfo.InvariantCulture),
                "sharedctl=" + (SharedControls ? "1" : "0"),
                "frameperiods=" + FramePeriodsPerTick.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var ip in RecentIps) lines.Add("ip=" + ip);
            File.WriteAllLines(path, lines);
        }
        catch { /* best effort — never disrupt a session over a settings write */ }
    }

    /// <summary>Push an IP to the front of the recent list (deduped, capped), newest first.</summary>
    public void RecordIp(string ip)
    {
        ip = (ip ?? "").Trim();
        if (ip.Length == 0) return;
        RecentIps.RemoveAll(x => string.Equals(x, ip, StringComparison.OrdinalIgnoreCase));
        RecentIps.Insert(0, ip);
        while (RecentIps.Count > MaxRecentIps) RecentIps.RemoveAt(RecentIps.Count - 1);
    }
}
