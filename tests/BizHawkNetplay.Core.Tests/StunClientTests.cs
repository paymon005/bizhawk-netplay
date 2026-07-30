using System.Collections.Generic;
using System.Net;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

public class StunClientTests
{
    // A Binding Success Response carrying XOR-MAPPED-ADDRESS for 203.0.113.5:51000.
    //   203.0.113.5 = CB 00 71 05 ; XOR cookie 2112A442 -> EA 12 D5 47
    //   51000 = C738 ; XOR (cookie>>16=2112) -> E62A
    private static byte[] XorMappedResponse(byte[] txn) => Concat(
        new byte[] { 0x01, 0x01, 0x00, 0x0C },          // type=Binding Success, msg length=12
        new byte[] { 0x21, 0x12, 0xA4, 0x42 },          // magic cookie
        txn,                                            // 12-byte transaction id
        new byte[] { 0x00, 0x20, 0x00, 0x08 },          // attr XOR-MAPPED-ADDRESS, length 8
        new byte[] { 0x00, 0x01, 0xE6, 0x2A, 0xEA, 0x12, 0xD5, 0x47 }); // reserved, IPv4, xport, xaddr

    [Fact]
    public void BuildRequest_HasCorrectHeader()
    {
        var req = StunClient.BuildRequest(out var txn);
        Assert.Equal(20, req.Length);
        Assert.Equal(0x00, req[0]);
        Assert.Equal(0x01, req[1]);                     // Binding Request
        Assert.Equal(0x00, req[2]);
        Assert.Equal(0x00, req[3]);                     // no attributes
        Assert.Equal(new byte[] { 0x21, 0x12, 0xA4, 0x42 }, new[] { req[4], req[5], req[6], req[7] });
        Assert.Equal(12, txn.Length);
        for (int i = 0; i < 12; i++) Assert.Equal(txn[i], req[8 + i]);
    }

    [Fact]
    public void ParsesXorMappedAddress()
    {
        var txn = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var ep = StunClient.ParseResponse(XorMappedResponse(txn), txn);
        Assert.NotNull(ep);
        Assert.Equal(IPAddress.Parse("203.0.113.5"), ep!.Address);
        Assert.Equal(51000, ep.Port);
    }

    [Fact]
    public void RejectsMismatchedTransactionId()
    {
        var txn = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var other = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
        Assert.Null(StunClient.ParseResponse(XorMappedResponse(txn), other));
    }

    [Fact]
    public void RejectsNonStunGarbage()
    {
        var txn = new byte[12];
        Assert.Null(StunClient.ParseResponse(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, txn));
        Assert.Null(StunClient.ParseResponse(new byte[100], txn)); // right length, wrong type/cookie
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var list = new List<byte>();
        foreach (var p in parts) list.AddRange(p);
        return list.ToArray();
    }
}
