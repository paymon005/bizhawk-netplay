using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BizHawkNetplay.Tool
{
    /// <summary>
    /// Small persisted UI preferences for the netplay tool: the toggles worth remembering between
    /// sessions (UPnP, port, delay, netcode) plus a most-recently-used list of join IPs surfaced as a
    /// dropdown on the Host IP box. Stored as a tiny <c>key=value</c> file under <c>%AppData%</c> rather
    /// than beside the DLL, which a redeploy overwrites. Loading and saving are best-effort — a missing
    /// or corrupt file just yields defaults, never an error that could disrupt play.
    /// </summary>
    internal sealed class NetplaySettings
    {
        private const int MaxRecentIps = 10;

        public bool Upnp = true;
        public int Port = 47800;
        public int Delay = 2;
        public int Netcode = 0; // index into the netcode dropdown (Automatic/Rollback/Lockstep)
        public readonly List<string> RecentIps = new List<string>();

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
                        case "delay": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) s.Delay = d; break;
                        case "netcode": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) s.Netcode = n; break;
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
                    "delay=" + Delay.ToString(CultureInfo.InvariantCulture),
                    "netcode=" + Netcode.ToString(CultureInfo.InvariantCulture),
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
}
