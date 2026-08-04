using System;
using System.Security.Cryptography;
using System.Text;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Session-password proof for the handshake — a nonce challenge-response, so the password is never
/// sent (not even as a static hash a wiretapper could echo straight back to pass the check) and a
/// proof captured off one session can't be replayed into another.
///
/// Each peer contributes a fresh random nonce in its HELLO. Both then derive a key from
/// <c>password + both nonces</c> with a deliberately slow KDF (PBKDF2), and exchange <em>role-tagged</em>
/// proofs. The slow KDF makes an offline dictionary attack on a captured proof expensive; the role tag
/// (host vs join) blocks a reflection attack where someone bounces the host's own proof back at it; the
/// fresh nonces make every proof single-use. An empty password derives the same proof on both ends —
/// i.e. an open, no-password session — which is the intended "anyone may join" behaviour.
/// </summary>
public static class SessionAuth
{
    private const int Iterations = 100_000; // PBKDF2 stretch: slows an offline password guess to a crawl
    private const int NonceBytes = 16;

    public const string RoleHost = "host";
    public const string RoleJoin = "join";

    /// <summary>A fresh 16-byte random nonce for one HELLO.</summary>
    public static byte[] NewNonce()
    {
        var n = new byte[NonceBytes];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(n);
        return n;
    }

    /// <summary>A fresh mesh membership token for one seat (see <see cref="Net.MeshTokens"/>).
    /// Same size and source of randomness as a nonce; named apart because it lives for the whole
    /// session rather than one exchange.</summary>
    public static byte[] NewMeshToken() => NewNonce();

    /// <summary>A fresh non-zero identifier for one netplay session.</summary>
    public static ulong NewSessionId()
    {
        var bytes = new byte[sizeof(ulong)];
        using var rng = RandomNumberGenerator.Create();
        ulong id;
        do
        {
            rng.GetBytes(bytes);
            id = BitConverter.ToUInt64(bytes, 0);
        }
        while (id == 0);
        return id;
    }

    /// <summary>
    /// The proof a peer sends for its <paramref name="role"/>. The two nonces are passed in their fixed
    /// roles (host's first, joiner's second) so both ends derive identical inputs no matter who computes.
    /// </summary>
    public static string Proof(string? password, string role, byte[] hostNonce, byte[] joinNonce)
    {
        var salt = Salt(hostNonce, joinNonce);
        return ProofFromKey(DeriveKey(password, salt), role, salt);
    }

    /// <summary>
    /// Both proofs for one exchange, from a single key derivation.
    ///
    /// The two differ only in a role tag appended after the KDF, so deriving them separately ran
    /// 100,000 PBKDF2 iterations twice over identical inputs for identical output. Every peer paid
    /// that, and so did a host for every connection that reached the password step — including the
    /// ones that were never going to pass it, which made a stranger's connection attempt about
    /// twice as expensive as it needed to be.
    /// </summary>
    public static (string mine, string peers) ProofPair(
        string? password, string myRole, string peerRole, byte[] hostNonce, byte[] joinNonce)
    {
        var (mine, peers, key) = ProofPairWithKey(password, myRole, peerRole, hostNonce, joinNonce);
        Array.Clear(key, 0, key.Length);
        return (mine, peers);
    }

    /// <summary>
    /// As <see cref="ProofPair"/>, and hands the derived session key back instead of discarding
    /// it. This is what KI-13 called "the fix is cheaper than it reads": the 32 bytes that
    /// authenticate every control frame for the rest of the session were already being computed
    /// here and thrown away. The caller feeds them to <see cref="MacKey"/> and then to
    /// <see cref="ControlChannel.EnableIntegrity"/>, and should clear its copy afterwards.
    /// </summary>
    public static (string mine, string peers, byte[] key) ProofPairWithKey(
        string? password, string myRole, string peerRole, byte[] hostNonce, byte[] joinNonce)
    {
        var salt = Salt(hostNonce, joinNonce);
        var key = DeriveKey(password, salt);
        return (ProofFromKey(key, myRole, salt), ProofFromKey(key, peerRole, salt), key);
    }

    /// <summary>
    /// The control-frame MAC key, derived from — never equal to — the session key. The proofs are
    /// SHA256 over the session key with a role tag; keeping the MAC in its own HMAC domain means
    /// no value computed for one purpose is ever verified as another.
    /// </summary>
    public static byte[] MacKey(byte[] sessionKey)
    {
        if (sessionKey == null) throw new ArgumentNullException(nameof(sessionKey));
        using var hmac = new HMACSHA256(sessionKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes("bizhawk-netplay control-frame mac v1"));
    }

    private static byte[] Salt(byte[] hostNonce, byte[] joinNonce)
    {
        if (hostNonce == null) throw new ArgumentNullException(nameof(hostNonce));
        if (joinNonce == null) throw new ArgumentNullException(nameof(joinNonce));

        var salt = new byte[hostNonce.Length + joinNonce.Length];
        Buffer.BlockCopy(hostNonce, 0, salt, 0, hostNonce.Length);
        Buffer.BlockCopy(joinNonce, 0, salt, hostNonce.Length, joinNonce.Length);
        return salt;
    }

    private static byte[] DeriveKey(string? password, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(password ?? "", salt, Iterations);
        return kdf.GetBytes(32);
    }

    private static string ProofFromKey(byte[] key, string role, byte[] salt)
    {
        var roleBytes = Encoding.UTF8.GetBytes(role ?? "");
        var msg = new byte[key.Length + roleBytes.Length + salt.Length];
        Buffer.BlockCopy(key, 0, msg, 0, key.Length);
        Buffer.BlockCopy(roleBytes, 0, msg, key.Length, roleBytes.Length);
        Buffer.BlockCopy(salt, 0, msg, key.Length + roleBytes.Length, salt.Length);

        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(msg));
    }

    /// <summary>Length-independent, early-exit-free string compare for proof verification.</summary>
    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    public static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Decode a hex nonce; null on any malformed input (untrusted wire value).</summary>
    public static byte[]? FromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || (hex!.Length & 1) != 0) return null;
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
        {
            int hi = HexVal(hex[2 * i]);
            int lo = HexVal(hex[2 * i + 1]);
            if (hi < 0 || lo < 0) return null;
            b[i] = (byte)((hi << 4) | lo);
        }
        return b;
    }

    private static int HexVal(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}
