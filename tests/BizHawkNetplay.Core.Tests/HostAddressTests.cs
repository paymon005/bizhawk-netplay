using System.Net;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The join box takes either a bare IP or the "ip:port" form a host reads out, so the parser has to
/// tell a port apart from an IPv6 literal's colons and reject junk before anything gets dialed.
/// </summary>
public class HostAddressTests
{
    private const int Default = 47800;

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", Default)]                 // bare IP -> the Port box's value
    [InlineData("  1.2.3.4  ", "1.2.3.4", Default)]             // pasted with whitespace
    [InlineData("1.2.3.4:47801", "1.2.3.4", 47801)]             // typed port wins
    [InlineData("127.0.0.1:1", "127.0.0.1", 1)]                 // port range edges
    [InlineData("127.0.0.1:65535", "127.0.0.1", 65535)]
    [InlineData("::1", "::1", Default)]                         // bare IPv6: all colons, no port
    [InlineData("[::1]", "::1", Default)]                       // bracketed, no port
    [InlineData("[::1]:47801", "::1", 47801)]                   // bracketed with a port
    [InlineData("[fe80::1%2]:5000", "fe80::1%2", 5000)]         // scoped IPv6
    public void ParsesAcceptedForms(string text, string expectedIp, int expectedPort)
    {
        Assert.True(HostAddress.TryParse(text, Default, out var ip, out int port));
        Assert.Equal(IPAddress.Parse(expectedIp), ip);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("myhost.example.com")]      // literal IPs only — no DNS resolution here
    [InlineData("myhost.example.com:47800")]
    [InlineData("1.2.3.4:")]                // colon with no port
    [InlineData("1.2.3.4:abc")]
    [InlineData("1.2.3.4: 47800")]          // space inside the port
    [InlineData("1.2.3.4:+47800")]
    [InlineData("1.2.3.4:0")]               // port 0 would bind ephemeral, never reach a host
    [InlineData("1.2.3.4:65536")]           // out of range
    [InlineData("1.2.3.4:123456")]
    [InlineData("1.2.3.4:-1")]
    [InlineData(":47800")]                  // no address
    [InlineData("[::1")]                    // unclosed bracket
    [InlineData("[::1]junk")]               // trailing junk after the bracket
    [InlineData("[::1]:")]
    public void RejectsMalformedInput(string? text)
    {
        Assert.False(HostAddress.TryParse(text, Default, out var ip, out _));
        Assert.Null(ip);
    }

    [Fact]
    public void RejectedInputStillReportsTheDefaultPort()
    {
        // The caller falls back to the Port box on failure; it must never see a half-parsed port.
        Assert.False(HostAddress.TryParse("1.2.3.4:99999", Default, out _, out int port));
        Assert.Equal(Default, port);
    }

    [Fact]
    public void FormatRoundTripsThroughTryParse()
    {
        foreach (var (ip, port) in new[] { ("1.2.3.4", 47801), ("::1", 47801) })
        {
            string text = HostAddress.Format(IPAddress.Parse(ip), port);
            Assert.True(HostAddress.TryParse(text, Default, out var back, out int backPort));
            Assert.Equal(IPAddress.Parse(ip), back);
            Assert.Equal(port, backPort);
        }
    }
}
