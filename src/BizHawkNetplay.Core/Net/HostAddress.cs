using System;
using System.Globalization;
using System.Net;

namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// Parses what a joiner types into the "Host IP" box. People paste what the host reads out —
    /// usually <c>1.2.3.4:47800</c>, since that's how a host's address is quoted everywhere else in
    /// the tool (the public-address readout, the log lines, every UDP endpoint) — so a bare IP and an
    /// <c>ip:port</c> pair both have to work, with the typed port winning over the Port box.
    ///
    /// IPv6 needs the bracket form to be unambiguous (<c>[::1]:47800</c>), because a bare <c>::1</c>
    /// is all colons; an unbracketed literal with more than one colon is therefore read as an address
    /// with no port rather than a mangled split.
    /// </summary>
    public static class HostAddress
    {
        /// <summary>
        /// Parse "1.2.3.4", "1.2.3.4:47800", "::1" or "[::1]:47800". <paramref name="defaultPort"/> is
        /// used when the text carries no port. Returns false (with <paramref name="address"/> null) on
        /// anything malformed — a bad address, a non-numeric or out-of-range port, or trailing junk.
        /// </summary>
        public static bool TryParse(string? text, int defaultPort, out IPAddress? address, out int port)
        {
            address = null;
            port = defaultPort;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text!.Trim();
            string hostPart;
            string? portPart = null;

            if (s[0] == '[')
            {
                // Bracketed IPv6, optionally "]:port".
                int close = s.IndexOf(']');
                if (close < 0) return false;
                hostPart = s.Substring(1, close - 1);
                string rest = s.Substring(close + 1);
                if (rest.Length > 0)
                {
                    if (rest[0] != ':') return false;   // "[::1]junk"
                    portPart = rest.Substring(1);
                }
            }
            else
            {
                int firstColon = s.IndexOf(':');
                int lastColon = s.LastIndexOf(':');
                if (firstColon >= 0 && firstColon == lastColon)
                {
                    hostPart = s.Substring(0, firstColon);
                    portPart = s.Substring(firstColon + 1);
                }
                else
                {
                    hostPart = s; // no colon at all, or a bare IPv6 literal — no port either way
                }
            }

            if (portPart != null)
            {
                // int.TryParse would accept "+80" and leading/trailing spaces; a port is digits only.
                if (portPart.Length == 0 || portPart.Length > 5) return false;
                foreach (char c in portPart)
                    if (c < '0' || c > '9') return false;
                if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out int p)) return false;
                if (p < 1 || p > 65535) return false;
                port = p;
            }

            if (!IPAddress.TryParse(hostPart, out var ip)) return false;
            address = ip;
            return true;
        }

        /// <summary>Render an address + port the way this parser accepts it back (bracketing IPv6).</summary>
        public static string Format(IPAddress address, int port)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"
                : $"{address}:{port}";
        }
    }
}
