using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RhythmSync.Core.Clock;
using Serilog;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// The main window: the selected page, the client screen, the host screen, the sidebar footer and the app-wide
/// Public-network warning.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string PublicNetworkText = "This Wi-Fi is set to Public – set it to Private so other PCs can connect.";
    public const string FooterNotConnected = "Not connected";
    public const string FooterHostingWaiting = "Hosting · waiting for PCs";

    private readonly Action<Action> _post;
    private HostViewModel? _watchedHost;
    private readonly List<ClientRowViewModel> _watchedRows = new();
    private string _footerText = FooterNotConnected;

    public MainViewModel(ClientViewModel client, Action<Action>? post = null, HostTabViewModel? host = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Host = host;
        _post = post ?? (a => a());
        if (host is not null) host.PropertyChanged += OnHostTabChanged;
        client.PropertyChanged += OnClientChanged;

        WatchHost(host?.Current);
        _footerText = ComputeFooter();
    }

    /// <summary>The page to open at startup: Join when this PC has joined a host before, otherwise Host.</summary>
    public static Page InitialPage(string? lastHostName) => lastHostName is null ? Page.Host : Page.Join;

    [ObservableProperty]
    private Page _selectedPage;

    public ClientViewModel Client { get; }

    /// <summary>The Host page; null when host mode is unavailable.</summary>
    public HostTabViewModel? Host { get; }

    /// <summary>False while hosting: this PC cannot also be a client of another host.</summary>
    public bool ClientEnabled => Host is not { IsHosting: true };

    [ObservableProperty]
    private bool _showPublicNetworkWarning;

    public string PublicNetworkWarningText => PublicNetworkText;

    /// <summary>Shows or hides the banner. Safe to call from any thread.</summary>
    public void SetPublicNetwork(bool isPublic)
    {
        try { _post(() => ShowPublicNetworkWarning = isPublic); }
        catch (Exception e) { Log.Warning(e, "Could not post the network warning"); }
    }

    // ---- footer ------------------------------------------------------------------------------------------------------

    /// <summary>The app status in the sidebar's footer card (spec §3.1).</summary>
    public string FooterText
    {
        get => _footerText;
        private set => SetProperty(ref _footerText, value);
    }

    private string ComputeFooter()
    {
        if (Host is { IsHosting: true } host)
        {
            var rows = host.Current?.Clients;
            if (rows is null || rows.Count == 0) return FooterHostingWaiting;
            int inSync = rows.Count(r => r.State == SyncState.InSync);
            return $"Hosting · {inSync} of {rows.Count} in sync";
        }

        if (Client.State != SyncState.Disconnected && Client.JoinedHostName is { } name)
            return $"{Client.StatusText} · {name}";

        return FooterNotConnected;
    }

    /// <summary>Recomputes <see cref="FooterText"/> through the dispatcher hook; failures are logged, never thrown.</summary>
    private void UpdateFooter()
    {
        try
        {
            _post(() =>
            {
                try { FooterText = ComputeFooter(); }
                catch (Exception e) { Log.Error(e, "Updating the footer failed"); }
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not post the footer update");
        }
    }

    private void OnHostTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HostTabViewModel.IsHosting):
                OnPropertyChanged(nameof(ClientEnabled));
                UpdateFooter();
                break;
            case nameof(HostTabViewModel.Current):
                WatchHost(Host?.Current);
                UpdateFooter();
                break;
        }
    }

    private void OnClientChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientViewModel.State) or nameof(ClientViewModel.JoinedHostName))
            UpdateFooter();
    }

    /// <summary>Follows the running host's client list (and each row's state) instead of the previous host's.</summary>
    private void WatchHost(HostViewModel? host)
    {
        if (ReferenceEquals(host, _watchedHost)) return;
        if (_watchedHost is not null) _watchedHost.Clients.CollectionChanged -= OnRowsChanged;
        _watchedHost = host;
        if (host is not null) host.Clients.CollectionChanged += OnRowsChanged;
        WatchRows();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchRows();
        UpdateFooter();
    }

    private void WatchRows()
    {
        foreach (var row in _watchedRows) row.PropertyChanged -= OnRowChanged;
        _watchedRows.Clear();
        if (_watchedHost is null) return;
        _watchedRows.AddRange(_watchedHost.Clients);
        foreach (var row in _watchedRows) row.PropertyChanged += OnRowChanged;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClientRowViewModel.State)) UpdateFooter();
    }
}
