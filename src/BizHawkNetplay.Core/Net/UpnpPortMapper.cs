using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace BizHawkNetplay.Core.Net;

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
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
    ];
    private static readonly string[] WanServices = ["WANIPConnection", "WANPPPConnection"];

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

    /// <summary>
    /// One header out of an SSDP reply. Internal because the input is a broadcast answer from
    /// anything on the LAN, which makes it worth driving with the shapes a router would not send.
    /// </summary>
    internal static string? ExtractHeader(string httpText, string headerName)
    {
        if (httpText == null) return null;
        foreach (var line in httpText.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line.Substring(0, colon).Trim().ToLowerInvariant() == headerName)
                return line.Substring(colon + 1).Trim();
        }
        return null;
    }

    private static (string? controlUrl, string? serviceType) FindControlUrl(string location, TimeSpan timeout) =>
        ParseControlUrl(HttpGet(location, timeout), location);

    /// <summary>
    /// The WAN connection service's control URL out of a device description, or (null, null).
    ///
    /// Split from the fetch so it can be driven with what a hostile or merely broken device sends.
    /// The XML is read with DTD processing off and no resolver: the responder is whatever answered
    /// a broadcast, and the default reader would happily expand an entity bomb or fetch a URL an
    /// unknown device chose, on the thread the host's lobby runs on. Anything unparseable returns
    /// (null, null) rather than throwing — the caller's next device is a better answer than an
    /// exception, and returning it here means every caller gets that behaviour rather than the one
    /// that remembered to wrap the call.
    /// </summary>
    internal static (string? controlUrl, string? serviceType) ParseControlUrl(string? xml, string location)
    {
        if (string.IsNullOrEmpty(xml)) return (null, null);
        XDocument doc;
        try
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
            };
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml!), settings);
            doc = XDocument.Load(reader);
        }
        catch { return (null, null); }

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
            // An EMPTY controlURL is not a relative one. Resolved against the base it comes back as
            // the description URL, and the mapper would then POST AddPortMapping to the address it
            // had just fetched the description from and report the forward as installed. The player
            // is told UPnP worked and nobody can reach them.
            if (serviceType == null || string.IsNullOrEmpty(controlUrl) || !ContainsWan(serviceType))
                continue;
            // Keep looking rather than giving up: a description can list several WAN services, and
            // one with an unusable control URL must not hide the one after it.
            var resolved = ResolveUrl(urlBase, controlUrl);
            if (resolved != null) return (resolved, serviceType);
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
        req.ReadWriteTimeout = (int)timeout.TotalMilliseconds;
        using (var rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
        using var resp = (HttpWebResponse)req.GetResponse();
        return ReadBounded(resp);
    }

    private static string HttpGet(string url, TimeSpan timeout)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = (int)timeout.TotalMilliseconds;
        req.ReadWriteTimeout = (int)timeout.TotalMilliseconds;
        req.UserAgent = "BizHawkNetplay";
        using var resp = (HttpWebResponse)req.GetResponse();
        return Encoding.UTF8.GetString(ReadBounded(resp));
    }

    /// <summary>
    /// Largest device description or SOAP reply worth reading. Real ones are a few kilobytes; the
    /// cap exists because the responder is whatever answered an SSDP broadcast on the local network,
    /// which is not necessarily a router and is not necessarily well behaved.
    /// </summary>
    private const int MaxResponseBytes = 1 << 20;   // 1 MiB

    /// <summary>
    /// Read a response body with a ceiling.
    ///
    /// Timeout alone was not enough: HttpWebRequest.Timeout covers the connect and the response
    /// HEADERS, and the body copy below was governed by ReadWriteTimeout — which defaults to five
    /// minutes. A device that accepted the connection and then trickled bytes held the host's lobby
    /// accept loop for that long, per call, while joiners sat in the backlog with no explanation.
    /// </summary>
    private static byte[] ReadBounded(HttpWebResponse resp)
    {
        using var ms = new MemoryStream();
        var stream = resp.GetResponseStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > MaxResponseBytes)
                throw new InvalidOperationException(
                    $"UPnP response exceeded {MaxResponseBytes} bytes; ignoring this device.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static bool ContainsWan(string serviceType)
    {
        foreach (var w in WanServices) if (serviceType.Contains(w)) return true;
        return false;
    }

    /// <summary>
    /// Resolve a device's control URL against the description's base, and refuse anything that is
    /// not HTTP.
    ///
    /// Both halves are the device's text. A relative <c>controlURL</c> against an <c>http://</c>
    /// location is what every real router sends; the scheme check is for the ones that do not,
    /// because the result is handed straight to <see cref="WebRequest.Create"/> and posted to —
    /// and <c>file://</c> is a scheme WebRequest will happily accept from something that answered
    /// a broadcast. A base URL that will not parse leaves the relative text alone rather than
    /// throwing out of the caller's loop.
    /// </summary>
    internal static string? ResolveUrl(string baseUrl, string rel)
    {
        if (Uri.TryCreate(rel, UriKind.Absolute, out var abs)) return Http(abs);
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var base_)
            && Uri.TryCreate(base_, rel, out var combined)) return Http(combined);
        return null;
    }

    private static string? Http(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
