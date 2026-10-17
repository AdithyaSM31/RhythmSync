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

/// <summary>Composition root: wires settings, discovery, the client session, the ticker, the tray and the window together.</summary>
public partial class App : Application
{
    private const int DiscoveryPort = 47800;
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NetworkCheckInterval = TimeSpan.FromSeconds(15);

    private SettingsStore _store = null!;
    private AppSettings _settings = null!;
    private MainWindow _window = null!;
    private MainViewModel _main = null!;
    private TrayIcon? _tray;
    private DiscoveryListener? _listener;
    private ClientSession? _session;
    private SessionTicker? _ticker;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _networkTimer;
    private bool _checkingNetwork;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
        _store = new SettingsStore(dataDir);
        _settings = _store.Load();

        var clock = new StopwatchClock();
        _session = new ClientSession(new ClientSessionOptions(_settings.DeviceId, _settings.DeviceName), clock,
            new UdpDatagramChannelFactory(), new WasapiOutputFactory(() => null));
        var adapter = new ClientSessionAdapter(_session);

        Action<Action> post = a => Dispatcher.BeginInvoke(a);
        var client = new ClientViewModel(() => _listener?.Hosts ?? Array.Empty<DiscoveredHost>(),
            (endpoint, code) => adapter.JoinAsync(endpoint, code), adapter, post)
        {
            Code = _settings.LastPairingCode ?? "",
        };
        client.Joined += OnJoined;
        _main = new MainViewModel(client, post);

        StartListener(client, clock);

        _ticker = new SessionTicker(_session.Tick, _listener is null ? null : _listener.Tick);

        _window = new MainWindow { DataContext = _main };
        _tray = new TrayIcon(_window.ToggleVisibility, Quit);
        client.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ClientViewModel.StatusText)) _tray?.SetStatus(client.StatusText);
        };
        _tray.SetStatus(client.StatusText);

        _refreshTimer = new DispatcherTimer(UiRefreshInterval, DispatcherPriority.Normal, (_, _) => client.Refresh(), Dispatcher);
        _networkTimer = new DispatcherTimer(NetworkCheckInterval, DispatcherPriority.Background, async (_, _) => await CheckNetworkAsync(), Dispatcher);

        DispatcherUnhandledException += (_, args) => Log.Error(args.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error(args.Exception, "Unobserved task exception");
        SessionEnding += (_, _) => _window.AllowClose = true;

        _window.Show();
        client.RefreshHosts();
        _ = CheckNetworkAsync();
        if (!options.SkipFirewall && !_settings.FirewallConfigured) _ = SetUpFirewallAsync();
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
    {
        _settings = _settings with { LastHostName = host.Name, LastPairingCode = code };
        SaveSettings();
    }

    private async Task SetUpFirewallAsync()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            if (await Task.Run(() => FirewallSetup.EnsureRules(exe)))
            {
                _settings = _settings with { FirewallConfigured = true };
                SaveSettings();
            }
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

    private void SaveSettings()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Could not save settings"); }
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            _refreshTimer?.Stop();
            _networkTimer?.Stop();
            _ticker?.Dispose();   // first, so nothing ticks a session that is being torn down
            if (_session is not null) await _session.DisposeAsync();   // also closes its audio output
            _listener?.Dispose();
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while shutting down");
        }
        finally
        {
            _window.AllowClose = true;
            Log.CloseAndFlush();
            Shutdown();
        }
    }
}
