using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Briefcase.ServerManager.Core;

namespace Briefcase.ServerManager;

public sealed class MainWindow : Window
{
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#18141F"));
    private static readonly IBrush BorderColor = new SolidColorBrush(Color.Parse("#5C4F6E"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#C475F7"));
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#27865D"));
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#2877B7"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#B54250"));
    private BriefcaseInstallation? installation;
    private readonly BriefcaseReleaseInstaller installer = new();
    private readonly AdminConnection administration = new();
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border noticeBorder = new() { IsVisible = false, Padding = new Thickness(12), CornerRadius = new CornerRadius(6) };
    private readonly TextBlock processState = new();
    private readonly TextBlock installPath = new();
    private readonly TextBox targetDirectory = new() { MinHeight = 36 };
    private readonly ProgressBar installProgress = new() { Minimum = 0, Maximum = 100, Height = 12, IsVisible = false };
    private readonly TextBlock installStage = new() { TextWrapping = TextWrapping.Wrap, Opacity = .72 };
    private readonly ContentControl contentHost = new();
    private readonly List<Button> navigation = [];
    private readonly TextBlock adminState = new() { Text = "Disconnected" };
    private readonly TextBox listen = new() { Text = "127.0.0.1", MinHeight = 36 };
    private readonly NumericUpDown port = new() { Value = AdminEndpoint.DefaultPort, Minimum = 1, Maximum = 65535, Increment = 1, MinHeight = 36 };
    private readonly TextBox publicEndpoint = new() { Text = AdminEndpoint.DefaultValue, MinHeight = 36 };
    private readonly TextBox endpoint = new() { Text = AdminEndpoint.DefaultValue, MinHeight = 36 };
    private readonly TextBox fingerprint = new() { MinHeight = 36 };
    private readonly TextBox password = new() { PasswordChar = '●', MinHeight = 36 };
    private readonly TextBlock overview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel modsPanel = new() { Spacing = 12 };
    private readonly StackPanel selectionPanel = new() { Spacing = 8 };
    private readonly StackPanel configurationPanel = new() { Spacing = 12 };
    private readonly StackPanel balancePanel = new() { Spacing = 12 };
    private readonly TextBox logs = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 480 };
    private readonly ComboBox logSource = new() { ItemsSource = new[] { "framework", "game" }, SelectedIndex = 0, MinWidth = 180 };
    private readonly ComboBox balanceGroup = new() { MinWidth = 220 };
    private readonly DispatcherTimer processTimer;
    private JsonArray mods = [];
    private JsonObject? selection;
    private JsonObject? balance;
    private bool busy;

    public MainWindow()
    {
        Title = "Briefcase Server Manager";
        Width = 1240;
        Height = 820;
        MinWidth = 920;
        MinHeight = 640;
        Background = new SolidColorBrush(Color.Parse("#110D14"));
        try
        {
            installation = BriefcaseInstallation.Locate(Program.ApplicationDirectory);
            installPath.Text = installation.PlatformDirectory;
            targetDirectory.Text = installation.PlatformDirectory;
        }
        catch (Exception error)
        {
            installPath.Text = error.Message;
            targetDirectory.Text = Program.ApplicationDirectory;
        }

        noticeBorder.Child = notice;
        Content = BuildLayout();
        processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        processTimer.Tick += (_, _) => UpdateProcessState();
        processTimer.Start();
        Opened += async (_, _) =>
        {
            UpdateProcessState();
            if (installation is not null && File.Exists(installation.PairingFile))
                await LoadLocalIdentityAsync();
        };
        Closing += (_, _) => administration.DisposeAsync().AsTask().GetAwaiter().GetResult();
        balanceGroup.SelectionChanged += async (_, _) =>
        {
            if (!busy && administration.Connected && balanceGroup.SelectedItem is string group)
                await LoadBalanceAsync(group);
        };
    }

    private Control BuildLayout()
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(22, 18, 22, 12)
        };
        var title = new StackPanel { Spacing = 2 };
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        identity.Children.Add(new TextBlock { Text = "BRIEFCASE", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Accent });
        identity.Children.Add(new TextBlock { Text = "NATIVE  /  SERVER MANAGER", VerticalAlignment = VerticalAlignment.Center, Opacity = .62 });
        title.Children.Add(identity);
        title.Children.Add(new TextBlock { Text = "Install, launch and administer your dedicated server", Opacity = .62 });
        header.Children.Add(title);
        adminState.VerticalAlignment = VerticalAlignment.Center;
        adminState.FontWeight = FontWeight.SemiBold;
        Grid.SetColumn(adminState, 1);
        header.Children.Add(adminState);

        var pages = new (string Name, Control Content)[]
        {
            ("Home", BuildHome()), ("Administration", BuildConnection()),
            ("Mods", Scroll(modsPanel)), ("Server", BuildServer()),
            ("Configuration", Scroll(configurationPanel)), ("Balance", BuildBalance())
        };
        var nav = new StackPanel { Spacing = 8, Margin = new Thickness(18, 8, 10, 18) };
        foreach (var page in pages)
        {
            var button = new Button
            {
                Content = page.Name, Tag = page.Content, MinHeight = 42,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8), Background = Brushes.Transparent
            };
            button.Click += (_, _) => SelectPage(button);
            navigation.Add(button);
            nav.Children.Add(button);
        }
        contentHost.Content = pages[0].Content;
        navigation[0].Background = new SolidColorBrush(Color.Parse("#3D2452"));
        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("180,*"),
            Margin = new Thickness(4, 0, 18, 18)
        };
        workspace.Children.Add(nav);
        contentHost.Margin = new Thickness(8);
        Grid.SetColumn(contentHost, 1);
        workspace.Children.Add(contentHost);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        root.Children.Add(header);
        Grid.SetRow(noticeBorder, 1);
        noticeBorder.Margin = new Thickness(22, 0, 22, 12);
        root.Children.Add(noticeBorder);
        Grid.SetRow(workspace, 2);
        root.Children.Add(workspace);
        return root;
    }

    private void SelectPage(Button selected)
    {
        foreach (var button in navigation)
            button.Background = ReferenceEquals(button, selected)
                ? new SolidColorBrush(Color.Parse("#3D2452")) : Brushes.Transparent;
        contentHost.Content = selected.Tag;
    }

    private Control BuildHome()
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(12) };
        var install = new StackPanel { Spacing = 10 };
        install.Children.Add(Heading("Install or update Briefcase"));
        install.Children.Add(new TextBlock
        {
            Text = "Select the Deceive Inc. dedicated server folder. The manager downloads the latest official release, verifies its GitHub SHA-256 digest and installs it beside the Shipping executable.",
            TextWrapping = TextWrapping.Wrap, Opacity = .72
        });
        install.Children.Add(FormRow("Server directory", targetDirectory));
        var installActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        installActions.Children.Add(ActionButton("Browse", Info, BrowseServerDirectoryAsync));
        installActions.Children.Add(ActionButton("Install latest release", Success, InstallLatestAsync));
        install.Children.Add(installActions);
        install.Children.Add(installProgress);
        install.Children.Add(installStage);
        body.Children.Add(Card(install));
        var status = new StackPanel { Spacing = 8 };
        status.Children.Add(Heading("Installation"));
        status.Children.Add(Labelled("Platform directory", installPath));
        status.Children.Add(Labelled("Server process", processState));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(ActionButton("Start server", Success, async () => await StartServerAsync()));
        actions.Children.Add(ActionButton("Refresh", Info, () => { UpdateProcessState(); return Task.CompletedTask; }));
        status.Children.Add(actions);
        body.Children.Add(Card(status));

        var setup = new StackPanel { Spacing = 10 };
        setup.Children.Add(Heading("Configure administration"));
        setup.Children.Add(new TextBlock
        {
            Text = "Generate the server identity and a strong administrator password. Use 127.0.0.1 for local-only access or 0.0.0.0 with a reachable public endpoint for remote access.",
            TextWrapping = TextWrapping.Wrap, Opacity = .72
        });
        setup.Children.Add(FormRow("Listen address", listen));
        setup.Children.Add(FormRow("Administration port", port));
        setup.Children.Add(FormRow("Public endpoint", publicEndpoint));
        var setupActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        setupActions.Children.Add(ActionButton("Generate administration password", Success, ConfigureAdministrationAsync));
        setupActions.Children.Add(ActionButton("Load existing identity", Info, LoadLocalIdentityAsync));
        setup.Children.Add(setupActions);
        body.Children.Add(Card(setup));
        return Scroll(body);
    }

    private async Task BrowseServerDirectoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Deceive Inc. dedicated server folder",
            AllowMultiple = false
        });
        if (folders.Count > 0)
            targetDirectory.Text = folders[0].Path.LocalPath;
    }

    private async Task InstallLatestAsync()
    {
        await GuardAsync(async () =>
        {
            installProgress.IsVisible = true;
            installProgress.Value = 0;
            var progress = new Progress<InstallProgress>(value =>
            {
                installProgress.Value = value.Fraction * 100;
                installStage.Text = value.Detail;
            });
            var result = await installer.InstallLatestAsync(targetDirectory.Text ?? "", progress);
            installation = result.Installation;
            installPath.Text = installation.PlatformDirectory;
            targetDirectory.Text = installation.PlatformDirectory;
            Show($"Briefcase {result.Version} was installed and verified.", "success");
            UpdateProcessState();
            if (File.Exists(installation.PairingFile)) await LoadLocalIdentityAsync();
        });
    }

    private Control BuildConnection()
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(12) };
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(Heading("Administration connection"));
        form.Children.Add(FormRow("Endpoint", endpoint));
        form.Children.Add(FormRow("Certificate fingerprint", fingerprint));
        form.Children.Add(FormRow("Administrator password", password));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(ActionButton("Connect", Success, ConnectAsync));
        actions.Children.Add(ActionButton("Refresh", Info, RefreshAdministrationAsync));
        actions.Children.Add(ActionButton("Disconnect", Danger, DisconnectAsync));
        form.Children.Add(actions);
        body.Children.Add(Card(form));
        var summary = new StackPanel { Spacing = 8 };
        summary.Children.Add(Heading("Server status"));
        summary.Children.Add(overview);
        body.Children.Add(Card(summary));
        return Scroll(body);
    }

    private Control BuildServer()
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(12) };
        var selectionCard = new StackPanel { Spacing = 10 };
        selectionCard.Children.Add(Heading("Enabled mods"));
        selectionCard.Children.Add(selectionPanel);
        selectionCard.Children.Add(ActionButton("Save mod selection", Success, SaveSelectionAsync));
        body.Children.Add(Card(selectionCard));
        var logCard = new StackPanel { Spacing = 10 };
        logCard.Children.Add(Heading("Logs"));
        var logActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        logActions.Children.Add(logSource);
        logActions.Children.Add(ActionButton("Refresh logs", Info, LoadLogsAsync));
        logCard.Children.Add(logActions);
        logCard.Children.Add(logs);
        body.Children.Add(Card(logCard));
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Heading("Restart"),
                new TextBlock { Text = "Restart through Briefcase to apply saved settings and mod updates.", Opacity = .72 },
                ActionButton("Restart server", Danger, RestartServerAsync)
            }
        }));
        return Scroll(body);
    }

    private Control BuildBalance()
    {
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(12) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        top.Children.Add(new TextBlock { Text = "Character or group", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold });
        top.Children.Add(balanceGroup);
        top.Children.Add(ActionButton("Reload", Info, async () =>
        {
            if (balanceGroup.SelectedItem is string group) await LoadBalanceAsync(group);
        }));
        body.Children.Add(Card(top));
        body.Children.Add(balancePanel);
        return Scroll(body);
    }

    private async Task StartServerAsync()
    {
        await GuardAsync(async () =>
        {
            _ = installation?.StartServer() ?? throw new InvalidOperationException("No valid installation was found.");
            Show("The native launcher started. Updates are checked before the dedicated server starts.", "success");
            await Task.Delay(1200);
            UpdateProcessState();
        });
    }

    private async Task ConfigureAdministrationAsync()
    {
        await GuardAsync(async () =>
        {
            var target = installation ?? throw new InvalidOperationException("No valid installation was found.");
            var identity = await target.ConfigureAdministrationAsync(listen.Text ?? "", (int)(port.Value ?? 0),
                publicEndpoint.Text ?? "");
            ApplyIdentity(identity);
            Show("Administration configured. The password is ready for this session and stored privately by the server.", "success");
        });
    }

    private async Task LoadLocalIdentityAsync()
    {
        await GuardAsync(async () =>
        {
            var identity = await (installation ?? throw new InvalidOperationException("No valid installation was found."))
                .ReadAdministrationAsync();
            ApplyIdentity(identity);
            Show("The local administration identity was loaded.", "info");
        });
    }

    private void ApplyIdentity(LocalAdministration identity)
    {
        endpoint.Text = identity.Endpoint;
        fingerprint.Text = identity.Fingerprint;
        password.Text = identity.Password;
    }

    private async Task ConnectAsync()
    {
        await GuardAsync(async () =>
        {
            await administration.ConnectAsync(endpoint.Text ?? "", fingerprint.Text ?? "", password.Text ?? "");
            password.Text = "";
            adminState.Text = $"Connected · {administration.Protocol}";
            await RefreshAdministrationCoreAsync();
            Show("Authenticated administration connection established.", "success");
        });
    }

    private async Task DisconnectAsync()
    {
        await GuardAsync(async () =>
        {
            await administration.LogoutAsync();
            adminState.Text = "Disconnected";
            overview.Text = "";
            ClearAdministrationViews();
            Show("Administration disconnected.", "info");
        });
    }

    private async Task RefreshAdministrationAsync() => await GuardAsync(RefreshAdministrationCoreAsync);

    private async Task RefreshAdministrationCoreAsync()
    {
        var status = (await administration.RequestAsync("server.status")).AsObject();
        mods = (await administration.RequestAsync("mods.list")).AsArray();
        overview.Text =
            $"Briefcase {status["framework"]}  ·  Game {status["gameBuild"]}\n" +
            $"Unreal: {status["unreal"]}  ·  Backend state: {status["backendState"]}\n" +
            $"Loaded mods: {status["loadedMods"]}/{status["discoveredMods"]}  ·  Uptime: {FormatUptime(status["uptimeSeconds"]?.GetValue<long>() ?? 0)}\n" +
            $"Server identity: {administration.ServerId}";
        await LoadModsAsync();
        if ((status["administrationVersion"]?.GetValue<int>() ?? 0) >= 2)
        {
            await LoadSelectionAsync();
            await LoadConfigurationAsync();
            var first = (await administration.RequestAsync("balance.read")).AsObject();
            balance = first;
            var groups = first["groups"]?.AsObject().Select(x => x.Key).ToArray() ?? [];
            balanceGroup.ItemsSource = groups;
            if (groups.Length > 0)
                balanceGroup.SelectedIndex = 0;
        }
        Show("Server data refreshed.", "success");
    }

    private async Task LoadModsAsync()
    {
        modsPanel.Children.Clear();
        foreach (var node in mods)
        {
            if (node is not JsonObject mod) continue;
            var card = new StackPanel { Spacing = 8 };
            card.Children.Add(new TextBlock
            {
                Text = $"{mod["name"]}  {mod["version"]}", FontSize = 17, FontWeight = FontWeight.Bold
            });
            card.Children.Add(new TextBlock
            {
                Text = $"{mod["author"]} · State {mod["state"]}" +
                       (string.IsNullOrWhiteSpace(mod["error"]?.GetValue<string>()) ? "" : $" · {mod["error"]}"),
                Opacity = .7, TextWrapping = TextWrapping.Wrap
            });
            if (mod["editable"]?.GetValue<bool>() == true)
            {
                var id = mod["id"]?.GetValue<string>() ?? "";
                var settings = (await administration.RequestAsync("mod.config.read", new JsonObject { ["modId"] = id })).AsObject();
                var editor = new SettingsEditor(settings);
                card.Children.Add(editor.View);
                card.Children.Add(ActionButton("Save changes", Success, async () =>
                {
                    await GuardAsync(async () =>
                    {
                        var payload = new JsonObject
                        {
                            ["modId"] = id,
                            ["expectedRevision"] = editor.Revision,
                            ["writeId"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                            ["values"] = editor.ReadValues()
                        };
                        await administration.RequestAsync("mod.config.write", payload);
                        await RefreshAdministrationCoreAsync();
                    });
                }));
            }
            modsPanel.Children.Add(Card(card));
        }
        if (modsPanel.Children.Count == 0)
            modsPanel.Children.Add(Card(new TextBlock { Text = "No server mods were discovered." }));
    }

    private async Task LoadSelectionAsync()
    {
        selection = (await administration.RequestAsync("mods.selection.read")).AsObject();
        var enabled = selection["saved"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").ToHashSet() ?? [];
        selectionPanel.Children.Clear();
        foreach (var node in mods)
        {
            if (node is not JsonObject mod) continue;
            var id = mod["id"]?.GetValue<string>() ?? "";
            selectionPanel.Children.Add(new CheckBox
            {
                Content = mod["name"]?.GetValue<string>() ?? id,
                Tag = id,
                IsChecked = enabled.Contains(id),
                MinHeight = 32
            });
        }
    }

    private async Task SaveSelectionAsync()
    {
        await GuardAsync(async () =>
        {
            if (selection is null) throw new InvalidOperationException("Refresh the server first.");
            var enabled = new JsonArray(selectionPanel.Children.OfType<CheckBox>()
                .Where(x => x.IsChecked == true).Select(x => (JsonNode?)JsonValue.Create((string)x.Tag!)).ToArray());
            selection = (await administration.RequestAsync("mods.selection.write", new JsonObject
            {
                ["expectedRevision"] = selection["revision"]?.DeepClone(), ["enabledMods"] = enabled
            })).AsObject();
            await LoadSelectionAsync();
            Show("The mod selection was saved for the next server start.", "success");
        });
    }

    private async Task LoadConfigurationAsync()
    {
        var document = (await administration.RequestAsync("server.config.read")).AsObject();
        configurationPanel.Children.Clear();
        var editor = new SettingsEditor(document);
        configurationPanel.Children.Add(editor.View);
        configurationPanel.Children.Add(ActionButton("Save server configuration", Success, async () =>
        {
            await GuardAsync(async () =>
            {
                await administration.RequestAsync("server.config.write", new JsonObject
                {
                    ["expectedRevision"] = editor.Revision, ["values"] = editor.ReadValues()
                });
                await LoadConfigurationAsync();
                Show("Server configuration saved. Restart to apply it.", "success");
            });
        }));
    }

    private async Task LoadLogsAsync()
    {
        await GuardAsync(async () =>
        {
            var result = (await administration.RequestAsync("server.logs", new JsonObject
            {
                ["source"] = logSource.SelectedItem?.ToString() ?? "framework"
            })).AsObject();
            logs.Text = result["text"]?.GetValue<string>() ?? "";
            Show(result["truncated"]?.GetValue<bool>() == true ? "The displayed log was truncated." : "Log refreshed.", "info");
        });
    }

    private async Task RestartServerAsync()
    {
        await GuardAsync(async () =>
        {
            await administration.RequestAsync("server.restart");
            await administration.DisposeAsync();
            adminState.Text = "Restarting";
            Show("Server restart requested. Reconnect after startup completes.", "warning");
        });
    }

    private async Task LoadBalanceAsync(string group)
    {
        await GuardAsync(async () =>
        {
            balance = (await administration.RequestAsync("balance.read", new JsonObject { ["group"] = group })).AsObject();
            balancePanel.Children.Clear();
            var controls = new List<(string Id, JsonNode? Original, Control Control, bool Boolean)>();
            foreach (var node in balance["entries"]?.AsArray() ?? [])
            {
                if (node is not JsonObject entry) continue;
                var label = entry["presentation"]?["displayName"]?.GetValue<string>() ?? entry["field"]?.GetValue<string>() ?? "Setting";
                var rowName = entry["rowPresentation"]?["displayName"]?.GetValue<string>() ?? entry["row"]?.GetValue<string>() ?? "";
                var original = entry["saved"]?.DeepClone();
                Control control;
                var boolean = false;
                var isBoolean = original is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out boolean);
                if (isBoolean)
                    control = new CheckBox { IsChecked = boolean, MinHeight = 32 };
                else
                    control = new NumericUpDown { Value = (decimal)(original?.GetValue<double>() ?? 0), Increment = .1m, FormatString = "0.###", MinHeight = 36 };
                control.IsEnabled = entry["editable"]?.GetValue<bool>() == true;
                controls.Add((entry["id"]?.GetValue<string>() ?? "", original, control, isBoolean));
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), ColumnSpacing = 12 };
                grid.Children.Add(new TextBlock { Text = $"{rowName} · {label}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(control, 1);
                grid.Children.Add(control);
                balancePanel.Children.Add(Card(grid));
            }
            var save = ActionButton("Save balance changes", Success, async () =>
            {
                await GuardAsync(async () =>
                {
                    if (balance is null) return;
                    var changes = new JsonArray();
                    foreach (var item in controls.Where(x => x.Control.IsEnabled))
                    {
                        JsonNode? value = item.Boolean
                            ? JsonValue.Create(((CheckBox)item.Control).IsChecked == true)
                            : JsonValue.Create((double)(((NumericUpDown)item.Control).Value ?? 0));
                        if (!JsonNode.DeepEquals(value, item.Original))
                            changes.Add((JsonNode)new JsonObject { ["id"] = item.Id, ["value"] = value });
                    }
                    if (changes.Count == 0) { Show("No balance value changed.", "info"); return; }
                    balance = (await administration.RequestAsync("balance.write", new JsonObject
                    {
                        ["group"] = group, ["expectedRevision"] = balance["revision"]?.DeepClone(), ["changes"] = changes
                    })).AsObject();
                    await LoadBalanceAsync(group);
                    Show("Balance changes saved. Restart to apply them.", "success");
                });
            });
            balancePanel.Children.Add(save);
        });
    }

    private void ClearAdministrationViews()
    {
        modsPanel.Children.Clear();
        selectionPanel.Children.Clear();
        configurationPanel.Children.Clear();
        balancePanel.Children.Clear();
        logs.Text = "";
    }

    private void UpdateProcessState()
    {
        if (installation is null) { processState.Text = "Installation unavailable"; return; }
        var name = Path.GetFileNameWithoutExtension(installation.ShippingPath);
        var running = Process.GetProcessesByName(name).Any(process =>
        {
            try { return string.Equals(process.MainModule?.FileName, installation.ShippingPath, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
            finally { process.Dispose(); }
        });
        processState.Text = running ? "Running" : "Stopped";
        processState.Foreground = running ? Success : null;
    }

    private async Task GuardAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        try { await operation(); }
        catch (AdminProtocolException error) { Show($"{error.Message} ({error.Code})", "danger"); }
        catch (Exception error) { Show(error.Message, "danger"); }
        finally { busy = false; }
    }

    private void Show(string message, string kind)
    {
        notice.Text = message;
        noticeBorder.Background = kind switch
        {
            "success" => new SolidColorBrush(Color.Parse("#193B2F")),
            "danger" => new SolidColorBrush(Color.Parse("#4A2028")),
            "warning" => new SolidColorBrush(Color.Parse("#4A391B")),
            _ => new SolidColorBrush(Color.Parse("#19354B"))
        };
        noticeBorder.IsVisible = true;
    }

    private static string FormatUptime(long seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString("d'.'hh':'mm':'ss", CultureInfo.InvariantCulture);
    private static ScrollViewer Scroll(Control content) => new() { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeight.Bold };
    private static Border Card(Control content) => new()
    {
        Background = Panel, BorderBrush = BorderColor, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = content
    };
    private static Control Labelled(string label, Control value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), ColumnSpacing = 12 };
        row.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(value, 1); row.Children.Add(value); return row;
    }
    private static Control FormRow(string label, Control input)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 12 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(input, 1); row.Children.Add(input); return row;
    }
    private static Button ActionButton(string text, IBrush color, Func<Task> action)
    {
        var button = new Button
        {
            Content = text, Background = color, Foreground = Brushes.White, MinHeight = 36,
            Padding = new Thickness(16, 7), HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += async (_, _) => await action();
        return button;
    }
}
