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

JsonObject BalanceValue(string table, string row, string field, double saved) => new()
{
    ["id"] = $"{table}/{row}/{field}",
    ["table"] = table,
    ["row"] = row,
    ["field"] = field,
    ["saved"] = saved,
    ["active"] = saved,
    ["default"] = saved,
    ["editable"] = true,
    ["allowedRange"] = "0 to 100",
    ["presentation"] = new JsonObject { ["displayName"] = field + " label" },
    ["rowPresentation"] = new JsonObject { ["displayName"] = row + " label" }
};

var yumiCatalog = BalanceCatalogBuilder.Build(new JsonObject
{
    ["group"] = "Yumi",
    ["groupLabels"] = new JsonObject
    {
        ["Yumi"] = new JsonObject { ["displayName"] = "Yumi" }
    },
    ["entries"] = new JsonArray
    {
        BalanceValue("DT_Yumi_ActivesBalancing", "Yumi_Passive_Mod2", "Duration", 12.5),
        BalanceValue("DT_Balancing_Projectiles", "Yumi_ActiveProjectile", "Speed", 950),
        BalanceValue("DT_Balancing_Projectiles", "Yumi_Weapon_Mod2_Regular", "Gravity", 0.2),
        BalanceValue("DT_Projectiles_Balancing", "Yumi_Weapon_Mod2_Split", "Damage", 15)
    }
});
var yumiPassive = yumiCatalog.Entries.Single(x => x.Row == "Yumi_Passive_Mod2");
Check(yumiPassive.Path.Category == BalanceCategory.Passives && yumiPassive.Path.Variant == 3,
    "Yumi passive table exception classification");
var yumiActive = yumiCatalog.Entries.Single(x => x.Row == "Yumi_ActiveProjectile");
Check(yumiActive.Path.Category == BalanceCategory.Expertises && yumiActive.Path.Variant == 1,
    "Yumi active projectile classification");
Check(yumiCatalog.Entries.Count(x => x.Row.Contains("Yumi_Weapon_Mod2", StringComparison.Ordinal)) == 2 &&
      yumiCatalog.Slices.Any(x => x.Category == BalanceCategory.Weapons && x.Variant == 3),
    "Yumi regular and split weapon grouping");
Check(!yumiCatalog.Slices.Any(x => x.Category == BalanceCategory.Passives && x.Variant is 1 or 2),
    "Empty character variants remain hidden");
Check(yumiCatalog.Schema.OptionCount == yumiCatalog.Entries.Count &&
      yumiCatalog.Schema.Categories.Single(x => x.Category == BalanceCategory.Weapons)
          .Variants.Single(x => x.Number == 3).Components.Count == 2,
    "Generated C# balancing hierarchy");
Check(yumiCatalog.Entries.All(x => x.Constraint.Minimum == 0 && x.Constraint.Maximum == 100) &&
      yumiCatalog.Entries.Single(x => x.Field == "Gravity").Constraint.Increment == .01m,
    "Server ranges become typed numeric constraints");
Check(yumiCatalog.Select(null, "950").Single().Field == "Speed" &&
      yumiCatalog.Select(null, "Gravity label").Single().Field == "Gravity",
    "Balance search includes values and labels");

var exceptionCatalog = BalanceCatalogBuilder.Build(new JsonObject
{
    ["group"] = "Vigil",
    ["entries"] = new JsonArray
    {
        BalanceValue("DT_Vigil_PassivesBalancing", "Vigil_Passive_ThrowableDevice", "Range", 8),
        BalanceValue("DT_Balancing_HitscanWeapons", "Vigil_Weapon_ADS_Mod1", "Damage", 20)
    }
});
Check(exceptionCatalog.Entries.Single(x => x.Row.Contains("Throwable", StringComparison.Ordinal)).Path.Variant == 0,
    "Unnumbered Vigil passive fallback");
var vigilAds = exceptionCatalog.Entries.Single(x => x.Row.Contains("ADS", StringComparison.Ordinal));
Check(vigilAds.Path.Category == BalanceCategory.Weapons && vigilAds.Path.Variant == 2 &&
      vigilAds.Path.Component == "ads" && vigilAds.ComponentLabel == "ADS",
    "ADS weapon component identity and code-defined label");

var larcinCatalog = BalanceCatalogBuilder.Build(new JsonObject
{
    ["group"] = "Larcin",
    ["entries"] = new JsonArray
    {
        BalanceValue("DT_Larcin_ActivesBalancing", "Larcin_Active_Prototype", "Cooldown", 30)
    }
});
var prototype = larcinCatalog.Entries.Single();
Check(prototype.Path.Category == BalanceCategory.Expertises && prototype.Path.Variant == 0 &&
      larcinCatalog.Slices.Single().Label == "Additional data", "Larcin prototype fallback grouping");

var gameCatalog = BalanceCatalogBuilder.Build(new JsonObject
{
    ["group"] = "Commun",
    ["entries"] = new JsonArray
    {
        BalanceValue("DT_CommonBalancing", "Movement", "WalkSpeed", 400),
        BalanceValue("DT_CommonBalancing", "Movement", "SprintSpeed", 650)
    }
});
Check(gameCatalog.Root == BalanceRoot.Game &&
      gameCatalog.Entries.All(x => x.Path.Category == BalanceCategory.Shared) &&
      gameCatalog.Slices.Single().Count == 2, "Shared game balancing grouping");
var constrained = gameCatalog.Entries[0];
Check(constrained.Constraint.TryValidate(JsonValue.Create(50), constrained.ValueType, out _) &&
      !constrained.Constraint.TryValidate(JsonValue.Create(101), constrained.ValueType, out _),
    "Balance constraints reject out-of-range values");

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
