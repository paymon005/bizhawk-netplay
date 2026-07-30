using System;
using System.Net;
using System.Text;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// Encodes a public (STUN-reflexive) <see cref="IPEndPoint"/> as a short, human-typeable
/// "connect code" that peers swap out-of-band (Discord, text) to reach each other with no
/// port-forwarding — the RemotePlay punch-through model. An IPv4 address (4 bytes) + UDP port
/// (2 bytes) + a checksum byte pack into 7 bytes, rendered as 12 Crockford-base32 characters
/// grouped for legibility (e.g. <c>K7Q2-9M4H-XT10</c>). The checksum rejects most typos before
/// anyone tries to connect, and Crockford's alphabet drops the ambiguous I/L/O/U so a hand-copied
/// code round-trips even if someone writes O for 0.
/// </summary>
public static class ConnectCode
{
    // Crockford base32: no I, L, O, U (typo-resistant). Index = 5-bit value.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Encode an IPv4 endpoint as a 12-char grouped code. Throws on a non-IPv4 address.</summary>
    public static string Encode(IPEndPoint endpoint)
    {
        if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
        var ip = endpoint.Address.GetAddressBytes();
        if (ip.Length != 4)
            throw new ArgumentException("connect codes are IPv4-only", nameof(endpoint));
        if (endpoint.Port < 0 || endpoint.Port > 65535)
            throw new ArgumentException("port out of range", nameof(endpoint));

        var raw = new byte[7];
        raw[0] = ip[0]; raw[1] = ip[1]; raw[2] = ip[2]; raw[3] = ip[3];
        raw[4] = (byte)(endpoint.Port >> 8);
        raw[5] = (byte)endpoint.Port;
        raw[6] = Checksum(raw, 6);

        var sb = new StringBuilder(14);
        // 7 bytes = 56 bits -> 12 base32 symbols (5 bits each; the last symbol uses 1 real bit).
        long bits = 0;
        int nbits = 0, produced = 0;
        for (int i = 0; i < raw.Length || nbits > 0; )
        {
            if (nbits < 5 && i < raw.Length) { bits = (bits << 8) | raw[i++]; nbits += 8; continue; }
            int shift = nbits - 5;
            int idx = shift >= 0 ? (int)((bits >> shift) & 0x1F) : (int)((bits << -shift) & 0x1F);
            nbits = shift >= 0 ? shift : 0;
            sb.Append(Alphabet[idx]);
            if (++produced % 4 == 0 && produced < 12) sb.Append('-');
            if (produced == 12) break;
        }
        return sb.ToString();
    }

    /// <summary>Parse a code back to an endpoint. Returns null on a malformed or checksum-failing code.</summary>
    public static IPEndPoint? TryDecode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        long bits = 0;
        int nbits = 0;
        var raw = new byte[7];
        int outPos = 0;
        foreach (char c0 in code!)
        {
            char c = char.ToUpperInvariant(c0);
            if (c == '-' || c == ' ' || c == '\t') continue;
            if (c == 'O') c = '0';
            if (c == 'I' || c == 'L') c = '1';
            int v = Alphabet.IndexOf(c);
            if (v < 0) return null; // stray character
            bits = (bits << 5) | (uint)v;
            nbits += 5;
            if (nbits >= 8)
            {
                if (outPos >= raw.Length) return null; // too long
                nbits -= 8;
                raw[outPos++] = (byte)((bits >> nbits) & 0xFF);
            }
        }
        if (outPos != 7) return null;                       // wrong length
        if (raw[6] != Checksum(raw, 6)) return null;        // typo caught

        var ip = new IPAddress(new[] { raw[0], raw[1], raw[2], raw[3] });
        int port = (raw[4] << 8) | raw[5];
        return new IPEndPoint(ip, port);
    }

    /// <summary>
    /// Parse either a connect code or a literal <c>ip:port</c> — RemotePlay-style, so a joiner
    /// can type the host's advertised endpoint straight from the host's invite line without
    /// anyone encoding it. Codes never contain ':' or '.', so the two forms can't collide.
    /// </summary>
    public static IPEndPoint? TryParseTarget(string? text)
    {
        var decoded = TryDecode(text);
        if (decoded != null) return decoded;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text!.Trim();
        int colon = s.LastIndexOf(':');
        if (colon <= 0 || colon == s.Length - 1) return null;
        if (!IPAddress.TryParse(s.Substring(0, colon), out var ip)) return null;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        if (!int.TryParse(s.Substring(colon + 1), out int port) || port < 1 || port > 65535) return null;
        return new IPEndPoint(ip, port);
    }

    private static byte Checksum(byte[] b, int len)
    {
        // Simple additive-rotate checksum: order-sensitive (catches transposed groups), 1 byte.
        byte h = 0x5A;
        for (int i = 0; i < len; i++) { h = (byte)((h << 1) | (h >> 7)); h ^= b[i]; }
        return h;
    }
}
