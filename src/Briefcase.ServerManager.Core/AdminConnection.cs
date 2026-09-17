using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Briefcase.ServerManager.Core;

public sealed class AdminConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim requests = new(1, 1);
    private TcpClient? client;
    private SslStream? stream;
    private string? fingerprint;
    public bool Connected => stream is not null;
    public string ServerId { get; private set; } = "";
    public string Protocol { get; private set; } = "";

    public async Task ConnectAsync(string endpointText, string expectedFingerprint, string password,
        CancellationToken cancellationToken = default)
    {
        await DisposeTransportAsync();
        var endpoint = AdminEndpoint.Parse(endpointText);
        fingerprint = NormalizeFingerprint(expectedFingerprint);
        if (Encoding.UTF8.GetByteCount(password) is < 12 or > 256)
            throw new ArgumentException("The administrator password must contain 12 to 256 UTF-8 bytes.");

        var nextClient = new TcpClient();
        try
        {
            await nextClient.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
            var nextStream = new SslStream(nextClient.GetStream(), false, ValidateCertificate);
            await nextStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            client = nextClient;
            stream = nextStream;
            Protocol = nextStream.SslProtocol.ToString();
            var hello = await RequestAsync("hello", null, cancellationToken);
            ServerId = hello["serverId"]?.GetValue<string>() ?? throw new AdminProtocolException("protocol_error", "Missing server identity.");
            var authentication = new JsonObject { ["password"] = password };
            await RequestAsync("authenticate", authentication, cancellationToken);
            authentication["password"] = "";
        }
        catch
        {
            nextClient.Dispose();
            await DisposeTransportAsync();
            throw;
        }
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null || fingerprint is null)
            return false;
        var raw = certificate.GetRawCertData();
        var actual = Convert.ToHexStringLower(SHA256.HashData(raw));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(fingerprint)))
            return false;
        using var parsed = X509CertificateLoader.LoadCertificate(raw);
        var now = DateTimeOffset.UtcNow;
        return now >= parsed.NotBefore.ToUniversalTime() && now <= parsed.NotAfter.ToUniversalTime();
    }

    public async Task<JsonNode> RequestAsync(string operation, JsonObject? payload = null,
        CancellationToken cancellationToken = default)
    {
        if (stream is null)
            throw new AdminProtocolException("disconnected", "Connect to the server first.");
        await requests.WaitAsync(cancellationToken);
        try
        {
            var requestId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var envelope = new JsonObject
            {
                ["version"] = 1,
                ["requestId"] = requestId,
                ["operation"] = operation,
                ["payload"] = payload?.DeepClone() ?? new JsonObject()
            };
            var data = Encoding.UTF8.GetBytes(envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            if (data.Length is 0 or > 65536)
                throw new AdminProtocolException("request_too_large", "The administration request is too large.");
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(data, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            CryptographicOperations.ZeroMemory(data);

            await ReadExactlyAsync(stream, header, cancellationToken);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size is 0 or > 1_048_576)
                throw new AdminProtocolException("protocol_error", "The server response is too large.");
            var response = new byte[size];
            await ReadExactlyAsync(stream, response, cancellationToken);
            JsonObject reply;
            try
            {
                reply = JsonNode.Parse(response)?.AsObject()
                        ?? throw new JsonException("Response is not an object.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response);
            }
            if (reply["version"]?.GetValue<int>() != 1 || reply["requestId"]?.GetValue<string>() != requestId)
                throw new AdminProtocolException("protocol_error", "The server response does not match the request.");
            if (reply["ok"]?.GetValue<bool>() != true)
            {
                var error = reply["error"]?.AsObject();
                throw new AdminProtocolException(error?["code"]?.GetValue<string>() ?? "remote_error",
                    error?["message"]?.GetValue<string>() ?? "The server rejected the request.");
            }
            return reply["payload"]?.DeepClone() ?? new JsonObject();
        }
        finally
        {
            requests.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (stream is not null)
        {
            try { await RequestAsync("logout", null, cancellationToken); }
            catch { }
        }
        await DisposeTransportAsync();
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await input.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0)
                throw new EndOfStreamException("The administration connection was closed.");
            read += count;
        }
    }

    public static string NormalizeFingerprint(string value)
    {
        var normalized = new string(value.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (normalized.Length != 64)
            throw new FormatException("The certificate fingerprint must contain 64 hexadecimal characters.");
        return normalized;
    }

    private async ValueTask DisposeTransportAsync()
    {
        if (stream is not null)
            await stream.DisposeAsync();
        stream = null;
        client?.Dispose();
        client = null;
        ServerId = "";
        Protocol = "";
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeTransportAsync();
        requests.Dispose();
    }
}

public sealed class AdminProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
