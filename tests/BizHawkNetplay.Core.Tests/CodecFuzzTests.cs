using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Every decoder, against bodies nobody meant to send.
///
/// These run on the peer reader thread. An unhandled exception there does not produce a decode
/// failure — it kills the thread, and the session it was reading for dies without a message anyone
/// can act on. So the contract is not "decodes correctly", it is: <b>refuse, or return something
/// inside its own declared domain, and never throw anything but the two exceptions the callers
/// catch by name.</b>
///
/// The round-trip tests next door only ever hand a decoder what its own encoder produced. Nothing
/// asked what happens when a body arrives from a peer on a different build, a corrupted frame past
/// UDP's checksum, or somebody with a hex editor — and the control channel authenticates the
/// SENDER, not the sender's competence.
///
/// Cross-feeding is deliberate: every sample goes to every decoder, so a checksum body reaches the
/// mesh-RTT reader and a route list reaches the state unpacker. Same-length-different-meaning is
/// exactly the shape a version skew produces.
/// </summary>
public class CodecFuzzTests
{
    private static readonly SessionGeneration Gen = new(0x0123456789ABCDEF, 7);

    /// <summary>What a decoder is allowed to raise. Both are caught by name at every call site:
    /// a malformed body is a refused message, not a dead reader.</summary>
    private static bool IsTolerated(Exception e) =>
        e is FormatException || e is ArgumentNullException;

    // ---------------------------------------------------------------- the surface

    private delegate void Decoder(byte[] body);

    private static readonly (string Name, Decoder Run)[] Decoders =
    [
        ("Pacing", b => ControlMessageCodec.TryDecodePacing(b, out _, out _, out _, out _, out _)),
        ("Checksum", b => ControlMessageCodec.TryDecodeChecksum(b, Gen, out _, out _)),
        ("ResyncBegin", b => ControlMessageCodec.TryDecodeResyncBegin(b, out _, out _, out _, out _, out _, out _)),
        ("StatePayload", b => ControlMessageCodec.TryDecodeStatePayload(b, out _, out _)),
        ("MeshRtt", b => ControlMessageCodec.TryDecodeMeshRtt(b, out _, out _, out _, out _, out _, out _)),
        ("SeatVacated", b => ControlMessageCodec.TryDecodeSeatVacated(b, out _, out _)),
        ("InputOutage", b => ControlMessageCodec.TryDecodeInputOutage(b, out _, out _)),
        ("StateRequest", b => ControlMessageCodec.TryDecodeStateRequest(b, out _)),
        ("StateOffer", b => ControlMessageCodec.TryDecodeStateOffer(b, out _, out _)),
        ("DivergenceReport", b => ControlMessageCodec.TryDecodeDivergenceReport(b, out _, out _, out _)),
        ("ExclusionMask", b => ControlMessageCodec.TryDecodeExclusionMask(b, out _, out _, out _)),
        ("InputDelay", b => ControlMessageCodec.TryDecodeInputDelay(b, out _, out _)),
        ("Generation", b => ControlMessageCodec.TryDecodeGeneration(b, out _)),

        ("HandshakeChallenge", b => HandshakeCodec.DecodeChallenge(b)),
        ("HandshakeJoinerIntro", b => HandshakeCodec.DecodeJoinerIntro(b)),
        ("HandshakeHello", b => HandshakeCodec.Decode(b)),
        ("HandshakeWelcome", b => HandshakeCodec.DecodeWelcome(b)),
        ("HandshakeRoutes", b => HandshakeCodec.DecodeRoutes(b)),
        ("HandshakeTokens", b => HandshakeCodec.DecodeTokens(b)),
        ("HandshakeChecksumInterval", b => HandshakeCodec.DecodeChecksumInterval(b)),
        ("HandshakeVacatedSeats", b => HandshakeCodec.DecodeVacatedSeats(b)),
        ("HandshakeEndpoints", b => HandshakeCodec.DecodeEndpoints(b)),
        ("HandshakeGeneration", b => HandshakeCodec.DecodeGeneration(b)),
    ];

    /// <summary>Well-formed bodies of every shape, to be mutated and cross-fed.</summary>
    private static List<byte[]> ValidSamples()
    {
        var samples = new List<byte[]>
        {
            ControlMessageCodec.EncodePacing(Gen, 3, 2, 100, -4),
            ControlMessageCodec.EncodeChecksum(Gen, 600, 0xDEADBEEF),
            ControlMessageCodec.EncodeResyncBegin(Gen, 4096, 3, SyncMode.Rollback, 5, true),
            ControlMessageCodec.EncodeStatePayload(Gen, new byte[64]),
            ControlMessageCodec.EncodeStatePayload(Gen, Incompressible(64)),
            ControlMessageCodec.EncodeMeshRtt(Gen, 12.5, 40.0, 2, 3, new[] { 1, 2 }),
            ControlMessageCodec.EncodeSeatVacated(Gen, 2),
            ControlMessageCodec.EncodeStateRequest(Gen),
            ControlMessageCodec.EncodeStateOffer(Gen, StateCompression.Pack(new byte[32])),
            ControlMessageCodec.EncodeDivergenceReport(Gen, 300, new uint[ControlMessageCodec.DivergenceBuckets]),
            ControlMessageCodec.EncodeExclusionMask(Gen, 900, AlternatingMask()),
            ControlMessageCodec.EncodeInputDelay(Gen, 4),
            HandshakeCodec.EncodeGeneration(Gen),
            HandshakeCodec.EncodeChallenge(23, new byte[16]),
            HandshakeCodec.EncodeJoinerIntro(23, new byte[16], 9999,
                new IPEndPoint(IPAddress.Parse("203.0.113.5"), 30000)),
            HandshakeCodec.EncodeWelcome(1, 4, 3, SyncMode.Rollback, Gen,
                new[] { new PeerRoute(2, new[] { new IPEndPoint(IPAddress.Loopback, 4001) }) },
                MeshTokens.None, new[] { 3 }),
            HandshakeCodec.EncodeRoutes(new[]
            {
                new PeerRoute(1, new[] { new IPEndPoint(IPAddress.Loopback, 4000) }),
                new PeerRoute(2, new[] { new IPEndPoint(IPAddress.IPv6Loopback, 4002) }),
            }),
            HandshakeCodec.EncodeEndpoints(new[] { new IPEndPoint(IPAddress.Loopback, 7000) }),
        };

        // Text bodies whose shapes no encoder can produce, because the point is what a DIFFERENT
        // implementation might emit: absurd numbers, empty values, repeated and unterminated keys.
        foreach (var text in new[]
                 {
                     "",
                     "\n\n\n",
                     "=",
                     "=value",
                     "key=",
                     "proto",
                     "proto=99999999999999999999\nudpport=70000\ndelay=-2147483648\n",
                     "players=0\nport=-5\nsession=0\nepoch=-1\n",
                     "session=18446744073709551615\nepoch=2147483647\n",
                     "route=\nroute=1|\nroute=1|not-an-endpoint\nroute=999|1.2.3.4:5\n",
                     "route=1|[::1]:0|[::1]:65536|1.2.3.4:99999\n",
                     "tok=\ntok=:\ntok=1:\nmytok=\nmytok=zz\npk=\npk=1:1:00\npk=1:2:zz\n",
                     "vacated=\nvacated=,,,\nvacated=99,-1,2\n",
                     "disc=\ndisc=:\ndisc=-1:abc\ndisc=99999:abc\n",
                     "syncf=\nsyncf=|\nsyncf=k\nsyncf=\\\nsyncf=" + new string('x', 500) + "|v\n",
                     "refl=[\nrefl=[]:\nrefl=:::\nrefl=1.2.3.4:\n",
                     "ckint=0\nckint=-1\nckint=2147483647\n",
                     "layouts=,,,\nnonce=z\nbuild=\\\\\\\nvideo=\\q\n",
                 })
            samples.Add(Encoding.UTF8.GetBytes(text));

        return samples;
    }

    private static byte[] Incompressible(int n)
    {
        // Deflate must lose on this, so Pack emits FormatRaw and both branches of TryUnpack get fed.
        var rng = new Random(1);
        var bytes = new byte[n];
        rng.NextBytes(bytes);
        return bytes;
    }

    private static bool[] AlternatingMask()
    {
        var mask = new bool[ControlMessageCodec.DivergenceBuckets];
        for (int i = 0; i < mask.Length; i++) mask[i] = i % 3 == 0;
        return mask;
    }

    // ---------------------------------------------------------------- the properties

    /// <summary>
    /// Null, every valid body fed to every decoder, and every truncation of each.
    ///
    /// Truncation is the one a length check gets wrong: a decoder that tests <c>Length &gt;= n</c>
    /// and then reads a field at <c>n + 4</c> passes every round-trip test ever written and throws
    /// <see cref="IndexOutOfRangeException"/> on the first short frame that reaches it.
    /// </summary>
    [Fact]
    public void NoDecoderDiesOnATruncatedOrForeignBody()
    {
        var samples = ValidSamples();
        foreach (var (name, decode) in Decoders)
        {
            Attempt(name, null!, decode);
            foreach (var sample in samples)
            {
                Attempt(name, sample, decode);
                for (int length = 0; length <= sample.Length; length++)
                {
                    var cut = new byte[length];
                    Buffer.BlockCopy(sample, 0, cut, 0, length);
                    Attempt(name, cut, decode);
                }
                // And one byte too many: a decoder keyed on a minimum rather than an exact length
                // must still not read past what it was given.
                var over = new byte[sample.Length + 1];
                Buffer.BlockCopy(sample, 0, over, 0, sample.Length);
                Attempt(name, over, decode);
            }
        }
    }

    /// <summary>
    /// Single-bit corruption of otherwise valid bodies — the shape a frame takes when it survives
    /// UDP's 16-bit checksum, or when two builds disagree about one field's width.
    /// </summary>
    [Fact]
    public void NoDecoderDiesOnABitFlip()
    {
        var rng = new Random(0x5EED);
        var samples = ValidSamples();
        foreach (var sample in samples)
            for (int trial = 0; trial < 40; trial++)
            {
                if (sample.Length == 0) continue;
                var flipped = (byte[])sample.Clone();
                int at = rng.Next(flipped.Length);
                flipped[at] ^= (byte)(1 << rng.Next(8));
                foreach (var (name, decode) in Decoders) Attempt(name, flipped, decode);
            }
    }

    /// <summary>
    /// Bodies of pure noise, at every length a header could straddle.
    ///
    /// Length 0 through 40 covers the fixed-size frames exactly; the larger ones reach the
    /// variable-length readers. A seeded RNG so a failure names a body that can be rebuilt.
    /// </summary>
    [Fact]
    public void NoDecoderDiesOnNoise()
    {
        var rng = new Random(0xC0FFEE);
        var lengths = new List<int>();
        for (int n = 0; n <= 40; n++) lengths.Add(n);
        lengths.AddRange([64, 127, 128, 273, 274, 275, 1024, 4096]);

        foreach (int length in lengths)
            for (int trial = 0; trial < 8; trial++)
            {
                var noise = new byte[length];
                rng.NextBytes(noise);
                foreach (var (name, decode) in Decoders) Attempt(name, noise, decode);
            }
    }

    private static void Attempt(string name, byte[] body, Decoder decode)
    {
        try { decode(body); }
        catch (Exception e) when (IsTolerated(e)) { }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException(
                $"{name} threw {e.GetType().Name} on a {(body == null ? "null" : body.Length + "-byte")} " +
                $"body — on the reader thread that is a dead session, not a refused message. " +
                $"Body: {Describe(body)}. {e.Message}");
        }
    }

    private static string Describe(byte[]? body)
    {
        if (body == null) return "null";
        var sb = new StringBuilder("[");
        for (int i = 0; i < body.Length && i < 48; i++) sb.Append(body[i].ToString("X2"));
        if (body.Length > 48) sb.Append("…");
        return sb.Append(']').ToString();
    }

    // ---------------------------------------------------------------- accepted => in domain

    /// <summary>
    /// A decoder that says yes must produce values its caller can use without re-checking.
    ///
    /// Refusing safely is half the contract. The other half is that "true" means the out-values are
    /// inside the ranges the handlers index arrays and size buffers with — a seat that indexes a
    /// port array, a delay that seeds neutral frames, a bucket count the mask loop trusts.
    /// </summary>
    [Fact]
    public void AnAcceptedBodyCarriesValuesInsideTheirDeclaredRange()
    {
        var rng = new Random(0x16B00B5);
        var bodies = new List<byte[]>(ValidSamples());
        // Mutate hard: the interesting acceptances are the ones a valid body did not produce.
        foreach (var sample in ValidSamples())
            for (int trial = 0; trial < 200 && sample.Length > 0; trial++)
            {
                var m = (byte[])sample.Clone();
                for (int k = 0; k < 1 + rng.Next(4); k++) m[rng.Next(m.Length)] = (byte)rng.Next(256);
                bodies.Add(m);
            }

        int accepted = 0;
        foreach (var body in bodies)
        {
            if (ControlMessageCodec.TryDecodeSeatVacated(body, out _, out int seat))
            {
                accepted++;
                Assert.InRange(seat, 0, HandshakeCodec.MaxPlayers - 1);
            }
            if (ControlMessageCodec.TryDecodeInputOutage(body, out _, out int outagePort))
            {
                accepted++;
                Assert.InRange(outagePort, 0, HandshakeCodec.MaxPlayers - 1);
            }
            if (ControlMessageCodec.TryDecodeInputDelay(body, out _, out int delay))
            {
                accepted++;
                Assert.InRange(delay, 1, HandshakeCodec.MaxInputDelay);
            }
            if (ControlMessageCodec.TryDecodeResyncBegin(body, out _, out int stateBytes,
                    out int waitSeconds, out int resyncDelay, out _, out _))
            {
                accepted++;
                Assert.InRange(stateBytes, 0, ControlMessageCodec.MaxStateBytes);
                Assert.InRange(waitSeconds, 0, ControlMessageCodec.MaxResyncWaitSeconds);
                Assert.InRange(resyncDelay, 1, HandshakeCodec.MaxInputDelay);
            }
            if (ControlMessageCodec.TryDecodeMeshRtt(body, out _, out double median, out double high,
                    out int measured, out int total, out int[] silent))
            {
                accepted++;
                Assert.InRange(median, 0, ControlMessageCodec.MaxMeshRttMs);
                Assert.InRange(high, median, ControlMessageCodec.MaxMeshRttMs);
                Assert.InRange(measured, 0, total);
                Assert.InRange(total, 0, HandshakeCodec.MaxPlayers);
                Assert.InRange(silent.Length, 0, HandshakeCodec.MaxPlayers);
                foreach (int port in silent) Assert.InRange(port, 0, HandshakeCodec.MaxPlayers - 1);
            }
            if (ControlMessageCodec.TryDecodeDivergenceReport(body, out _, out int reportFrame,
                    out uint[] buckets))
            {
                accepted++;
                Assert.True(reportFrame >= 0);
                Assert.Equal(ControlMessageCodec.DivergenceBuckets, buckets.Length);
            }
            if (ControlMessageCodec.TryDecodeExclusionMask(body, out _, out int effectiveFrom,
                    out bool[] mask))
            {
                accepted++;
                Assert.True(effectiveFrom >= 0);
                Assert.Equal(ControlMessageCodec.DivergenceBuckets, mask.Length);
            }
            if (ControlMessageCodec.TryDecodeStatePayload(body, out _, out byte[] state))
            {
                accepted++;
                Assert.InRange(state.Length, 0, ControlMessageCodec.MaxStateBytes);
            }
        }

        // Without this the test would pass just as happily if every mutation were refused, which
        // proves nothing about what an acceptance means.
        Assert.True(accepted > 100, $"only {accepted} bodies were accepted — the mutations are " +
            "too destructive to say anything about what a 'true' return guarantees");
    }

    /// <summary>
    /// The generation prefix is the session's fence: a body from a dead timeline must never decode
    /// as one from the live one. Every in-session message carries it, and every handler acts on the
    /// decode rather than re-checking, so the check has to be here.
    /// </summary>
    [Fact]
    public void AGenerationFromAnotherTimelineIsNeverMistakenForOurs()
    {
        var other = new SessionGeneration(Gen.SessionId, Gen.Epoch + 1);
        var stranger = new SessionGeneration(Gen.SessionId ^ 1, Gen.Epoch);

        Assert.False(ControlMessageCodec.TryDecodeChecksum(
            ControlMessageCodec.EncodeChecksum(other, 60, 1), Gen, out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeChecksum(
            ControlMessageCodec.EncodeChecksum(stranger, 60, 1), Gen, out _, out _));
        Assert.True(ControlMessageCodec.TryDecodeChecksum(
            ControlMessageCodec.EncodeChecksum(Gen, 60, 1), Gen, out _, out _));

        // The rest hand the generation back rather than filtering, so what matters is that it comes
        // back intact — a handler comparing it is comparing the sender's, not a default.
        Assert.True(ControlMessageCodec.TryDecodeGeneration(
            HandshakeCodec.EncodeGeneration(other), out var readBack));
        Assert.Equal(other, readBack);
    }

    /// <summary>
    /// A zero session ID is not a session. It is <see cref="SessionGeneration.Legacy"/>'s marker
    /// and the value an all-zero body decodes to, so accepting it would let a run of zeros — the
    /// most likely corruption of all — pass as a live generation.
    /// </summary>
    [Fact]
    public void AnAllZeroBodyIsNotAGeneration()
    {
        Assert.False(ControlMessageCodec.TryDecodeGeneration(new byte[12], out _));
        Assert.False(ControlMessageCodec.TryDecodeStateRequest(new byte[12], out _));
        Assert.False(ControlMessageCodec.TryDecodePacing(new byte[ControlMessageCodec.PacingSize],
            out _, out _, out _, out _, out _));
        Assert.Throws<FormatException>(() => HandshakeCodec.DecodeGeneration(new byte[12]));
    }

    // ---------------------------------------------------------------- text entry points

    /// <summary>
    /// The two parsers that take a string a human pasted rather than a peer sent. Both are the
    /// first thing a wrong paste reaches, and neither may throw its way out of a click handler.
    /// </summary>
    [Fact]
    public void ThePastedTextParsersRefuseRatherThanThrow()
    {
        var rng = new Random(0x0EEDFACE);
        var inputs = new List<string?>
        {
            null, "", " ", ":", "::", "[", "[]", "[]:", "]:", ":1", "1.2.3.4:", "1.2.3.4:0",
            "1.2.3.4:65536", "1.2.3.4:99999999999999999999", "[::1]", "[::1]:", "[::1]:0",
            "host:port", "999.999.999.999:1", new string('A', 4096), "\0\0\0", "AAAA====",
            "-----", "%%%%", "\u0000\uFFFF\uD800",
        };
        for (int i = 0; i < 200; i++)
        {
            var chars = new char[rng.Next(0, 40)];
            for (int c = 0; c < chars.Length; c++) chars[c] = (char)rng.Next(32, 127);
            inputs.Add(new string(chars));
        }

        foreach (var text in inputs)
        {
            ConnectCode.TryDecode(text);
            HostAddress.TryParse(text, 7000, out _, out _);
        }
    }
}
