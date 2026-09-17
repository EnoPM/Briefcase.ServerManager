# Briefcase Server Manager

Briefcase Server Manager is the graphical companion for
[BriefcaseNative](https://github.com/EnoPM/BriefcaseNative). It is intended for
community server owners who do not want to use a terminal or install the modded
game client.

The application can:

- locate a Deceive Inc. dedicated server installation;
- download the latest official BriefcaseNative server release;
- verify the release size and GitHub SHA-256 digest;
- validate the package manifest and supported game build before installation;
- install or update Briefcase with progress and rollback protection;
- start the native headless server launcher;
- generate the TLS administration identity and a strong password;
- connect to a local or remote Briefcase administration service;
- manage mods, server settings, balance settings, logs and restart requests.

The native `Briefcase.ServerLauncher` remains the server process launcher. This
UI starts it and never replaces its update, injection or headless behavior.

## Install

Download the archive for your operating system from the latest release, extract
it anywhere and run `Briefcase.ServerManager`. The .NET runtime is not required.

Choose the dedicated server folder created by SteamCMD. The manager accepts the
server root, `DeceiveInc/Binaries/Win64`, or `DeceiveInc/Binaries/Linux`.

Stop the dedicated server before installing or updating Briefcase. The manager
refuses to replace files while that exact Shipping executable is running.

## Security

The installer accepts release metadata only from `EnoPM/BriefcaseNative`, follows
downloads only to GitHub release hosts, verifies the GitHub SHA-256 digest and
validates every package file against `Package.json`. Archive traversal, linked
installation directories, oversized files and unsupported game builds are
rejected.

The administration client validates the exact SHA-256 fingerprint of the server
certificate before sending the password. TLS 1.2 or TLS 1.3 is required.

## Development

Open `Briefcase.ServerManager.slnx` in JetBrains Rider. The UI uses Avalonia
12.1.2 and targets .NET 10 Native AOT.

```powershell
dotnet restore Briefcase.ServerManager.slnx
dotnet build Briefcase.ServerManager.slnx -c Release
dotnet run --project tests/Briefcase.ServerManager.Contracts -c Release
dotnet publish src/Briefcase.ServerManager -c Release -r win-x64
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for repository rules.
