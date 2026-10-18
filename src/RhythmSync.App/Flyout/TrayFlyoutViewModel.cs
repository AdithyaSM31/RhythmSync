using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmSync.App.Controls;
using RhythmSync.App.ViewModels;
using Serilog;

namespace RhythmSync.App.Flyout;

/// <summary>What the tray flyout shows: hosting, joined to a host, or neither.</summary>
public enum FlyoutMode { Idle, Hosting, Joined }

/// <summary>
/// The tray flyout (spec §5). It adds no behaviour of its own: it projects the Host tab (<see cref="HostTabViewModel"/>)
/// and the client (<see cref="ClientViewModel"/>) and reuses their commands. The client rows are the host's own
/// <see cref="ClientRowViewModel"/> instances, so a flyout volume slider takes the same path to the host (and the
/// settings store) as the main window's. Every update goes through <c>post</c> (the dispatcher in the app, inline by default).
/// </summary>
public sealed partial class TrayFlyoutViewModel : ObservableObject
{
    public const string IdleText = "Host this PC's audio, or join a host from the app.";
    public const string WaitingText = "Waiting for PCs to join…";
    public const string IdleTitle = "RhythmSync";
    public const string HostingChipText = "Hosting";
    public const string NotConnectedChipText = "Not connected";

    private readonly HostTabViewModel _host;
    private readonly Action _openMainWindow;
    private readonly Action<Action> _post;

    private HostViewModel? _followed;

    public TrayFlyoutViewModel(HostTabViewModel host, ClientViewModel client, Action openMainWindow, Action<Action>? post = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _openMainWindow = openMainWindow ?? throw new ArgumentNullException(nameof(openMainWindow));
        _post = post ?? (a => a());

        StopHostingCommand = Wrap(host.StopCommand);
        LeaveCommand = Wrap(client.LeaveCommand);
        OpenCommand = new RelayCommand(Open);

        host.PropertyChanged += (_, _) => Post(Refresh);
        client.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClientViewModel.State) or nameof(ClientViewModel.StatusText)
                or nameof(ClientViewModel.IsConnected) or nameof(ClientViewModel.JoinedHostName))
                Post(Refresh);   // not the once-a-second statistics: the view binds those on Client directly
        };
        Refresh();
    }

    /// <summary>Raised after Open RhythmSync, Stop hosting or Leave: the flyout should close.</summary>
    public event Action? CloseRequested;

    /// <summary>The client this PC runs; the Joined mode shows its status, statistics and host-set volume and offset.</summary>
    public ClientViewModel Client { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsHosting), nameof(IsJoined), nameof(ShowWaiting))]
    private FlyoutMode _mode;

    public bool IsIdle => Mode == FlyoutMode.Idle;
    public bool IsHosting => Mode == FlyoutMode.Hosting;
    public bool IsJoined => Mode == FlyoutMode.Joined;

    /// <summary>Hosting: the session name. Joined: the host's name. Idle: "RhythmSync".</summary>
    [ObservableProperty]
    private string _title = IdleTitle;

    /// <summary>The code other PCs type to join; only while hosting.</summary>
    [ObservableProperty]
    private string? _pairingCode;

    /// <summary>The header's status chip. Idle shows "Not connected" in the grey Connecting style (flyout.html): idle is not an error.</summary>
    [ObservableProperty]
    private StatusKind _chipKind = StatusKind.Connecting;

    [ObservableProperty]
    private string _chipText = NotConnectedChipText;

    /// <summary>While hosting: the host's client rows (the same instances, in the same order). Empty otherwise.</summary>
    public ObservableCollection<ClientRowViewModel> Clients { get; } = new();

    /// <summary>Hosting with nobody joined yet: the flyout shows <see cref="WaitingText"/>.</summary>
    public bool ShowWaiting => IsHosting && Clients.Count == 0;

    /// <summary>The Host tab's Stop command; closes the flyout.</summary>
    public IRelayCommand StopHostingCommand { get; }

    /// <summary>The client's Leave command; closes the flyout.</summary>
    public IRelayCommand LeaveCommand { get; }

    /// <summary>Shows and activates the main window; closes the flyout.</summary>
    public IRelayCommand OpenCommand { get; }

    private void Refresh()
    {
        // Joined follows the live session, never the remembered host name alone.
        Mode = _host.IsHosting ? FlyoutMode.Hosting : Client.IsConnected ? FlyoutMode.Joined : FlyoutMode.Idle;
        Follow(Mode == FlyoutMode.Hosting ? _host.Current : null);

        switch (Mode)
        {
            case FlyoutMode.Hosting:
                Title = _followed?.SessionName ?? _host.SessionName;
                PairingCode = _followed?.PairingCode;
                ChipKind = StatusKind.Hosting;
                ChipText = HostingChipText;
                break;
            case FlyoutMode.Joined:
                Title = Client.JoinedHostName ?? IdleTitle;
                PairingCode = null;
                ChipKind = StatusKinds.From(Client.State, hosting: false);
                ChipText = Client.StatusText;
                break;
            default:
                Title = IdleTitle;
                PairingCode = null;
                ChipKind = StatusKind.Connecting;   // grey, not Danger: nothing is wrong
                ChipText = NotConnectedChipText;
                break;
        }
        SyncClients();
    }

    /// <summary>Starts following another running host (its name and client list), or none, and lets go of the previous one.</summary>
    private void Follow(HostViewModel? host)
    {
        if (ReferenceEquals(host, _followed)) return;
        if (_followed is not null)
        {
            _followed.PropertyChanged -= OnFollowedChanged;
            _followed.Clients.CollectionChanged -= OnFollowedClientsChanged;
        }
        _followed = host;
        if (host is not null)
        {
            host.PropertyChanged += OnFollowedChanged;
            host.Clients.CollectionChanged += OnFollowedClientsChanged;
        }
    }

    private void OnFollowedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostViewModel.SessionName)) Post(Refresh);
    }

    private void OnFollowedClientsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Post(SyncClients);

    /// <summary>
    /// Brings <see cref="Clients"/> in line with the followed host's rows by moving, inserting and removing single items
    /// (never a reset, which would rebuild every row while a slider might be in use).
    /// </summary>
    private void SyncClients()
    {
        IReadOnlyList<ClientRowViewModel> source = _followed?.Clients ?? (IReadOnlyList<ClientRowViewModel>)Array.Empty<ClientRowViewModel>();
        bool wasEmpty = Clients.Count == 0;

        for (int i = 0; i < source.Count; i++)
        {
            var row = source[i];
            if (i < Clients.Count && ReferenceEquals(Clients[i], row)) continue;

            int at = -1;
            for (int j = i + 1; j < Clients.Count; j++)
            {
                if (ReferenceEquals(Clients[j], row)) { at = j; break; }
            }
            if (at >= 0) Clients.Move(at, i);
            else Clients.Insert(i, row);
        }
        while (Clients.Count > source.Count) Clients.RemoveAt(Clients.Count - 1);

        if (wasEmpty != (Clients.Count == 0)) OnPropertyChanged(nameof(ShowWaiting));
    }

    private void Open()
    {
        try { _openMainWindow(); }
        catch (Exception e) { Log.Error(e, "Opening the main window from the flyout failed"); }
        CloseRequested?.Invoke();
    }

    /// <summary>Runs <paramref name="command"/> and then asks the flyout to close; available whenever the command is.</summary>
    private IRelayCommand Wrap(System.Windows.Input.ICommand command)
    {
        var wrapped = new RelayCommand(() =>
        {
            if (!command.CanExecute(null)) return;
            command.Execute(null);
            CloseRequested?.Invoke();
        }, () => command.CanExecute(null));
        command.CanExecuteChanged += (_, _) => wrapped.NotifyCanExecuteChanged();
        return wrapped;
    }

    /// <summary>Runs <paramref name="action"/> through the dispatcher hook; an exception is logged, never thrown back into the source's event.</summary>
    private void Post(Action action)
    {
        try
        {
            _post(() =>
            {
                try { action(); }
                catch (Exception e) { Log.Error(e, "Flyout update failed"); }
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not post a flyout update");
        }
    }
}
