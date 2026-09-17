using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Briefcase.ServerManager.Core;
using FluentIcons.Avalonia;
using FluentIconName = FluentIcons.Common.Icon;
using FluentIcons.Common;

namespace Briefcase.ServerManager;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush Panel = Brush("#19151F");
    private static readonly IBrush BorderColor = Brush("#332A3D");
    private static readonly IBrush Accent = Brush("#C47AF2");
    private static readonly IBrush AccentSoft = Brush("#352044");
    private static readonly IBrush Muted = Brush("#A99DB0");
    private static readonly IBrush Success = Brush("#27865D");
    private static readonly IBrush Info = Brush("#2877B7");
    private static readonly IBrush Danger = Brush("#B54250");
    private BriefcaseInstallation? installation;
    private readonly BriefcaseReleaseInstaller installer = new();
    private readonly AdminConnection administration = new();
    private readonly ServerQueryClient serverQuery = new();
    private readonly TextBlock notice;
    private readonly Border noticeBorder;
    private readonly FluentIcon noticeIcon;
    private readonly TextBlock processState = new();
    private readonly TextBlock installPath = new();
    private readonly TextBox targetDirectory = new() { MinHeight = 36 };
    private readonly ProgressBar installProgress = new() { Minimum = 0, Maximum = 100, Height = 12, IsVisible = false };
    private readonly TextBlock installStage = new() { TextWrapping = TextWrapping.Wrap, Opacity = .72 };
    private readonly ContentControl contentHost;
    private readonly List<Button> navigation = [];
    private readonly Dictionary<Button, PageDefinition> pageDefinitions = [];
    private readonly TextBlock pageTitle;
    private readonly TextBlock pageSubtitle;
    private readonly TextBlock serverSummaryName;
    private readonly Border serverSummaryDot;
    private readonly TextBlock serverSummaryReachability;
    private readonly TextBlock serverSummaryPlayers;
    private readonly TextBlock serverSummaryState;
    private readonly TextBlock serverSummaryMap;
    private readonly TextBlock serverSummaryPing;
    private readonly TextBlock serverSummaryActionState;
    private readonly Button serverSummaryStart;
    private readonly Button serverSummaryRestart;
    private readonly Button serverSummaryShutdown;
    private readonly TextBlock adminState;
    private readonly Border connectionDot;
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
    private readonly StackPanel balanceNavigationPanel = new() { Spacing = 4 };
    private readonly TextBox balanceSearch = new()
    {
        PlaceholderText = "Search by label, key, table, row or value…",
        MinHeight = 38
    };
    private readonly TextBlock balanceContentTitle = new() { FontSize = 20, FontWeight = FontWeight.Bold };
    private readonly TextBlock balanceContentSubtitle = new()
    {
        Foreground = Muted, TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBox logs = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 480 };
    private readonly ComboBox logSource = new() { ItemsSource = new[] { "framework", "game" }, SelectedIndex = 0, MinWidth = 180 };
    private readonly DispatcherTimer processTimer;
    private readonly DispatcherTimer serverSummaryTimer;
    private Button? selectedNavigation;
    private Button? refreshAdministrationButton;
    private Button? disconnectAdministrationButton;
    private JsonArray mods = [];
    private JsonObject? selection;
    private JsonObject? balance;
    private readonly Dictionary<string, string> balanceGroupLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode?> balanceDraft = new(StringComparer.Ordinal);
    private readonly List<BalanceEditorBinding> balanceControls = [];
    private string[] balanceGroups = [];
    private string? selectedBalanceGroup;
    private BalanceCatalog? balanceCatalog;
    private BalanceSlice? selectedBalanceSlice;
    private Button? balanceSaveButton;
    private bool busy;
    private bool serverSummaryBusy;
    private bool reconnectBusy;
    private bool reconnectPending;
    private bool restartObservedStopped;
    private bool localServerRunning;
    private int administrationVersion;
    private DateTimeOffset transitionDeadline;
    private ServerTransition serverTransition;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        notice = Require<TextBlock>("NoticeText");
        noticeBorder = Require<Border>("NoticeBorder");
        noticeIcon = Require<FluentIcon>("NoticeIcon");
        contentHost = Require<ContentControl>("ContentHost");
        pageTitle = Require<TextBlock>("PageTitle");
        pageSubtitle = Require<TextBlock>("PageSubtitle");
        serverSummaryName = Require<TextBlock>("ServerSummaryName");
        serverSummaryDot = Require<Border>("ServerSummaryDot");
        serverSummaryReachability = Require<TextBlock>("ServerSummaryReachability");
        serverSummaryPlayers = Require<TextBlock>("ServerSummaryPlayers");
        serverSummaryState = Require<TextBlock>("ServerSummaryState");
        serverSummaryMap = Require<TextBlock>("ServerSummaryMap");
        serverSummaryPing = Require<TextBlock>("ServerSummaryPing");
        serverSummaryActionState = Require<TextBlock>("ServerSummaryActionState");
        serverSummaryStart = Require<Button>("ServerSummaryStart");
        serverSummaryRestart = Require<Button>("ServerSummaryRestart");
        serverSummaryShutdown = Require<Button>("ServerSummaryShutdown");
        serverSummaryStart.Click += async (_, _) => await StartServerAsync();
        serverSummaryRestart.Click += async (_, _) => await RestartServerAsync();
        serverSummaryShutdown.Click += async (_, _) => await ShutdownServerAsync();
        adminState = Require<TextBlock>("AdminState");
        connectionDot = Require<Border>("ConnectionDot");
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

        InitializeNavigation();
        processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        processTimer.Tick += async (_, _) =>
        {
            UpdateProcessState();
            await TryReconnectLocalAsync();
        };
        processTimer.Start();
        serverSummaryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        serverSummaryTimer.Tick += async (_, _) => await RefreshServerSummaryAsync();
        serverSummaryTimer.Start();
        ResetServerSummary();
        Opened += async (_, _) =>
        {
            UpdateProcessState();
            if (installation is not null && File.Exists(installation.PairingFile))
            {
                await LoadLocalIdentityAsync();
                if (!administration.Connected && !string.IsNullOrWhiteSpace(password.Text))
                    await ConnectAsync();
            }
        };
        Closing += (_, _) =>
        {
            processTimer.Stop();
            serverSummaryTimer.Stop();
            administration.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
    }

    private void InitializeNavigation()
    {
        var pages = new (Button Button, PageDefinition Page)[]
        {
            (Require<Button>("HomeNavigation"),
                new PageDefinition("Home", "Overview", "Install, update and launch your dedicated server.", BuildHome(), false)),
            (Require<Button>("AdministrationNavigation"),
                new PageDefinition("Administration", "Administration", "Connect securely to a local or remote Briefcase server.", BuildConnection(), false)),
            (Require<Button>("ModsNavigation"),
                new PageDefinition("Mods", "Mods", "Inspect installed mods and edit their settings.", Scroll(modsPanel), true)),
            (Require<Button>("ServerNavigation"),
                new PageDefinition("Server", "Server", "Manage enabled mods, logs and server restarts.", BuildServer(), true)),
            (Require<Button>("ConfigurationNavigation"),
                new PageDefinition("Configuration", "Configuration", "Configure gameplay, network and map rotation settings.", Scroll(configurationPanel), true)),
            (Require<Button>("BalanceNavigation"),
                new PageDefinition("Balancing", "Balancing", "Edit character, weapon and gameplay balancing values.", BuildBalance(), true))
        };
        foreach (var (button, page) in pages)
        {
            button.Click += (_, _) => SelectPage(button);
            navigation.Add(button);
            pageDefinitions[button] = page;
        }
        SelectPage(pages[0].Button);
        UpdateConnectionUi();
    }
    private void SelectPage(Button selected)
    {
        selectedNavigation = selected;
        foreach (var button in navigation)
        {
            var active = ReferenceEquals(button, selected);
            button.Background = active ? AccentSoft : Brushes.Transparent;
            button.Foreground = active ? Brushes.White : Muted;
        }
        var page = pageDefinitions[selected];
        pageTitle.Text = page.Title;
        pageSubtitle.Text = page.Subtitle;
        contentHost.Content = page.RequiresConnection && !administration.Connected
            ? BuildDisconnectedState() : page.Content;
    }

    private Control BuildDisconnectedState()
    {
        var icon = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(22), Background = AccentSoft,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new PathIcon
            {
                Data = StreamGeometry.Parse(
                    "M4 3 L20 3 L20 9 L4 9 Z M6 5 L8 5 L8 7 L6 7 Z " +
                    "M4 10 L20 10 L20 16 L4 16 Z M6 12 L8 12 L8 14 L6 14 Z " +
                    "M4 17 L20 17 L20 23 L4 23 Z M6 19 L8 19 L8 21 L6 21 Z"),
                Width = 40, Height = 40, Foreground = Accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }
        };
        var openAdministration = ActionButton("Open administration", Info, () =>
        {
            var target = pageDefinitions.First(x => x.Value.Name == "Administration").Key;
            SelectPage(target);
            return Task.CompletedTask;
        }, FluentIconName.PlugConnected);
        openAdministration.HorizontalAlignment = HorizontalAlignment.Center;
        var content = new StackPanel
        {
            Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 520,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = "No server connected", FontSize = 22, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0)
                },
                new TextBlock
                {
                    Text = "Connect to a running Briefcase server to view and manage this section.",
                    Foreground = Muted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap
                },
                openAdministration
            }
        };
        return new Grid
        {
            Children =
            {
                new Border
                {
                    Background = Panel, BorderBrush = BorderColor, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12), Padding = new Thickness(44),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center, Child = content
                }
            }
        };
    }

    private void UpdateConnectionUi()
    {
        var connected = administration.Connected;
        adminState.Text = connected ? $"Connected · {administration.Protocol}" : "Not connected";
        adminState.Foreground = connected ? Success : Muted;
        connectionDot.Background = connected ? Success : Danger;
        if (!connected)
        {
            administrationVersion = 0;
            overview.Text = "No server is connected. Enter the administration identity above, then connect to load its status.";
            ResetServerSummary();
        }
        if (refreshAdministrationButton is not null) refreshAdministrationButton.IsEnabled = connected;
        if (disconnectAdministrationButton is not null) disconnectAdministrationButton.IsEnabled = connected;
        UpdateServerActions();
        if (selectedNavigation is not null) SelectPage(selectedNavigation);
    }

    private Control BuildHome()
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(12) };
        var install = new StackPanel { Spacing = 10 };
        install.Children.Add(Heading("Install or update Briefcase", FluentIconName.CloudArrowDown));
        install.Children.Add(new TextBlock
        {
            Text = "Select the Deceive Inc. dedicated server folder. The manager downloads the latest official release, verifies its GitHub SHA-256 digest and installs it beside the Shipping executable.",
            TextWrapping = TextWrapping.Wrap, Opacity = .72
        });
        install.Children.Add(FormRow("Server directory", targetDirectory));
        var installActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        installActions.Children.Add(ActionButton("Browse", Info, BrowseServerDirectoryAsync, FluentIconName.FolderOpen));
        installActions.Children.Add(ActionButton("Install latest release", Success, InstallLatestAsync, FluentIconName.CloudArrowDown));
        install.Children.Add(installActions);
        install.Children.Add(installProgress);
        install.Children.Add(installStage);
        body.Children.Add(Card(install));
        var status = new StackPanel { Spacing = 8 };
        status.Children.Add(Heading("Installation", FluentIconName.Server));
        status.Children.Add(Labelled("Platform directory", installPath));
        status.Children.Add(Labelled("Server process", processState));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(ActionButton("Start server", Success, async () => await StartServerAsync(), FluentIconName.ServerPlay));
        actions.Children.Add(ActionButton("Refresh", Info, () => { UpdateProcessState(); return Task.CompletedTask; }, FluentIconName.ArrowSync));
        status.Children.Add(actions);
        body.Children.Add(Card(status));

        var setup = new StackPanel { Spacing = 10 };
        setup.Children.Add(Heading("Configure administration", FluentIconName.Key));
        setup.Children.Add(new TextBlock
        {
            Text = "Generate the server identity and a strong administrator password. Use 127.0.0.1 for local-only access or 0.0.0.0 with a reachable public endpoint for remote access.",
            TextWrapping = TextWrapping.Wrap, Opacity = .72
        });
        setup.Children.Add(FormRow("Listen address", listen));
        setup.Children.Add(FormRow("Administration port", port));
        setup.Children.Add(FormRow("Public endpoint", publicEndpoint));
        var setupActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        setupActions.Children.Add(ActionButton("Generate administration password", Success, ConfigureAdministrationAsync, FluentIconName.Key));
        setupActions.Children.Add(ActionButton("Load existing identity", Info, LoadLocalIdentityAsync, FluentIconName.FolderOpen));
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
        form.Children.Add(Heading("Administration connection", FluentIconName.PlugConnected));
        form.Children.Add(FormRow("Endpoint", endpoint));
        form.Children.Add(FormRow("Certificate fingerprint", fingerprint));
        form.Children.Add(FormRow("Administrator password", password));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(ActionButton("Connect", Success, ConnectAsync, FluentIconName.PlugConnected));
        refreshAdministrationButton = ActionButton("Refresh", Info, RefreshAdministrationAsync, FluentIconName.ArrowSync);
        actions.Children.Add(refreshAdministrationButton);
        disconnectAdministrationButton = ActionButton("Disconnect", Danger, DisconnectAsync, FluentIconName.PlugDisconnected);
        actions.Children.Add(disconnectAdministrationButton);
        form.Children.Add(actions);
        body.Children.Add(Card(form));
        var summary = new StackPanel { Spacing = 8 };
        summary.Children.Add(Heading("Server status", FluentIconName.Info));
        summary.Children.Add(overview);
        body.Children.Add(Card(summary));
        return Scroll(body);
    }

    private Control BuildServer()
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(12) };
        var selectionCard = new StackPanel { Spacing = 10 };
        selectionCard.Children.Add(Heading("Enabled mods", FluentIconName.Apps));
        selectionCard.Children.Add(selectionPanel);
        selectionCard.Children.Add(ActionButton("Save mod selection", Success, SaveSelectionAsync, FluentIconName.Save));
        body.Children.Add(Card(selectionCard));
        var logCard = new StackPanel { Spacing = 10 };
        logCard.Children.Add(Heading("Logs", FluentIconName.DocumentText));
        var logActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        logActions.Children.Add(logSource);
        logActions.Children.Add(ActionButton("Refresh logs", Info, LoadLogsAsync, FluentIconName.ArrowSync));
        logCard.Children.Add(logActions);
        logCard.Children.Add(logs);
        body.Children.Add(Card(logCard));
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Heading("Restart", FluentIconName.Power),
                new TextBlock { Text = "Restart through Briefcase to apply saved settings and mod updates.", Opacity = .72 },
                ActionButton("Restart server", Danger, RestartServerAsync, FluentIconName.Power)
            }
        }));
        return Scroll(body);
    }

    private Control BuildBalance()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 12,
            Margin = new Thickness(12)
        };

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        toolbar.Children.Add(balanceSearch);
        var reload = ActionButton("Reload", Info, async () =>
        {
            if (selectedBalanceGroup is not null) await LoadBalanceAsync(selectedBalanceGroup);
        }, FluentIconName.ArrowSync);
        Grid.SetColumn(reload, 1);
        toolbar.Children.Add(reload);
        root.Children.Add(Card(toolbar));

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("270,*"),
            ColumnSpacing = 14
        };
        Grid.SetRow(columns, 1);
        var navigationCard = Card(Scroll(balanceNavigationPanel));
        navigationCard.Padding = new Thickness(10);
        columns.Children.Add(navigationCard);

        var content = new StackPanel { Spacing = 8, Margin = new Thickness(2, 0, 0, 0) };
        content.Children.Add(balanceContentTitle);
        content.Children.Add(balanceContentSubtitle);
        content.Children.Add(balancePanel);
        balanceSaveButton = ActionButton("Save balance changes", Success, SaveBalanceAsync, FluentIconName.Save);
        balanceSaveButton.HorizontalAlignment = HorizontalAlignment.Left;
        balanceSaveButton.IsVisible = false;
        content.Children.Add(balanceSaveButton);
        var contentScroll = Scroll(content);
        Grid.SetColumn(contentScroll, 1);
        columns.Children.Add(contentScroll);
        root.Children.Add(columns);

        balanceSearch.TextChanged += (_, _) =>
        {
            CaptureBalanceDraft();
            RenderBalanceContent();
        };
        RenderBalanceNavigation();
        RenderBalanceContent();
        return root;
    }

    private async Task StartServerAsync()
    {
        await GuardAsync(async () =>
        {
            _ = installation?.StartServer() ?? throw new InvalidOperationException("No valid installation was found.");
            serverTransition = ServerTransition.Starting;
            reconnectPending = true;
            restartObservedStopped = false;
            transitionDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
            UpdateServerActions();
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
            UpdateConnectionUi();
            await RefreshAdministrationCoreAsync();
            Show("Authenticated administration connection established.", "success");
        });
    }

    private async Task DisconnectAsync()
    {
        await GuardAsync(async () =>
        {
            await administration.LogoutAsync();
            overview.Text = "";
            ClearAdministrationViews();
            UpdateConnectionUi();
            Show("Administration disconnected.", "info");
        });
    }

    private async Task RefreshAdministrationAsync() => await GuardAsync(RefreshAdministrationCoreAsync);

    private async Task RefreshAdministrationCoreAsync()
    {
        var status = (await administration.RequestAsync("server.status")).AsObject();
        await UpdateServerSummaryAsync(status);
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
            ApplyBalanceIndex(first);
            var group = selectedBalanceGroup is not null && balanceGroups.Contains(selectedBalanceGroup, StringComparer.Ordinal)
                ? selectedBalanceGroup
                : BalanceCatalogBuilder.Characters.FirstOrDefault(x => balanceGroups.Contains(x, StringComparer.Ordinal))
                  ?? balanceGroups.FirstOrDefault();
            if (group is not null) await LoadBalanceCoreAsync(group);
        }
        Show("Server data refreshed.", "success");
    }

    private async Task RefreshServerSummaryAsync()
    {
        if (!administration.Connected || serverSummaryBusy)
        {
            if (!administration.Connected) ResetServerSummary();
            return;
        }
        serverSummaryBusy = true;
        try
        {
            var status = (await administration.RequestAsync("server.status")).AsObject();
            await UpdateServerSummaryAsync(status);
        }
        catch
        {
            serverSummaryDot.Background = Danger;
            serverSummaryReachability.Text = "Unavailable";
            serverSummaryPing.Text = "— ms";
        }
        finally
        {
            serverSummaryBusy = false;
        }
    }

    private async Task UpdateServerSummaryAsync(JsonObject status)
    {
        administrationVersion = status["administrationVersion"]?.GetValue<int>() ?? 0;
        UpdateServerActions();
        var session = status["session"] as JsonObject;
        var available = session?["available"]?.GetValue<bool>() == true;
        serverSummaryName.Text = available
            ? session?["name"]?.GetValue<string>() ?? "Dedicated server"
            : "Server is starting";
        var players = session?["players"]?.GetValue<long>();
        var maximum = session?["maxPlayers"]?.GetValue<long>();
        serverSummaryPlayers.Text = players.HasValue && maximum.HasValue
            ? $"{players} / {maximum} players"
            : "— players";
        serverSummaryState.Text = session?["state"]?.GetValue<string>() ?? "Starting";
        var map = FriendlyMapName(session?["map"]?.GetValue<string>());
        var mode = session?["gameMode"]?.GetValue<string>();
        serverSummaryMap.Text = string.Join(" · ", new[] { map, mode }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(serverSummaryMap.Text)) serverSummaryMap.Text = "—";

        var queryPort = session?["queryPort"]?.GetValue<int>();
        if (!queryPort.HasValue)
        {
            serverSummaryDot.Background = available ? Info : Danger;
            serverSummaryReachability.Text = available ? "Connected" : "Starting";
            serverSummaryPing.Text = "— ms";
            return;
        }
        var host = AdminEndpoint.Parse(endpoint.Text ?? AdminEndpoint.DefaultValue).Host;
        var query = await serverQuery.ProbeAsync(host, queryPort.Value, TimeSpan.FromMilliseconds(900));
        serverSummaryDot.Background = query.Reachable ? Success : Danger;
        serverSummaryReachability.Text = query.Reachable ? "Online" : "No query";
        serverSummaryPing.Text = query.Reachable
            ? $"{Math.Max(1, Math.Round(query.LatencyMilliseconds)):0} ms"
            : "— ms";
    }

    private void ResetServerSummary()
    {
        serverSummaryName.Text = "No server connected";
        serverSummaryDot.Background = Danger;
        serverSummaryReachability.Text = "Offline";
        serverSummaryPlayers.Text = "— players";
        serverSummaryState.Text = "Unknown";
        serverSummaryMap.Text = "—";
        serverSummaryPing.Text = "— ms";
        UpdateServerActions();
    }

    private static string FriendlyMapName(string? value) => value switch
    {
        "HardSell" or "Hardsell" or "Hardsell_Day" or "DI_Hardsell" => "Hard Sell",
        "SilverReef" or "Silverreef" or "DI_SR" => "Silver Reef",
        "DiamondSpire" or "Diamondspire" or "DI_DS" => "Diamond Spire",
        "FragrantShore" or "DI_FS" => "Fragrant Shore",
        "SoundEclipse" or "DI_SE" => "Sound Eclipse",
        "FragrantShore_Night" or "DI_FSN" => "Fragrant Shore (Night)",
        "HardSell_Dawn" or "DI_HSD" => "Hard Sell (Morning)",
        _ => value ?? ""
    };

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
                }, FluentIconName.Save));
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
        }, FluentIconName.Save));
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
            serverTransition = ServerTransition.Restarting;
            reconnectPending = installation is not null;
            restartObservedStopped = false;
            transitionDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
            await administration.LogoutAsync();
            ClearAdministrationViews();
            UpdateConnectionUi();
            Show("Server restart requested. Reconnect after startup completes.", "warning");
        });
    }

    private async Task ShutdownServerAsync()
    {
        await GuardAsync(async () =>
        {
            await administration.RequestAsync("server.shutdown");
            serverTransition = ServerTransition.Stopping;
            reconnectPending = false;
            restartObservedStopped = false;
            transitionDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            await administration.LogoutAsync();
            ClearAdministrationViews();
            UpdateConnectionUi();
            Show("Server shutdown requested.", "warning");
        });
    }

    private async Task LoadBalanceAsync(string group) =>
        await GuardAsync(() => LoadBalanceCoreAsync(group));

    private async Task LoadBalanceCoreAsync(string group)
    {
        var preferredSlice = string.Equals(selectedBalanceGroup, group, StringComparison.Ordinal)
            ? selectedBalanceSlice?.Key
            : null;
        var response = (await administration.RequestAsync("balance.read",
            new JsonObject { ["group"] = group })).AsObject();
        ApplyBalanceResponse(response, preferredSlice);
    }

    private void ApplyBalanceIndex(JsonObject response)
    {
        balanceGroups = response["groups"]?.AsObject().Select(x => x.Key).ToArray() ?? [];
        balanceGroupLabels.Clear();
        if (response["groupLabels"] is JsonObject labels)
            foreach (var (key, value) in labels)
                balanceGroupLabels[key] = BalanceSchemaCustomization.Default.GroupLabel(key,
                    value?["displayName"]?.GetValue<string>() ?? key);
        RenderBalanceNavigation();
    }

    private void ApplyBalanceResponse(JsonObject response, string? preferredSlice = null)
    {
        balance = response;
        balanceCatalog = BalanceCatalogBuilder.Build(response);
        selectedBalanceGroup = balanceCatalog.Group;
        balanceDraft.Clear();
        foreach (var entry in balanceCatalog.Entries)
            balanceDraft[entry.Id] = entry.Saved?.DeepClone();
        selectedBalanceSlice = balanceCatalog.Slices.FirstOrDefault(x => x.Key == preferredSlice)
                               ?? balanceCatalog.Slices.FirstOrDefault();
        RenderBalanceNavigation();
        RenderBalanceContent();
    }

    private void RenderBalanceNavigation()
    {
        balanceNavigationPanel.Children.Clear();
        AddBalanceNavigationHeading("CHARACTERS");
        foreach (var group in BalanceCatalogBuilder.Characters.Where(x => balanceGroups.Contains(x, StringComparer.Ordinal)))
        {
            balanceNavigationPanel.Children.Add(BalanceNavigationButton(
                BalanceGroupLabel(group), null, string.Equals(group, selectedBalanceGroup, StringComparison.Ordinal),
                new Thickness(0), async () => await LoadBalanceAsync(group)));
            if (string.Equals(group, selectedBalanceGroup, StringComparison.Ordinal) &&
                balanceCatalog?.Root == BalanceRoot.Characters)
                AddCharacterBalanceSlices();
        }

        AddBalanceNavigationHeading("GAME", new Thickness(8, 18, 8, 5));
        foreach (var group in balanceGroups.Where(x => !BalanceCatalogBuilder.Characters.Contains(x, StringComparer.Ordinal)))
        {
            balanceNavigationPanel.Children.Add(BalanceNavigationButton(
                BalanceGroupLabel(group), null, string.Equals(group, selectedBalanceGroup, StringComparison.Ordinal),
                new Thickness(0), async () => await LoadBalanceAsync(group)));
            if (string.Equals(group, selectedBalanceGroup, StringComparison.Ordinal) &&
                balanceCatalog?.Root == BalanceRoot.Game)
                foreach (var slice in balanceCatalog.Slices)
                    balanceNavigationPanel.Children.Add(BalanceNavigationButton(
                        slice.Label, slice.Count, slice.Key == selectedBalanceSlice?.Key,
                        new Thickness(16, 0, 0, 0), () => SelectBalanceSliceAsync(slice)));
        }
    }

    private void AddCharacterBalanceSlices()
    {
        if (balanceCatalog is null) return;
        foreach (var category in new[] { BalanceCategory.Weapons, BalanceCategory.Passives, BalanceCategory.Expertises })
        {
            var slices = balanceCatalog.Slices.Where(x => x.Category == category).ToArray();
            if (slices.Length == 0) continue;
            balanceNavigationPanel.Children.Add(new TextBlock
            {
                Text = BalanceCatalogBuilder.CategoryLabel(category).ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Muted,
                Margin = new Thickness(16, 9, 8, 3)
            });
            foreach (var slice in slices)
                balanceNavigationPanel.Children.Add(BalanceNavigationButton(
                    slice.Label, slice.Count, slice.Key == selectedBalanceSlice?.Key,
                    new Thickness(24, 0, 0, 0), () => SelectBalanceSliceAsync(slice)));
        }
    }

    private Task SelectBalanceSliceAsync(BalanceSlice slice)
    {
        CaptureBalanceDraft();
        selectedBalanceSlice = slice;
        RenderBalanceNavigation();
        RenderBalanceContent();
        return Task.CompletedTask;
    }

    private Button BalanceNavigationButton(string label, int? count, bool selected,
        Thickness margin, Func<Task> action)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (count is not null)
        {
            var countText = new TextBlock
            {
                Text = count.Value.ToString(CultureInfo.InvariantCulture),
                FontSize = 11,
                Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(countText, 1);
            content.Children.Add(countText);
        }
        var button = new Button
        {
            Content = content,
            Margin = margin,
            Background = selected ? AccentSoft : Brushes.Transparent,
            Foreground = selected ? Brushes.White : Muted
        };
        button.Classes.Add("balance-navigation");
        button.Click += async (_, _) => await action();
        return button;
    }

    private void AddBalanceNavigationHeading(string text, Thickness? margin = null) =>
        balanceNavigationPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = Muted,
            Margin = margin ?? new Thickness(8, 4, 8, 5)
        });

    private string BalanceGroupLabel(string group) => BalanceSchemaCustomization.Default.GroupLabel(group,
        balanceGroupLabels.TryGetValue(group, out var label) ? label : group);

    private void RenderBalanceContent()
    {
        balancePanel.Children.Clear();
        balanceControls.Clear();
        if (balanceSaveButton is not null) balanceSaveButton.IsVisible = balanceCatalog is not null;
        if (balanceCatalog is null)
        {
            balanceContentTitle.Text = "Select balancing data";
            balanceContentSubtitle.Text = "Connect to a server and choose a character or game category.";
            balancePanel.Children.Add(Card(new TextBlock
            {
                Text = "No balancing data is loaded.",
                Foreground = Muted
            }));
            return;
        }

        var query = balanceSearch.Text?.Trim() ?? "";
        var entries = balanceCatalog.Select(selectedBalanceSlice, query).ToArray();
        if (query.Length > 0)
        {
            balanceContentTitle.Text = $"{entries.Length} search result{(entries.Length == 1 ? "" : "s")}";
            balanceContentSubtitle.Text = $"Searching every value in {balanceCatalog.GroupLabel}. Clear the search field to return to the selected category.";
        }
        else
        {
            balanceContentTitle.Text = selectedBalanceSlice?.Label ?? balanceCatalog.GroupLabel;
            balanceContentSubtitle.Text = BalanceBreadcrumb(balanceCatalog, selectedBalanceSlice);
        }

        if (entries.Length == 0)
        {
            balancePanel.Children.Add(Card(new TextBlock
            {
                Text = query.Length > 0 ? "No balancing value matches this search." : "This category has no balancing values.",
                Foreground = Muted
            }));
            return;
        }

        foreach (var component in entries.GroupBy(x => new
                 {
                     x.Path.Category, x.Path.Variant, x.Path.Component, x.ComponentLabel, x.Row, x.RowLabel
                 }))
        {
            var card = new StackPanel { Spacing = 12 };
            card.Children.Add(new TextBlock
            {
                Text = component.Key.ComponentLabel,
                FontSize = 17,
                FontWeight = FontWeight.Bold
            });
            card.Children.Add(new TextBlock
            {
                Text = query.Length > 0
                    ? $"{BalanceCatalogBuilder.CategoryLabel(component.Key.Category)} · {BalanceCatalogBuilder.VariantLabel(balanceCatalog.Group, component.Key.Category, component.Key.Variant)} · {component.Key.RowLabel}"
                    : component.Key.RowLabel,
                Foreground = Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var entry in component) card.Children.Add(BuildBalanceEditor(entry));
            balancePanel.Children.Add(Card(card));
        }
    }

    private Control BuildBalanceEditor(BalanceEntry entry)
    {
        var value = balanceDraft.GetValueOrDefault(entry.Id) ?? entry.Saved;
        Control editor;
        if (entry.ValueType == BalanceValueType.Boolean)
        {
            var checkedValue = value is JsonValue json && json.TryGetValue<bool>(out var result) && result;
            editor = new CheckBox
            {
                IsChecked = checkedValue,
                MinHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (entry.ValueType == BalanceValueType.Number && entry.Constraint.AllowedValues.Count > 0)
        {
            var allowed = entry.Constraint.AllowedValues;
            editor = new ComboBox
            {
                ItemsSource = allowed,
                SelectedItem = ToDecimal(value),
                MinHeight = 38,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }
        else if (entry.ValueType == BalanceValueType.Number)
        {
            var numeric = new NumericUpDown
            {
                Value = ToDecimal(value),
                Increment = entry.Constraint.Increment,
                FormatString = "0.###############",
                MinHeight = 38,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            if (entry.Constraint.Minimum is not null) numeric.Minimum = entry.Constraint.Minimum.Value;
            if (entry.Constraint.Maximum is not null) numeric.Maximum = entry.Constraint.Maximum.Value;
            editor = numeric;
        }
        else
            editor = new TextBox
            {
                Text = value?.ToJsonString() ?? "",
                IsReadOnly = true,
                MinHeight = 38
            };
        editor.IsEnabled = entry.Editable;
        ToolTip.SetTip(editor, $"{entry.Table} / {entry.Row} / {entry.Field}" +
                               ConstraintDescription(entry));
        if (entry.ValueType != BalanceValueType.Unsupported)
            balanceControls.Add(new BalanceEditorBinding(entry.Id, editor));

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = entry.Label,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        labels.Children.Add(new TextBlock
        {
            Text = entry.Field,
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,230"), ColumnSpacing = 18 };
        row.Children.Add(labels);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private void CaptureBalanceDraft()
    {
        foreach (var binding in balanceControls.Where(x => x.Control.IsEnabled))
            balanceDraft[binding.Id] = binding.Control switch
            {
                CheckBox checkBox => JsonValue.Create(checkBox.IsChecked == true),
                NumericUpDown numeric => JsonValue.Create((double)(numeric.Value ?? 0)),
                ComboBox { SelectedItem: decimal selected } => JsonValue.Create((double)selected),
                _ => balanceDraft.GetValueOrDefault(binding.Id)
            };
        balanceControls.Clear();
    }

    private async Task SaveBalanceAsync()
    {
        await GuardAsync(async () =>
        {
            if (balance is null || balanceCatalog is null) return;
            CaptureBalanceDraft();
            var changes = new JsonArray();
            foreach (var entry in balanceCatalog.Entries.Where(x => x.Editable))
            {
                var value = balanceDraft.GetValueOrDefault(entry.Id);
                if (!entry.Constraint.TryValidate(value, entry.ValueType, out var validationError))
                {
                    RenderBalanceContent();
                    Show($"{entry.Label}: {validationError}", "danger");
                    return;
                }
                if (!JsonNode.DeepEquals(value, entry.Saved))
                    changes.Add((JsonNode)new JsonObject
                    {
                        ["id"] = entry.Id,
                        ["value"] = value?.DeepClone()
                    });
            }
            if (changes.Count == 0)
            {
                RenderBalanceContent();
                Show("No balance value changed.", "info");
                return;
            }
            var response = (await administration.RequestAsync("balance.write", new JsonObject
            {
                ["group"] = balanceCatalog.Group,
                ["expectedRevision"] = balance["revision"]?.DeepClone(),
                ["changes"] = changes
            })).AsObject();
            ApplyBalanceResponse(response, selectedBalanceSlice?.Key);
            Show("Balance changes saved. Restart to apply them.", "success");
        });
    }

    private static decimal ToDecimal(JsonNode? value)
    {
        return BalanceValueConstraint.TryDecimal(value, out var number) ? number : 0;
    }

    private static string ConstraintDescription(BalanceEntry entry)
    {
        if (entry.Constraint.AllowedValues.Count > 0)
            return $"\nAllowed values: {string.Join(", ", entry.Constraint.AllowedValues)}";
        if (entry.Constraint.Minimum is not null || entry.Constraint.Maximum is not null)
            return $"\nAllowed range: {entry.Constraint.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "−∞"} to {entry.Constraint.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "+∞"}";
        return entry.AllowedRange.Length > 0 ? $"\nServer constraint: {entry.AllowedRange}" : "";
    }

    private static string BalanceBreadcrumb(BalanceCatalog catalog, BalanceSlice? slice)
    {
        if (slice is null) return catalog.GroupLabel;
        return catalog.Root == BalanceRoot.Characters
            ? $"Characters  /  {catalog.GroupLabel}  /  {BalanceCatalogBuilder.CategoryLabel(slice.Category)}  /  {slice.Label}"
            : $"Game  /  {catalog.GroupLabel}  /  {slice.Label}";
    }

    private void ClearAdministrationViews()
    {
        modsPanel.Children.Clear();
        selectionPanel.Children.Clear();
        configurationPanel.Children.Clear();
        balancePanel.Children.Clear();
        balanceNavigationPanel.Children.Clear();
        balanceGroups = [];
        balanceGroupLabels.Clear();
        balanceDraft.Clear();
        balanceControls.Clear();
        balance = null;
        balanceCatalog = null;
        selectedBalanceGroup = null;
        selectedBalanceSlice = null;
        balanceContentTitle.Text = "Select balancing data";
        balanceContentSubtitle.Text = "Connect to a server and choose a character or game category.";
        if (balanceSaveButton is not null) balanceSaveButton.IsVisible = false;
        logs.Text = "";
    }

    private void UpdateProcessState()
    {
        if (installation is null)
        {
            localServerRunning = false;
            processState.Text = "Installation unavailable";
            UpdateServerActions();
            return;
        }
        var name = Path.GetFileNameWithoutExtension(installation.ShippingPath);
        var running = Process.GetProcessesByName(name).Any(process =>
        {
            try { return string.Equals(process.MainModule?.FileName, installation.ShippingPath, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
            finally { process.Dispose(); }
        });
        localServerRunning = running;
        if (serverTransition == ServerTransition.Restarting)
        {
            if (!running) restartObservedStopped = true;
            if (restartObservedStopped && running) serverTransition = ServerTransition.None;
        }
        else if (serverTransition == ServerTransition.Starting && running)
            serverTransition = ServerTransition.None;
        else if (serverTransition == ServerTransition.Stopping && !running)
            serverTransition = ServerTransition.None;
        if (serverTransition != ServerTransition.None && DateTimeOffset.UtcNow >= transitionDeadline)
        {
            serverTransition = ServerTransition.None;
            reconnectPending = false;
        }
        processState.Text = serverTransition switch
        {
            ServerTransition.Starting => "Starting",
            ServerTransition.Restarting => "Restarting",
            ServerTransition.Stopping => "Stopping",
            _ => running ? "Running" : "Stopped"
        };
        processState.Foreground = running ? Success : null;
        UpdateServerActions();
    }

    private void UpdateServerActions()
    {
        var transitioning = serverTransition != ServerTransition.None;
        serverSummaryStart.IsVisible = installation is not null && !localServerRunning &&
                                       !administration.Connected && !transitioning;
        serverSummaryRestart.IsVisible = administration.Connected && !transitioning;
        serverSummaryShutdown.IsVisible = administration.Connected && administrationVersion >= 4 &&
                                          !transitioning;
        serverSummaryActionState.Text = serverTransition switch
        {
            ServerTransition.Starting => "Starting server…",
            ServerTransition.Restarting => "Restarting server…",
            ServerTransition.Stopping => "Shutting down server…",
            _ when administration.Connected => "Server controls",
            _ when localServerRunning => "Connecting…",
            _ when installation is not null => "Local server stopped",
            _ => "Remote start unavailable"
        };
    }

    private async Task TryReconnectLocalAsync()
    {
        if (!reconnectPending || reconnectBusy || administration.Connected || !localServerRunning ||
            installation is null)
            return;
        if (DateTimeOffset.UtcNow >= transitionDeadline)
        {
            reconnectPending = false;
            return;
        }
        reconnectBusy = true;
        try
        {
            var identity = await installation.ReadAdministrationAsync();
            await administration.ConnectAsync(identity.Endpoint, identity.Fingerprint, identity.Password);
            reconnectPending = false;
            serverTransition = ServerTransition.None;
            UpdateConnectionUi();
            await RefreshAdministrationCoreAsync();
            Show("Server is running and administration reconnected.", "success");
        }
        catch
        {
            // The process can exist for several seconds before its TLS listener is ready.
        }
        finally
        {
            reconnectBusy = false;
        }
    }

    private async Task GuardAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        try { await operation(); }
        catch (AdminProtocolException error)
        {
            if (!administration.Connected)
            {
                ClearAdministrationViews();
                UpdateConnectionUi();
            }
            Show($"{error.Message} ({error.Code})", "danger");
        }
        catch (Exception error)
        {
            if (error is IOException or EndOfStreamException or System.Net.Sockets.SocketException)
                await administration.LogoutAsync();
            if (!administration.Connected)
            {
                ClearAdministrationViews();
                UpdateConnectionUi();
            }
            Show(error.Message, "danger");
        }
        finally { busy = false; }
    }

    private void Show(string message, string kind)
    {
        notice.Text = message;
        noticeIcon.Icon = kind switch
        {
            "success" => FluentIconName.CheckmarkCircle,
            "danger" => FluentIconName.DismissCircle,
            "warning" => FluentIconName.Warning,
            _ => FluentIconName.Info
        };
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
    private static Control Heading(string text, FluentIconName icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        row.Children.Add(new FluentIcon
        {
            Icon = icon, IconSize = IconSize.Size20, Foreground = Accent,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = text, FontSize = 18, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }
    private static Border Card(Control content) => new()
    {
        Background = Panel, BorderBrush = BorderColor, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Child = content
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
    private static Button ActionButton(string text, IBrush color, Func<Task> action, FluentIconName icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FluentIcon
        {
            Icon = icon, IconSize = IconSize.Size20,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content, Background = color, Foreground = Brushes.White, MinHeight = 38,
            Padding = new Thickness(15, 8), HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add(ReferenceEquals(color, Success) ? "action-success" :
            ReferenceEquals(color, Danger) ? "action-danger" : "action-info");
        button.Click += async (_, _) => await action();
        return button;
    }

    private T Require<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing XAML control '{name}'.");

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    private sealed record PageDefinition(string Name, string Title, string Subtitle, Control Content,
        bool RequiresConnection);

    private sealed record BalanceEditorBinding(string Id, Control Control);

    private enum ServerTransition
    {
        None,
        Starting,
        Restarting,
        Stopping
    }
}
