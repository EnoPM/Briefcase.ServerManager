using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Briefcase.ServerManager.Core;

public sealed record InstallProgress(string Stage, double Fraction, string Detail);
public sealed record InstalledRelease(string Version, BriefcaseInstallation Installation);

public sealed class BriefcaseReleaseInstaller(HttpClient? client = null)
{
    private const string Repository = "EnoPM/BriefcaseNative";
    private readonly HttpClient http = client ?? CreateClient();

    public static string FindPlatformDirectory(string selectedDirectory)
    {
        var selected = Path.GetFullPath(selectedDirectory);
        var platform = OperatingSystem.IsWindows() ? "Win64" : "Linux";
        var candidates = new[]
        {
            selected,
            Path.Combine(selected, "Binaries", platform),
            Path.Combine(selected, "DeceiveInc", "Binaries", platform)
        };
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var installation = new BriefcaseInstallation(candidate);
            if (File.Exists(installation.ShippingPath))
                return installation.PlatformDirectory;
        }
        throw new InvalidOperationException($"Select the dedicated server folder or its Binaries/{platform} directory.");
    }

    public async Task<InstalledRelease> InstallLatestAsync(string selectedDirectory,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new("Checking", 0.02, "Reading the latest Briefcase release…"));
        var root = FindPlatformDirectory(selectedDirectory);
        EnsurePlainDirectory(root);
        var installation = new BriefcaseInstallation(root);
        EnsureServerStopped(installation);
        var release = await ReadReleaseAsync(cancellationToken);
        var platform = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
        var version = release["tag_name"]?.GetValue<string>()?.TrimStart('v')
                      ?? throw new InvalidDataException("The release has no version.");
        if (!Version.TryParse(version, out _) || release["draft"]?.GetValue<bool>() == true ||
            release["prerelease"]?.GetValue<bool>() == true)
            throw new InvalidDataException("The latest Briefcase release is not stable.");
        var expectedName = $"BriefcaseNative-Server-{platform}-{version}.zip";
        var asset = release["assets"]?.AsArray().Select(x => x?.AsObject())
            .SingleOrDefault(x => x?["name"]?.GetValue<string>() == expectedName)
                    ?? throw new InvalidDataException($"The release does not contain {expectedName}.");
        ValidateAsset(asset, expectedName, version);

        var temporary = Path.Combine(Path.GetTempPath(), $"briefcase-{Guid.NewGuid():N}.zip");
        var stage = Path.Combine(root, $".briefcase-install-{Guid.NewGuid():N}");
        try
        {
            await DownloadAsync(new Uri(asset["browser_download_url"]!.GetValue<string>()), temporary,
                asset["size"]!.GetValue<long>(), asset["digest"]!.GetValue<string>()[7..], progress,
                cancellationToken);
            progress?.Report(new("Extracting", 0.62, "Validating and extracting the package…"));
            Directory.CreateDirectory(stage);
            ExtractSafe(temporary, stage, progress);
            var manifest = await ValidatePackageAsync(stage, installation, platform, version, cancellationToken);
            progress?.Report(new("Installing", 0.82, "Installing Briefcase into the server directory…"));
            await CommitAsync(stage, installation, manifest, progress, cancellationToken);
            progress?.Report(new("Complete", 1, $"Briefcase {version} is ready."));
            return new(version, installation);
        }
        finally
        {
            TryDeleteFile(temporary);
            TryDeleteDirectory(stage);
        }
    }

    private async Task<JsonObject> ReadReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024)
            throw new InvalidDataException("The GitHub release response is too large.");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (await JsonNode.ParseAsync(body, cancellationToken: cancellationToken))?.AsObject()
               ?? throw new InvalidDataException("GitHub returned an invalid release document.");
    }

    private static void ValidateAsset(JsonObject asset, string name, string version)
    {
        var size = asset["size"]?.GetValue<long>() ?? 0;
        var digest = asset["digest"]?.GetValue<string>() ?? "";
        var state = asset["state"]?.GetValue<string>() ?? "";
        var url = asset["browser_download_url"]?.GetValue<string>() ?? "";
        var expectedPrefix = $"https://github.com/{Repository}/releases/download/v{version}/";
        if (state != "uploaded" || size is <= 0 or > 536_870_912 ||
            digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest[7..].All(Uri.IsHexDigit) || url != expectedPrefix + name)
            throw new InvalidDataException("The Briefcase release asset metadata is invalid.");
    }

    private async Task DownloadAsync(Uri uri, string destination, long expectedSize, string expectedDigest,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri;
        if (final is null || final.Scheme != Uri.UriSchemeHttps || final.Host is not
            ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("The release download was redirected to an unexpected host.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                total += count;
                if (total > expectedSize || total > 536_870_912)
                    throw new InvalidDataException("The release download exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                hash.AppendData(buffer, 0, count);
                progress?.Report(new("Downloading", .08 + .5 * total / expectedSize,
                    $"Downloading Briefcase… {total / 1_048_576d:0.0} / {expectedSize / 1_048_576d:0.0} MiB"));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        if (total != expectedSize || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedDigest,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded release failed SHA-256 verification.");
    }

    private static void ExtractSafe(string archivePath, string stage, IProgress<InstallProgress>? progress)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > 4096)
            throw new InvalidDataException("The release archive has an invalid number of entries.");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
                throw new InvalidDataException("The release archive contains a symbolic link.");
            if (string.IsNullOrEmpty(entry.Name)) continue;
            total += entry.Length;
            if (entry.Length > 536_870_912 || total > 1_073_741_824)
                throw new InvalidDataException("The extracted release is too large.");
            var target = SafePath(stage, entry.FullName);
            if (!seen.Add(target)) throw new InvalidDataException("The release contains duplicate paths.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            progress?.Report(new("Extracting", .62 + .14 * seen.Count / archive.Entries.Count, entry.FullName));
        }
    }

    private static async Task<JsonObject> ValidatePackageAsync(string stage, BriefcaseInstallation installation,
        string platform, string version, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(stage, "Package.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))?.AsObject()
                       ?? throw new InvalidDataException("The release package manifest is invalid.");
        if (manifest["updateSchema"]?.GetValue<int>() != 1 || manifest["environment"]?.GetValue<string>() != "server" ||
            manifest["platform"]?.GetValue<string>() != platform || manifest["frameworkVersion"]?.GetValue<string>() != version)
            throw new InvalidDataException("The release package targets another environment.");
        var gameDigest = await DigestFileAsync(installation.ShippingPath, cancellationToken);
        if (!string.Equals(gameDigest, manifest["gameSha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This Briefcase release does not support the installed game server build.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Package.json" };
        foreach (var item in manifest["files"]?.AsArray() ?? throw new InvalidDataException("Missing file inventory."))
        {
            var row = item?.AsObject() ?? throw new InvalidDataException("Invalid file inventory row.");
            var relative = row["path"]?.GetValue<string>() ?? "";
            var file = SafePath(stage, relative);
            if (!listed.Add(relative.Replace('\\', '/')) || !File.Exists(file) ||
                new FileInfo(file).Length != row["bytes"]?.GetValue<long>() ||
                !string.Equals(await DigestFileAsync(file, cancellationToken), row["sha256"]?.GetValue<string>(),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package validation failed for {relative}.");
        }
        var actual = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(stage, x).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(listed)) throw new InvalidDataException("The release contains unlisted files.");
        return manifest;
    }

    private static async Task CommitAsync(string stage, BriefcaseInstallation installation, JsonObject manifest,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var backup = Path.Combine(installation.BriefcaseDirectory, "Updates", $"manager-backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(backup);
        var changed = new List<(string Target, string? Backup)>();
        try
        {
            var files = manifest["files"]!.AsArray().Select(x => x!.AsObject()["path"]!.GetValue<string>()).Append("Package.json").ToArray();
            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = files[index];
                var source = SafePath(stage, relative);
                var target = SafePath(installation.PlatformDirectory, relative);
                EnsureNoLinkedPath(installation.PlatformDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? saved = null;
                if (File.Exists(target))
                {
                    saved = SafePath(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(target, saved, true);
                }
                var temporary = target + ".manager-new";
                File.Copy(source, temporary, true);
                File.Move(temporary, target, true);
                changed.Add((target, saved));
                progress?.Report(new("Installing", .82 + .17 * (index + 1) / files.Length, relative));
            }
        }
        catch
        {
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                if (item.Backup is not null && File.Exists(item.Backup)) File.Copy(item.Backup, item.Target, true);
                else TryDeleteFile(item.Target);
            }
            throw;
        }
    }

    private static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("The package contains an unsafe path.");
        var normalized = relative.Replace('\\', '/');
        if (normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("The package contains an unsafe path.");
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The package path escaped the server directory.");
        return result;
    }

    private static void EnsurePlainDirectory(string root)
    {
        var current = new DirectoryInfo(root);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked installation directories are not supported.");
            current = current.Parent;
        }
    }

    private static void EnsureNoLinkedPath(string root, string relative)
    {
        var current = Path.GetFullPath(root);
        foreach (var part in relative.Replace('\\', '/').Split('/').SkipLast(1))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current)) continue;
            if ((new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked installation directories are not supported.");
        }
        var target = SafePath(root, relative);
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked installation files are not supported.");
    }

    private static void EnsureServerStopped(BriefcaseInstallation installation)
    {
        var name = Path.GetFileNameWithoutExtension(installation.ShippingPath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, installation.ShippingPath,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("Stop the dedicated server before installing Briefcase.");
                }
                catch (InvalidOperationException) { throw; }
                catch { }
            }
        }
    }

    private static async Task<string> DigestFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 4 };
        var result = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("Briefcase.ServerManager/1.0");
        result.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return result;
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
