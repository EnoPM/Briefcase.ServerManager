using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Briefcase.ServerManager.Core;

public readonly record struct ServerQueryResult(bool Reachable, double LatencyMilliseconds);

public sealed class ServerQueryClient
{
    public async Task<ServerQueryResult> ProbeAsync(string host, int port,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            throw new ArgumentException("The query endpoint is invalid.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, deadline.Token);
            var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6)
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            using var socket = new UdpClient(address.AddressFamily);
            socket.Connect(new IPEndPoint(address, port));
            var challenge = RandomNumberGenerator.GetBytes(24);
            challenge[0] = (byte)'B';
            challenge[1] = (byte)'C';
            var timer = Stopwatch.StartNew();
            await socket.SendAsync(challenge, deadline.Token);
            var response = await socket.ReceiveAsync(deadline.Token);
            timer.Stop();
            return new(response.Buffer.AsSpan().SequenceEqual(challenge), timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, 0);
        }
        catch (SocketException)
        {
            return new(false, 0);
        }
    }
}
