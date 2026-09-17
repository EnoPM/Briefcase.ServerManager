using Briefcase.ServerManager.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

var checks = 0;
void Check(bool value, string message)
{
    checks++;
    if (!value) throw new InvalidOperationException(message);
}
void Reject(Action action, string message)
{
    checks++;
    try { action(); }
    catch { return; }
    throw new InvalidOperationException(message);
}

var ipv4 = AdminEndpoint.Parse("127.0.0.1:32189");
Check(ipv4.Host == "127.0.0.1" && ipv4.Port == 32189, "IPv4 endpoint parsing");
var ipv6 = AdminEndpoint.Parse("[::1]:32189");
Check(ipv6.Host == "::1" && ipv6.ToString() == "[::1]:32189", "IPv6 endpoint parsing");
Reject(() => AdminEndpoint.Parse("127.0.0.1"), "Missing port accepted");
Reject(() => AdminEndpoint.Parse("host:0"), "Invalid port accepted");
Check(AdminConnection.NormalizeFingerprint(string.Join(':', Enumerable.Repeat("AA", 32))) == new string('a', 64),
    "Fingerprint normalization");
Reject(() => AdminConnection.NormalizeFingerprint("abcd"), "Short fingerprint accepted");

using (var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
{
    var echoPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
    var responder = Task.Run(async () =>
    {
        var request = await echo.ReceiveAsync();
        await echo.SendAsync(request.Buffer, request.RemoteEndPoint);
    });
    var probe = await new ServerQueryClient().ProbeAsync("127.0.0.1", echoPort, TimeSpan.FromSeconds(2));
    await responder;
    Check(probe.Reachable && probe.LatencyMilliseconds >= 0, "Query port echo probe");
}

var fixture = Path.Combine(Path.GetTempPath(), "briefcase-manager-contracts-" + Guid.NewGuid().ToString("N"));
try
{
    var platform = Path.Combine(fixture, "DeceiveInc", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux");
    Directory.CreateDirectory(Path.Combine(platform, "Briefcase"));
    var shipping = Path.Combine(platform, OperatingSystem.IsWindows()
        ? "DeceiveIncServer-Win64-Shipping.exe" : "DeceiveIncServer-Linux-Shipping");
    File.WriteAllBytes(shipping, [1, 2, 3]);
    Check(BriefcaseReleaseInstaller.FindPlatformDirectory(fixture) == Path.GetFullPath(platform),
        "Server root discovery");
    Check(BriefcaseInstallation.Locate(platform).PlatformDirectory == Path.GetFullPath(platform),
        "Installed manager discovery");

    const string version = "9.8.7";
    var releasePlatform = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    var launcherName = OperatingSystem.IsWindows() ? "Briefcase.ServerLauncher.exe" : "Briefcase.ServerLauncher";
    var launcherBytes = Encoding.UTF8.GetBytes("native launcher fixture");
    var gameDigest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(shipping)));
    var manifest = new JsonObject
    {
        ["updateSchema"] = 1, ["environment"] = "server", ["platform"] = releasePlatform,
        ["frameworkVersion"] = version, ["gameSha256"] = gameDigest,
        ["files"] = new JsonArray(new JsonObject
        {
            ["path"] = launcherName, ["bytes"] = launcherBytes.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(launcherBytes))
        })
    };
    byte[] archiveBytes;
    await using (var archiveStream = new MemoryStream())
    {
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, true))
        {
            await using (var output = archive.CreateEntry(launcherName).Open())
                await output.WriteAsync(launcherBytes);
            await using (var output = archive.CreateEntry("Package.json").Open())
                await output.WriteAsync(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        archiveBytes = archiveStream.ToArray();
    }
    var archiveDigest = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
    var assetName = $"BriefcaseNative-Server-{releasePlatform}-{version}.zip";
    var assetUrl = $"https://github.com/EnoPM/BriefcaseNative/releases/download/v{version}/{assetName}";
    var release = new JsonObject
    {
        ["tag_name"] = $"v{version}", ["draft"] = false, ["prerelease"] = false,
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = assetName, ["state"] = "uploaded", ["size"] = archiveBytes.Length,
            ["digest"] = $"sha256:{archiveDigest}", ["browser_download_url"] = assetUrl
        })
    };
    using var client = new HttpClient(new FixtureHandler(release.ToJsonString(), archiveBytes));
    var installed = await new BriefcaseReleaseInstaller(client).InstallLatestAsync(fixture);
    Check(installed.Version == version && File.ReadAllBytes(Path.Combine(platform, launcherName)).SequenceEqual(launcherBytes),
        "Verified release installation");
    Check(File.Exists(Path.Combine(platform, "Package.json")), "Installed package identity");
}
finally
{
    if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
}

Console.WriteLine($"PASS {checks} Briefcase Server Manager contracts");

file sealed class FixtureHandler(string release, byte[] archive) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isRelease = request.RequestUri!.AbsoluteUri.EndsWith("/releases/latest", StringComparison.Ordinal);
        HttpContent content = isRelease
            ? new StringContent(release, Encoding.UTF8, "application/json")
            : new ByteArrayContent(archive);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = content
        });
    }
}
