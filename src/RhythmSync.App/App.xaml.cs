using System.IO;
using System.Net;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using RhythmSync.App.Services;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Config;
using RhythmSync.Core.Net;
using RhythmSync.Platform.Windows;
using Serilog;

namespace RhythmSync.App;

/// <summary>Composition root: wires settings, discovery, the client and host services, the ticker, the tray and the window together.</summary>
public partial class App : Application
{
    private const int DiscoveryPort = 47800;
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NetworkCheckInterval = TimeSpan.FromSeconds(15);

    private SingleInstanceCoordinator? _instance;
    private SettingsPersister _settings = null!;
    private MainWindow _window = null!;
    private MainViewModel _main = null!;
    private TrayIcon? _tray;
    private DiscoveryListener? _listener;
    private ClientSessionHolder? _client;
    private HostRuntime? _host;
    private SessionTicker? _ticker;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _networkTimer;
    private bool _checkingNetwork;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // One instance per user session: a second launch shows the first one's window and exits before touching
        // the log, settings, sockets or the tray.
        _instance = new SingleInstanceCoordinator(new NamedSingleInstanceGate());
        bool first;
        try
        {
            first = _instance.Claim(() => Dispatcher.BeginInvoke(() => _window?.ShowAndActivate()));
        }
        catch (Exception ex)
        {
            first = true;   // the check itself failed: better to run than to refuse to start
            System.Diagnostics.Trace.WriteLine($"RhythmSync single-instance check failed: {ex}");
        }
        if (!first)
        {
            _instance.Dispose();
            _instance = null;
            Shutdown(0);
            return;
        }

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RhythmSync");
        ConfigureLogging(dataDir);
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            Compose(StartupOptions.Parse(e.Args), dataDir);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RhythmSync failed to start");
            MessageBox.Show($"RhythmSync could not start: {ex.Message}", "RhythmSync", MessageBoxButton.OK, MessageBoxImage.Error);
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    private static void ConfigureLogging(string dataDir)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(dataDir, "logs", "rhythmsync-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
    }

    private void Compose(StartupOptions options, string dataDir)
    {
        var store = new SettingsStore(dataDir);
        _settings = new SettingsPersister(store, store.Load());

        var clock = new StopwatchClock();
        Action<Action> post = a => Dispatcher.BeginInvoke(a);

        // The client session is created on the first join and disposed while hosting, so the two modes never share ports.
        var client = new ClientSessionHolder(() => new ClientSession(
            new ClientSessionOptions(_settings.Current.DeviceId, _settings.Current.DeviceName), clock,
            new UdpDatagramChannelFactory(), new WasapiOutputFactory(() => null)));
        _client = client;
        var clientVm = new ClientViewModel(() => _listener?.Hosts ?? Array.Empty<DiscoveredHost>(),
            (endpoint, code) => client.JoinAsync(endpoint, code), client, post)
        {
            Code = _settings.Current.LastPairingCode ?? "",
        };
        clientVm.Joined += OnJoined;

        _host = new HostRuntime(new WindowsHostEnvironment(), clock, _settings, post, client.ReleaseAsync, client.Allow);
        var hostTab = new HostTabViewModel(_host, _settings.Current.DeviceName);
        _main = new MainViewModel(clientVm, post, hostTab);

        StartListener(clientVm, clock);

        // 10 ms ticks and 1 ms timer resolution only while a session is active (joined as a client, or hosting).
        _ticker = new SessionTicker(Tick, _listener is null ? null : _listener.Tick,
            isActive: () => client.State != SyncState.Disconnected || _host.IsHosting);

        _window = new MainWindow { DataContext = _main };
        _tray = new TrayIcon(_window.ToggleVisibility, Quit);
        clientVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ClientViewModel.StatusText)) _tray?.SetStatus(clientVm.StatusText);
        };
        _tray.SetStatus(clientVm.StatusText);

        _refreshTimer = new DispatcherTimer(UiRefreshInterval, DispatcherPriority.Normal, (_, _) =>
        {
            clientVm.Refresh();
            _host?.Poll();   // heartbeats do not raise ClientsChanged
        }, Dispatcher);
        _networkTimer = new DispatcherTimer(NetworkCheckInterval, DispatcherPriority.Background, async (_, _) => await CheckNetworkAsync(), Dispatcher);

        DispatcherUnhandledException += (_, args) => Log.Error(args.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error(args.Exception, "Unobserved task exception");
        SessionEnding += (_, _) => _window.AllowClose = true;

        _window.Show();
        clientVm.RefreshHosts();
        _ = CheckNetworkAsync();
        if (!options.SkipFirewall && !_settings.Current.FirewallConfigured) _ = SetUpFirewallAsync();
    }

    /// <summary>One ticker tick: the client session and the host each get their turn even if the other throws.</summary>
    private void Tick()
    {
        try { _client?.Tick(); }
        catch (Exception e) { Log.Error(e, "Client tick failed"); }
        try { _host?.Tick(); }
        catch (Exception e) { Log.Error(e, "Host tick failed"); }
    }

    private void StartListener(ClientViewModel client, ISessionClock clock)
    {
        try
        {
            var channel = new UdpDatagramChannelFactory().Create(new IPEndPoint(IPAddress.Any, DiscoveryPort), broadcast: true);
            _listener = new DiscoveryListener(channel, clock);
            _listener.Changed += client.RefreshHosts;
            _listener.Start();
        }
        catch (Exception ex)
        {
            // Most likely another RhythmSync instance owns the port. The app still starts, without a host list.
            Log.Error(ex, "Could not listen for hosts on UDP {Port}", DiscoveryPort);
            _listener?.Dispose();
            _listener = null;
            client.HostsUnavailable = true;
        }
    }

    private void OnJoined(DiscoveredHost host, string code)
        => _settings.Update(s => s with { LastHostName = host.Name, LastPairingCode = code }, immediate: true);

    private async Task SetUpFirewallAsync()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            if (await Task.Run(() => FirewallSetup.EnsureRules(exe)))
                _settings.Update(s => s with { FirewallConfigured = true }, immediate: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Firewall setup failed");
        }
    }

    private async Task CheckNetworkAsync()
    {
        if (_checkingNetwork || _quitting) return;
        _checkingNetwork = true;
        try { _main.SetPublicNetwork(await Task.Run(NetworkProfile.AnyConnectedPublic)); }
        catch (Exception ex) { Log.Warning(ex, "Network check failed"); }
        finally { _checkingNetwork = false; }
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;

        // Each step has its own try/catch so one failure cannot skip the rest.
        Step("timers", () => { _refreshTimer?.Stop(); _networkTimer?.Stop(); });
        Step("ticker", () => _ticker?.Dispose());   // first, so nothing ticks a session that is being torn down
        await StepAsync("host", async () => { if (_host is not null) await _host.StopAsync(); });
        await StepAsync("client session", async () => { if (_client is not null) await _client.DisposeAsync(); });   // also closes its audio output
        Step("discovery listener", () => _listener?.Dispose());
        Step("settings", () => _settings?.Dispose());   // writes anything still pending
        Step("tray", () => _tray?.Dispose());
        Step("single instance", () => _instance?.Dispose());

        _window.AllowClose = true;
        Log.CloseAndFlush();
        Shutdown();
    }

    private static void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error(ex, "Shutdown step failed: {What}", what); }
    }

    private static async Task StepAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.Error(ex, "Shutdown step failed: {What}", what); }
    }
}
