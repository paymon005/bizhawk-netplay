using System;
using System.Net;
using System.Net.Sockets;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Core.Tests.Fakes;

/// <summary>
/// Two real <see cref="ControlChannel"/>s wired back to back over a loopback TCP pair, with the
/// roles and integrity a live session would have.
///
/// This existed as a private helper inside <c>HandshakeTests</c> and was only ever used to drive
/// handshake flows. Everything AFTER the handshake — every checksum, resync, vacate, divergence
/// report and state offer — was tested against a codec and never through a channel, which is
/// exactly the gap <c>StateOffer</c> shipped through: a frame that encoded and decoded perfectly
/// and could not be sent.
///
/// <see cref="Authenticated"/> and the declared role are what the frame-size ceiling turns on, so a
/// pair that skips them is not testing the rules a session runs under.
/// </summary>
public sealed class ChannelPair : IDisposable
{
    private readonly TcpClient _hostTcp;
    private readonly TcpClient _joinerTcp;

    private ChannelPair(TcpClient hostTcp, TcpClient joinerTcp,
        ControlChannel host, ControlChannel joiner)
    {
        _hostTcp = hostTcp;
        _joinerTcp = joinerTcp;
        Host = host;
        Joiner = joiner;
    }

    public ControlChannel Host { get; }
    public ControlChannel Joiner { get; }

    /// <summary>The channel for a peer in this role, so a test can be written once and run twice.</summary>
    public ControlChannel For(bool isHost) => isHost ? Host : Joiner;

    /// <summary>The channel at the OTHER end from the given role.</summary>
    public ControlChannel Opposite(bool isHost) => isHost ? Joiner : Host;

    /// <param name="authenticated">Whether the password exchange is treated as complete. False
    /// models a connection that has not earned the savestate ceiling.</param>
    /// <param name="integrity">Whether per-frame MACs are on. Also what declares each end's role,
    /// which the direction half of the ceiling reads.</param>
    public static ChannelPair Create(bool authenticated = true, bool integrity = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var joinerTcp = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        joinerTcp.Connect(IPAddress.Loopback, port);
        var hostTcp = accept.GetAwaiter().GetResult();
        listener.Stop();

        var host = new ControlChannel(hostTcp.GetStream()) { Authenticated = authenticated };
        var joiner = new ControlChannel(joinerTcp.GetStream()) { Authenticated = authenticated };
        if (integrity)
        {
            // The same key both ends: a real session derives it from the password and both nonces.
            var key = new byte[32];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 3 + 7);
            host.EnableIntegrity(key, isHost: true);
            joiner.EnableIntegrity(key, isHost: false);
        }
        return new ChannelPair(hostTcp, joinerTcp, host, joiner);
    }

    /// <summary>
    /// Send one frame and return what the other end read.
    ///
    /// The receive runs on its own task because a body larger than the socket buffers cannot be
    /// sent and then read on one thread: <c>Send</c> blocks once the buffer fills and nobody is
    /// draining it. Every state-bearing message is exactly that size, so a test written the obvious
    /// way deadlocks on precisely the messages worth testing.
    /// </summary>
    public (ControlMessageType type, byte[] body) RoundTrip(
        bool senderIsHost, ControlMessageType type, byte[] body, int timeoutMs = 20_000)
    {
        var receiver = Opposite(senderIsHost);
        var pending = System.Threading.Tasks.Task.Run(() => receiver.Receive());
        For(senderIsHost).Send(type, body);
        if (!pending.Wait(timeoutMs))
            throw new TimeoutException($"{type} never arrived at the {(senderIsHost ? "joiner" : "host")}");
        return pending.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { _hostTcp.Close(); } catch { }
        try { _joinerTcp.Close(); } catch { }
    }
}
