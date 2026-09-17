using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Briefcase.ServerManager.Bootstrap;

internal static class Program
{
    private const string ManifestResource = "payload/payload.manifest.json";

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var manifestStream = assembly.GetManifestResourceStream(ManifestResource)
                ?? throw new InvalidDataException("The embedded manager payload is missing.");
            var manifest = JsonNode.Parse(manifestStream)?.AsObject()
                ?? throw new InvalidDataException("The embedded manager manifest is invalid.");
            var version = manifest["version"]?.GetValue<string>()
                ?? throw new InvalidDataException("The embedded manager version is missing.");
            var payloadId = manifest["payloadSha256"]?.GetValue<string>()
                ?? throw new InvalidDataException("The embedded manager identity is missing.");
            if (payloadId.Length != 64 || !payloadId.All(Uri.IsHexDigit))
                throw new InvalidDataException("The embedded manager identity is invalid.");

            var root = CacheRoot();
            var target = Path.Combine(root, version, payloadId);
            if (!Validate(target, manifest))
                Extract(assembly, root, target, manifest);
            if (args.Length == 1 && args[0] == "--bootstrap-self-test")
                return Validate(target, manifest) ? 0 : 1;

            var executable = Path.Combine(target, manifest["executable"]!.GetValue<string>());
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false
            };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.Environment["BRIEFCASE_SERVER_MANAGER_HOME"] = AppContext.BaseDirectory;
            start.Environment["BRIEFCASE_SERVER_MANAGER_BOOTSTRAPPED"] = "1";
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The embedded server manager did not start.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return 1;
        }
    }

    private static string CacheRoot()
    {
        var baseDirectory = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ??
              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new InvalidOperationException("A private user cache directory is unavailable.");
        return Path.Combine(baseDirectory, "Briefcase", "ServerManager");
    }

    private static bool Validate(string directory, JsonObject manifest)
    {
        if (!Directory.Exists(directory) || IsLinked(directory)) return false;
        foreach (var node in manifest["files"]?.AsArray() ?? [])
        {
            if (node is not JsonObject item) return false;
            var name = SafeName(item["name"]?.GetValue<string>() ?? "");
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(path).Length != item["bytes"]?.GetValue<long>() ||
                !string.Equals(Digest(path), item["sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static void Extract(Assembly assembly, string root, string target, JsonObject manifest)
    {
        Directory.CreateDirectory(root);
        if (IsLinked(root)) throw new InvalidOperationException("The manager cache cannot be a linked directory.");
        var temporary = Path.Combine(root, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var node in manifest["files"]?.AsArray() ??
                     throw new InvalidDataException("The embedded file inventory is missing."))
            {
                var item = node?.AsObject() ?? throw new InvalidDataException("The embedded file inventory is invalid.");
                var name = SafeName(item["name"]?.GetValue<string>() ?? "");
                using var input = assembly.GetManifestResourceStream("payload/" + name)
                    ?? throw new InvalidDataException($"The embedded file {name} is missing.");
                var outputPath = Path.Combine(temporary, name);
                using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                if (new FileInfo(outputPath).Length != item["bytes"]?.GetValue<long>() ||
                    !string.Equals(Digest(outputPath), item["sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The embedded file {name} failed verification.");
            }
            if (!OperatingSystem.IsWindows())
            {
                var executable = Path.Combine(temporary, manifest["executable"]!.GetValue<string>());
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temporary, target);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The embedded payload contains an unsafe file name.");
        return name;
    }

    private static bool IsLinked(string path) =>
        Directory.Exists(path) && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;

    private static string Digest(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static void ShowError(string message)
    {
        if (OperatingSystem.IsWindows())
            _ = MessageBox(IntPtr.Zero, message, "Briefcase Server Manager", 0x10);
        else
            Console.Error.WriteLine("Briefcase Server Manager: " + message);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
