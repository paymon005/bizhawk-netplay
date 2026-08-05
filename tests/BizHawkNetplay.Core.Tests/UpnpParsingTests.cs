using System;
using System.Text;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The two parsers UPnP discovery runs on data anything on the LAN can send.
///
/// <c>UpnpPortMapper</c> broadcasts to 239.255.255.250 and then parses whatever answers — an SSDP
/// reply, then an XML device description fetched from a URL that reply named. Neither had a test.
/// The result of the second is POSTed to, so it is not only a parse: it decides where the host
/// sends a SOAP request from its lobby thread.
///
/// Both are driven here with what a router sends and with what one would not.
/// </summary>
public class UpnpParsingTests
{
    private const string Location = "http://192.168.1.1:5000/rootDesc.xml";

    private static string Description(string serviceType, string controlUrl, string? urlBase = null) =>
        "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\">"
        + (urlBase == null ? "" : $"<URLBase>{urlBase}</URLBase>")
        + "<device><serviceList><service>"
        + $"<serviceType>{serviceType}</serviceType>"
        + $"<controlURL>{controlUrl}</controlURL>"
        + "</service></serviceList></device></root>";

    // ---------------------------------------------------------------- SSDP headers

    [Fact]
    public void TheLocationHeaderIsFoundHoweverTheDeviceCasedIt()
    {
        var reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\n"
            + "LOCATION: " + Location + "\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
        Assert.Equal(Location, UpnpPortMapper.ExtractHeader(reply, "location"));

        var lowercase = reply.Replace("LOCATION:", "location:");
        Assert.Equal(Location, UpnpPortMapper.ExtractHeader(lowercase, "location"));

        var spaced = reply.Replace("LOCATION:", "  Location  :");
        Assert.Equal(Location, UpnpPortMapper.ExtractHeader(spaced, "location"));
    }

    /// <summary>
    /// Replies that name no usable location, including the ones whose shape could trip an index.
    /// A header parse that throws here takes out discovery for every device, not just this one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    [InlineData(":")]
    [InlineData(":no-name")]
    [InlineData("HTTP/1.1 200 OK")]
    [InlineData("LOCATIONX: http://x/")]           // prefix, not the header
    [InlineData("NOT-LOCATION: http://x/")]
    [InlineData("\n\n:\n:\n\n")]
    public void AReplyWithNoLocationYieldsNothingRatherThanThrowing(string reply)
    {
        Assert.Null(UpnpPortMapper.ExtractHeader(reply, "location"));
    }

    [Fact]
    public void AnEmptyLocationValueComesBackEmptyRatherThanAsAUrl()
    {
        // Worth pinning because "" then reaches WebRequest.Create; the caller must see a value it
        // can reject, and the parse must not invent one.
        Assert.Equal("", UpnpPortMapper.ExtractHeader("LOCATION:\r\n", "location"));
    }

    [Fact]
    public void ARandomlyCorruptedReplyNeverThrows()
    {
        const string alphabet = "abcLOCATION: \r\n:=/.\0\uFFFD";
        var rng = new Random(0x5D5D);
        for (int trial = 0; trial < 500; trial++)
        {
            var chars = new char[rng.Next(0, 120)];
            for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
            UpnpPortMapper.ExtractHeader(new string(chars), "location");
        }
    }

    // ---------------------------------------------------------------- device description

    [Fact]
    public void ARelativeControlUrlResolvesAgainstTheLocationItCameFrom()
    {
        var (url, type) = UpnpPortMapper.ParseControlUrl(
            Description("urn:schemas-upnp-org:service:WANIPConnection:1", "/upnp/control/wanipc"),
            Location);
        Assert.Equal("http://192.168.1.1:5000/upnp/control/wanipc", url);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", type);
    }

    [Fact]
    public void AUrlBaseElementOverridesTheLocation()
    {
        var (url, _) = UpnpPortMapper.ParseControlUrl(
            Description("urn:schemas-upnp-org:service:WANPPPConnection:1", "ctl/IPConn",
                urlBase: "http://192.168.1.1:2555/"),
            Location);
        Assert.Equal("http://192.168.1.1:2555/ctl/IPConn", url);
    }

    [Fact]
    public void ADescriptionWithNoWanServiceIsDeclined()
    {
        var (url, type) = UpnpPortMapper.ParseControlUrl(
            Description("urn:schemas-upnp-org:service:Layer3Forwarding:1", "/ctl"), Location);
        Assert.Null(url);
        Assert.Null(type);
    }

    /// <summary>
    /// A control URL whose scheme is not HTTP is refused.
    ///
    /// The resolved string goes straight to <c>WebRequest.Create</c> and is POSTed to with a SOAP
    /// body. <c>file://</c> is a scheme WebRequest accepts, and the device supplying it is whatever
    /// answered a broadcast — so the scheme is checked where the value is produced rather than
    /// trusted because a real router would never send it.
    /// </summary>
    [Theory]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("ftp://192.168.1.1/x")]
    [InlineData("\\\\attacker\\share\\x")]
    public void AControlUrlThatIsNotHttpIsRefused(string hostile)
    {
        var (url, _) = UpnpPortMapper.ParseControlUrl(
            Description("urn:schemas-upnp-org:service:WANIPConnection:1", hostile), Location);
        Assert.Null(url);
    }

    [Fact]
    public void AUsableServiceAfterAnUnusableOneIsStillFound()
    {
        var xml = "<?xml version=\"1.0\"?><root><device><serviceList>"
            + "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>"
            + "<controlURL>file:///nope</controlURL></service>"
            + "<service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>"
            + "<controlURL>/good</controlURL></service>"
            + "</serviceList></device></root>";
        var (url, type) = UpnpPortMapper.ParseControlUrl(xml, Location);
        Assert.Equal("http://192.168.1.1:5000/good", url);
        Assert.Equal("urn:schemas-upnp-org:service:WANPPPConnection:1", type);
    }

    /// <summary>
    /// An entity-expansion bomb from a device that answered a broadcast.
    ///
    /// A guard rather than a fixed defect: <c>XDocument.Parse</c> already declined to expand this
    /// one. It is pinned because the reader runs on the thread discovering a gateway while the
    /// lobby waits, so if anyone ever sets <c>DtdProcessing.Parse</c> to accommodate a router that
    /// sends a doctype, the cost is a hang the player sees as a frozen tool — and that change
    /// would look harmless in review.
    /// </summary>
    [Fact]
    public void AnEntityBombIsRefusedRatherThanExpanded()
    {
        var bomb = new StringBuilder();
        bomb.Append("<?xml version=\"1.0\"?><!DOCTYPE root [");
        bomb.Append("<!ENTITY a \"").Append(new string('x', 1000)).Append("\">");
        for (char c = 'b'; c <= 'h'; c++)
        {
            bomb.Append("<!ENTITY ").Append(c).Append(" \"");
            for (int i = 0; i < 10; i++) bomb.Append('&').Append((char)(c - 1)).Append(';');
            bomb.Append("\">");
        }
        bomb.Append("]><root><device><serviceList><service>");
        bomb.Append("<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>");
        bomb.Append("<controlURL>&h;</controlURL>");
        bomb.Append("</service></serviceList></device></root>");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var (url, type) = UpnpPortMapper.ParseControlUrl(bomb.ToString(), Location);
        started.Stop();

        Assert.Null(url);
        Assert.Null(type);
        Assert.True(started.ElapsedMilliseconds < 2000,
            $"the bomb took {started.ElapsedMilliseconds}ms — it was expanded, not refused");
    }

    /// <summary>
    /// A description carrying a doctype is declined, rather than parsed with the entity quietly
    /// gone.
    ///
    /// This one was a real wrong answer, and not the one the shape suggests. The external entity is
    /// never fetched — the platform's default resolver already declines that — but it expanded to
    /// nothing, so <c>&lt;controlURL&gt;&amp;x;&lt;/controlURL&gt;</c> became an EMPTY control URL,
    /// which resolved against the base to the device-description URL itself. The mapper then
    /// accepted the device and POSTed <c>AddPortMapping</c> to the address it had just fetched the
    /// description from, and reported the forward as installed. Refusing the document is the honest
    /// answer: the caller moves to the next device instead of believing a mapping that is not there.
    /// </summary>
    [Fact]
    public void ADescriptionWithADoctypeIsDeclinedRatherThanSilentlyEmptied()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE root [<!ENTITY x SYSTEM \"http://127.0.0.1:9/x\">]>"
            + "<root><device><serviceList><service>"
            + "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>"
            + "<controlURL>&x;</controlURL></service></serviceList></device></root>";
        var (url, type) = UpnpPortMapper.ParseControlUrl(xxe, Location);
        Assert.Null(url);
        Assert.Null(type);
    }

    /// <summary>
    /// The same wrong answer without any DTD at all: a control URL that is empty or whitespace must
    /// not resolve to the description URL and be POSTed to as though it were a control endpoint.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyControlUrlIsNotTheDescriptionUrl(string empty)
    {
        var (url, _) = UpnpPortMapper.ParseControlUrl(
            Description("urn:schemas-upnp-org:service:WANIPConnection:1", empty), Location);
        Assert.NotEqual(Location, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<root>")]
    [InlineData("<root></wrong>")]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("\uFFFE\uFFFF")]
    public void UnparseableDescriptionsAreDeclinedRatherThanThrown(string? xml)
    {
        var (url, type) = UpnpPortMapper.ParseControlUrl(xml, Location);
        Assert.Null(url);
        Assert.Null(type);
    }

    /// <summary>A URLBase the device made up must not throw its way out of the scan — the next
    /// device on the list is the answer, and only reachable if this one merely fails.</summary>
    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(":::")]
    [InlineData("http://")]
    public void AnUnusableUrlBaseDeclinesRatherThanThrows(string urlBase)
    {
        UpnpPortMapper.ParseControlUrl(
            Description("urn:schemas-upnp-org:service:WANIPConnection:1", "/ctl", urlBase), Location);
    }

    [Fact]
    public void ARandomlyCorruptedDescriptionNeverThrows()
    {
        const string alphabet = "<>&;/\"' \0\uD800";
        var valid = Description("urn:schemas-upnp-org:service:WANIPConnection:1", "/ctl");
        var rng = new Random(0x7A7A);
        for (int trial = 0; trial < 300; trial++)
        {
            var chars = valid.ToCharArray();
            for (int k = 0; k < 1 + rng.Next(6); k++)
                chars[rng.Next(chars.Length)] = alphabet[rng.Next(alphabet.Length)];
            UpnpPortMapper.ParseControlUrl(new string(chars), Location);
        }
    }
}
