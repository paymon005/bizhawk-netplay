using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// A forward this session added to a UPnP router, kept so it can be removed on the way out.
    /// </summary>
    public sealed class UpnpMapping
    {
        internal UpnpMapping(string controlUrl, string serviceType, int port, string lanIp)
        {
            ControlUrl = controlUrl;
            ServiceType = serviceType;
            Port = port;
            LanIp = lanIp;
        }

        public string ControlUrl { get; }
        public string ServiceType { get; }
        public int Port { get; }
        public string LanIp { get; }

        /// <summary>Best-effort removal of the TCP+UDP forwards this mapping added.</summary>
        public void Remove(TimeSpan timeout)
        {
            foreach (var proto in new[] { "TCP", "UDP" })
            {
                var args = new[]
                {
                    ("NewRemoteHost", ""),
                    ("NewExternalPort", Port.ToString()),
                    ("NewProtocol", proto),
                };
                try { UpnpPortMapper.Soap(ControlUrl, ServiceType, "DeletePortMapping", args, timeout); } catch { }
            }
        }
    }

    /// <summary>
    /// Best-effort UPnP-IGD port forwarding so a host behind a home router usually never has to open
    /// the router UI: SSDP-discover the gateway, find its WAN connection service, and SOAP
    /// AddPortMapping for TCP+UDP with a lease. Pure BCL, no extra dependency; when it returns null the
    /// caller falls back to manual port-forward instructions. Ported from the RemotePlay app.
    /// </summary>
    public static class UpnpPortMapper
    {
        private const int LeaseSeconds = 7200; // 2h; a crash that skips the clean unmap just lets it lapse
        private static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        };
        private static readonly string[] WanServices = { "WANIPConnection", "WANPPPConnection" };

        /// <summary>
        /// Ask a UPnP router to forward <paramref name="port"/> (TCP+UDP) to <paramref name="lanIp"/>.
        /// Returns the mapping (for later <see cref="UpnpMapping.Remove"/>) or null if no router accepted it.
        /// </summary>
        public static UpnpMapping? TryAddPortMapping(int port, string lanIp, string description, TimeSpan timeout)
        {
            foreach (var location in Discover(timeout))
            {
                string? controlUrl, serviceType;
                try { (controlUrl, serviceType) = FindControlUrl(location, timeout); }
                catch { continue; }
                if (controlUrl == null || serviceType == null) continue;

                bool ok = true;
                foreach (var proto in new[] { "TCP", "UDP" })
                {
                    var args = new[]
                    {
                        ("NewRemoteHost", ""),
                        ("NewExternalPort", port.ToString()),
                        ("NewProtocol", proto),
                        ("NewInternalPort", port.ToString()),
                        ("NewInternalClient", lanIp),
                        ("NewEnabled", "1"),
                        ("NewPortMappingDescription", description),
                        ("NewLeaseDuration", LeaseSeconds.ToString()),
                    };
                    try { Soap(controlUrl, serviceType, "AddPortMapping", args, timeout); }
                    catch { ok = false; break; }
                }
                if (ok) return new UpnpMapping(controlUrl, serviceType, port, lanIp);
            }
            return null;
        }

        /// <summary>The LAN address the OS would use for an internet connection (for NewInternalClient).</summary>
        public static string PrimaryLanIp()
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("1.1.1.1", 53); // no application data sent; just resolves a route
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
            catch { return "127.0.0.1"; }
        }

        private static List<string> Discover(TimeSpan timeout)
        {
            var locations = new List<string>();
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            sock.ReceiveTimeout = Math.Max(300, (int)timeout.TotalMilliseconds);
            var mcast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

            foreach (var st in SearchTargets)
            {
                var msg = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
                try { sock.SendTo(msg, mcast); } catch { }
            }

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var buf = new byte[4096];
            while (elapsed.Elapsed < timeout)
            {
                int n;
                try { EndPoint from = new IPEndPoint(IPAddress.Any, 0); n = sock.ReceiveFrom(buf, ref from); }
                catch { break; }
                var loc = ExtractHeader(Encoding.ASCII.GetString(buf, 0, n), "location");
                if (loc != null && !locations.Contains(loc)) locations.Add(loc);
            }
            return locations;
        }

        private static string? ExtractHeader(string httpText, string headerName)
        {
            foreach (var line in httpText.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line.Substring(0, colon).Trim().ToLowerInvariant() == headerName)
                    return line.Substring(colon + 1).Trim();
            }
            return null;
        }

        private static (string? controlUrl, string? serviceType) FindControlUrl(string location, TimeSpan timeout)
        {
            var doc = XDocument.Parse(HttpGet(location, timeout));
            string urlBase = location;
            foreach (var el in doc.Descendants())
                if (el.Name.LocalName == "URLBase" && !string.IsNullOrWhiteSpace(el.Value)) { urlBase = el.Value.Trim(); break; }

            foreach (var svc in doc.Descendants())
            {
                if (svc.Name.LocalName != "service") continue;
                string? serviceType = null, controlUrl = null;
                foreach (var child in svc.Elements())
                {
                    if (child.Name.LocalName == "serviceType") serviceType = child.Value.Trim();
                    else if (child.Name.LocalName == "controlURL") controlUrl = child.Value.Trim();
                }
                if (serviceType != null && controlUrl != null && ContainsWan(serviceType))
                    return (ResolveUrl(urlBase, controlUrl), serviceType);
            }
            return (null, null);
        }

        internal static byte[] Soap(string controlUrl, string serviceType, string action, (string key, string val)[] args, TimeSpan timeout)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
            sb.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
            sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(serviceType).Append("\">");
            foreach (var (k, v) in args)
                sb.Append('<').Append(k).Append('>').Append(Escape(v)).Append("</").Append(k).Append('>');
            sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
            var data = Encoding.UTF8.GetBytes(sb.ToString());

            var req = (HttpWebRequest)WebRequest.Create(controlUrl);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=\"utf-8\"";
            req.Headers["SOAPAction"] = "\"" + serviceType + "#" + action + "\"";
            req.Timeout = (int)timeout.TotalMilliseconds;
            using (var rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
            using var resp = (HttpWebResponse)req.GetResponse();
            using var ms = new MemoryStream();
            resp.GetResponseStream().CopyTo(ms);
            return ms.ToArray();
        }

        private static string HttpGet(string url, TimeSpan timeout)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = (int)timeout.TotalMilliseconds;
            req.UserAgent = "BizHawkNetplay";
            using var resp = (HttpWebResponse)req.GetResponse();
            using var sr = new StreamReader(resp.GetResponseStream());
            return sr.ReadToEnd();
        }

        private static bool ContainsWan(string serviceType)
        {
            foreach (var w in WanServices) if (serviceType.Contains(w)) return true;
            return false;
        }

        private static string ResolveUrl(string baseUrl, string rel)
        {
            if (Uri.TryCreate(rel, UriKind.Absolute, out var abs)) return abs.ToString();
            if (Uri.TryCreate(new Uri(baseUrl), rel, out var combined)) return combined.ToString();
            return rel;
        }

        private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
