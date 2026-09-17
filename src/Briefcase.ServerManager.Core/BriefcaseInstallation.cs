using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Briefcase.ServerManager.Core;

public sealed class BriefcaseInstallation
{
    public string PlatformDirectory { get; }
    public string BriefcaseDirectory => Path.Combine(PlatformDirectory, "Briefcase");
    public string AdminDirectory => Path.Combine(BriefcaseDirectory, "Admin");
    public string PairingFile => Path.Combine(AdminDirectory, "pairing.json");
    public string ServerFile => Path.Combine(AdminDirectory, "server.json");
    public string FrameworkLog => Path.Combine(BriefcaseDirectory, "Logs", "BriefcaseNative.log");
    public string LauncherPath => Path.Combine(PlatformDirectory,
        OperatingSystem.IsWindows() ? "Briefcase.ServerLauncher.exe" : "Briefcase.ServerLauncher");
    public string ShippingPath => Path.Combine(PlatformDirectory,
        OperatingSystem.IsWindows() ? "DeceiveIncServer-Win64-Shipping.exe" : "DeceiveIncServer-Linux-Shipping");
    public string SetupPath => Path.Combine(BriefcaseDirectory, "Core", "Tools",
        OperatingSystem.IsWindows() ? "Briefcase.AdminSetup.exe" : "Briefcase.AdminSetup");

    public BriefcaseInstallation(string platformDirectory)
    {
        PlatformDirectory = Path.GetFullPath(platformDirectory);
    }

    public static BriefcaseInstallation Locate(string applicationDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(applicationDirectory));
        for (var depth = 0; depth < 5 && current is not null; depth++, current = current.Parent)
        {
            var candidate = new BriefcaseInstallation(current.FullName);
            if (File.Exists(candidate.ShippingPath) && Directory.Exists(candidate.BriefcaseDirectory))
                return candidate;
        }
        throw new InvalidOperationException(
            "Briefcase.ServerManager must be installed in the server Binaries/Win64 or Binaries/Linux directory.");
    }

    public Process StartServer()
    {
        if (!File.Exists(LauncherPath))
            throw new FileNotFoundException("Briefcase.ServerLauncher is missing.", LauncherPath);
        return Process.Start(new ProcessStartInfo
        {
            FileName = LauncherPath,
            WorkingDirectory = PlatformDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The native server launcher did not start.");
    }

    public async Task<LocalAdministration> ReadAdministrationAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PairingFile) || !File.Exists(ServerFile))
            throw new InvalidOperationException("Administration is not configured for this server.");
        var pairing = JsonNode.Parse(await File.ReadAllTextAsync(PairingFile, cancellationToken))?.AsObject()
                      ?? throw new InvalidDataException("Invalid pairing.json.");
        var server = JsonNode.Parse(await File.ReadAllTextAsync(ServerFile, cancellationToken))?.AsObject()
                     ?? throw new InvalidDataException("Invalid server.json.");
        return new(
            pairing["endpoint"]?.GetValue<string>() ?? throw new InvalidDataException("Missing endpoint."),
            pairing["fingerprint"]?.GetValue<string>() ?? throw new InvalidDataException("Missing fingerprint."),
            server["password"]?.GetValue<string>() ?? throw new InvalidDataException("Missing password."));
    }

    public async Task<LocalAdministration> ConfigureAdministrationAsync(
        string listenAddress, int port, string publicEndpoint, CancellationToken cancellationToken = default)
    {
        if (File.Exists(ServerFile) || File.Exists(PairingFile))
            throw new InvalidOperationException("Administration is already configured. The existing identity was preserved.");
        _ = AdminEndpoint.Parse(publicEndpoint);
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(listenAddress))
            throw new ArgumentException("The listen address or port is invalid.");
        if (!File.Exists(SetupPath))
            throw new FileNotFoundException("Briefcase.AdminSetup is missing.", SetupPath);

        var start = new ProcessStartInfo
        {
            FileName = SetupPath,
            WorkingDirectory = PlatformDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("--root");
            start.ArgumentList.Add(BriefcaseDirectory);
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add(listenAddress);
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--endpoint");
            start.ArgumentList.Add(publicEndpoint);
            start.ArgumentList.Add("--generate-password");
        }
        else
        {
            start.ArgumentList.Add(BriefcaseDirectory);
            start.ArgumentList.Add(listenAddress);
            start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(publicEndpoint);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Administration setup did not start.");
        await process.WaitForExitAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Administration setup failed." : error.Trim());
        return await ReadAdministrationAsync(cancellationToken);
    }
}

public sealed record LocalAdministration(string Endpoint, string Fingerprint, string Password);
