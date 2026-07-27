using System.Net;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    public class ConnectCodeTests
    {
        [Theory]
        [InlineData("203.0.113.5", 51000)]
        [InlineData("8.8.8.8", 47800)]
        [InlineData("0.0.0.0", 0)]
        [InlineData("255.255.255.255", 65535)]
        [InlineData("127.0.0.1", 1)]
        public void RoundTrips(string ip, int port)
        {
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
            var code = ConnectCode.Encode(ep);
            var back = ConnectCode.TryDecode(code);
            Assert.Equal(ep, back);
        }

        [Fact]
        public void EncodesToTwelveCharsGroupedByFour()
        {
            var code = ConnectCode.Encode(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 51000));
            Assert.Equal(2, System.Linq.Enumerable.Count(code, c => c == '-')); // three groups
            Assert.Equal(12, code.Replace("-", "").Length);
        }

        [Fact]
        public void DecodeIsCaseAndSeparatorInsensitive()
        {
            var ep = new IPEndPoint(IPAddress.Parse("198.51.100.22"), 30000);
            var code = ConnectCode.Encode(ep);
            Assert.Equal(ep, ConnectCode.TryDecode(code.ToLowerInvariant()));
            Assert.Equal(ep, ConnectCode.TryDecode(code.Replace("-", " ")));
            Assert.Equal(ep, ConnectCode.TryDecode(code.Replace("-", "")));
        }

        [Fact]
        public void ForgivesAmbiguousLetters()
        {
            // Crockford: O->0, I/L->1. A code with those substituted should still decode.
            var ep = new IPEndPoint(IPAddress.Parse("10.20.30.40"), 12345);
            var code = ConnectCode.Encode(ep);
            var mangled = code.Replace('0', 'O').Replace('1', 'I');
            Assert.Equal(ep, ConnectCode.TryDecode(mangled));
        }

        [Fact]
        public void RejectsChecksumTypos()
        {
            var code = ConnectCode.Encode(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 51000));
            var chars = code.Replace("-", "").ToCharArray();
            // Flip one symbol to a different valid symbol -> checksum must reject.
            chars[0] = chars[0] == '2' ? '3' : '2';
            Assert.Null(ConnectCode.TryDecode(new string(chars)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("not-a-code")]
        [InlineData("K7Q2-9M4H")]        // too short
        [InlineData("K7Q2-9M4H-XT10-99")] // too long
        public void RejectsMalformed(string? code)
        {
            Assert.Null(ConnectCode.TryDecode(code));
        }

        [Fact]
        public void TryParseTarget_AcceptsCodesAndLiteralEndpoints()
        {
            // A joiner can paste either the host's connect code or the ip:port straight off the
            // host's invite line ("internet joiners connect to 72.217.44.36:47800").
            var ep = new IPEndPoint(IPAddress.Parse("72.217.44.36"), 47800);
            Assert.Equal(ep, ConnectCode.TryParseTarget(ConnectCode.Encode(ep)));
            Assert.Equal(ep, ConnectCode.TryParseTarget("72.217.44.36:47800"));
            Assert.Equal(ep, ConnectCode.TryParseTarget("  72.217.44.36:47800  "));
        }

        [Theory]
        [InlineData("72.217.44.36")]        // no port
        [InlineData("72.217.44.36:")]       // empty port
        [InlineData("72.217.44.36:0")]      // port out of range
        [InlineData("72.217.44.36:70000")]  // port out of range
        [InlineData("nonsense:47800")]      // not an address
        [InlineData("::1:47800")]           // IPv6 not supported by codes
        [InlineData("")]
        [InlineData(null)]
        public void TryParseTarget_RejectsMalformedEndpoints(string? text)
        {
            Assert.Null(ConnectCode.TryParseTarget(text));
        }
    }
}
