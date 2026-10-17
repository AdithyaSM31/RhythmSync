using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using Serilog;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// Client mode: the discovered hosts, the pairing code, joining, live status and the local volume/offset sliders.
/// Network callbacks (host list changes, session state changes) arrive on background threads, so every UI-bound update
/// goes through the injected <c>post</c> (the dispatcher in the app, inline in tests) and exceptions never travel back
/// into the network thread.
/// </summary>
public sealed partial class ClientViewModel : ObservableObject
{
    public const string CantReachText = "Can't reach host";
    public const string HostsUnavailableText = "Can't listen for hosts on this PC – is RhythmSync already running?";
    public const string RejectedText = "Join was rejected";
    public const string SearchingText = "Looking for hosts…";
    public const string AudioDeviceText = "Can't open the audio device – check your sound settings.";
    public const string Unknown = StatFormat.Unknown;

    public const float MaxVolume = 1f;
    public const double MaxOffsetMs = 100;

    private readonly Func<IReadOnlyList<DiscoveredHost>> _hosts;
    private readonly Func<IPEndPoint, string, Task<JoinResult>> _join;
    private readonly IClientSessionControl? _control;
    private readonly Action<Action> _post;

    private float _volume = MaxVolume;
    private double _offsetMs;
    private bool _pulling;
    private long _outputFailures;
    private long _failureBaseline;
    private PlaybackStats? _stats;

    public ClientViewModel(Func<IReadOnlyList<DiscoveredHost>> hosts, Func<IPEndPoint, string, Task<JoinResult>> join,
        IClientSessionControl? control = null, Action<Action>? post = null)
    {
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _join = join ?? throw new ArgumentNullException(nameof(join));
        _control = control;
        _post = post ?? (a => a());

        Hosts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HostListMessage));

        if (control is not null)
        {
            _state = control.State;
            _outputFailures = _failureBaseline = control.OutputFailures;
            if (float.IsFinite(control.Volume)) _volume = Math.Clamp(control.Volume, 0f, MaxVolume);
            if (double.IsFinite(control.OffsetMs)) _offsetMs = Math.Clamp(control.OffsetMs, -MaxOffsetMs, MaxOffsetMs);
            control.StateChanged += OnSessionStateChanged;
        }
    }

    /// <summary>Raised on the UI thread after a successful join with the host and code that were used.</summary>
    public event Action<DiscoveredHost, string>? Joined;

    public ObservableCollection<DiscoveredHost> Hosts { get; } = new();

    // ---- hosts and joining -------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    private DiscoveredHost? _selectedHost;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    private string _code = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostListMessage))]
    private bool _hostsUnavailable;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isJoining;

    /// <summary>Why the host list is empty or unavailable; null while hosts are listed.</summary>
    public string? HostListMessage => HostsUnavailable ? HostsUnavailableText : Hosts.Count == 0 ? SearchingText : null;

    public static string RejectText(RejectReason r) => r switch
    {
        RejectReason.BadCode => "Wrong code",
        RejectReason.LockedOut => "Too many attempts – wait 30 s",
        RejectReason.VersionMismatch => "Host runs a different RhythmSync version",
        RejectReason.SessionFull => "Session is full",
        _ => RejectedText,
    };

    /// <summary>Re-reads the host list. Safe to call from any thread.</summary>
    public void RefreshHosts() => Post(ApplyHosts);

    private void ApplyHosts()
    {
        IReadOnlyList<DiscoveredHost> latest;
        try { latest = _hosts(); }
        catch (Exception e)
        {
            Log.Warning(e, "Reading the host list failed");
            return;
        }

        if (latest.Select(Identity).SequenceEqual(Hosts.Select(Identity))) return;   // only beacon refreshes: leave the list alone

        var selected = SelectedHost is null ? (DiscoveredHost?)null : latest.FirstOrDefault(h => SameHost(h, SelectedHost));
        Hosts.Clear();
        foreach (var h in latest) Hosts.Add(h);
        SelectedHost = selected;
    }

    private static (uint, string, IPEndPoint) Identity(DiscoveredHost h) => (h.SessionId, h.Name, h.ControlEndPoint);
    private static bool SameHost(DiscoveredHost a, DiscoveredHost b) => a.SessionId == b.SessionId && a.ControlEndPoint.Equals(b.ControlEndPoint);

    private bool CanJoin() => SelectedHost is not null && IsValidCode(Code);

    private static bool IsValidCode(string? code) => code is { Length: 4 } && code.All(char.IsAsciiDigit);

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task JoinAsync()
    {
        var host = SelectedHost;
        string code = Code;
        if (host is null || !IsValidCode(code)) return;

        ErrorText = null;
        IsJoining = true;
        _failureBaseline = _control?.OutputFailures ?? 0;
        try
        {
            var result = await _join(host.ControlEndPoint, code);
            switch (result.Outcome)
            {
                case JoinOutcome.Joined:
                    Joined?.Invoke(host, code);
                    break;
                case JoinOutcome.Rejected:
                    ErrorText = result.Reason is { } reason ? RejectText(reason) : RejectedText;
                    break;
                default:
                    ErrorText = CantReachText;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another join or by leaving; the session state already says what happened.
        }
        catch (Exception e)
        {
            Log.Warning(e, "Joining {Host} failed", host.ControlEndPoint);
            ErrorText = CantReachText;
        }
        finally
        {
            IsJoining = false;
        }
    }

    // ---- status ------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsConnected), nameof(StatusMessage))]
    [NotifyCanExecuteChangedFor(nameof(LeaveCommand))]
    private SyncState _state = SyncState.Disconnected;

    public bool IsConnected => State != SyncState.Disconnected;

    public string StatusText => State switch
    {
        SyncState.Connecting => "Connecting…",
        SyncState.Syncing => "Syncing…",
        SyncState.InSync => "In sync",
        SyncState.Degraded => "Sync is degraded",
        _ => "Not connected",
    };

    /// <summary>A problem worth showing next to the status, e.g. the audio device cannot be opened; null when all is well.</summary>
    public string? StatusMessage => State == SyncState.Syncing && _outputFailures > _failureBaseline ? AudioDeviceText : null;

    public string SyncErrorText => $"Sync error: {StatFormat.WithUnit(_stats?.SyncErrorMs, 2, "ms")}";
    public string DriftText => $"Drift correction: {StatFormat.WithUnit(_stats?.RatioPpm, 0, "ppm")}";

    public string CountersText => _stats is { } s
        ? $"Resyncs: {s.Resyncs} · Concealed frames: {s.ConcealedFrames} · Late frames: {s.LateFrames}"
        : $"Resyncs: {Unknown} · Concealed frames: {Unknown} · Late frames: {Unknown}";

    partial void OnStateChanged(SyncState value)
    {
        // Playback works, so output failures counted so far are history.
        if (value != SyncState.InSync) return;
        _outputFailures = _control?.OutputFailures ?? _outputFailures;
        _failureBaseline = _outputFailures;
    }

    private void OnSessionStateChanged()
    {
        // Runs on whatever thread the session raised it on.
        Post(() => { if (_control is not null) State = _control.State; });
    }

    /// <summary>Polls the session for stats, failures and host-driven volume/offset changes. Safe to call from any thread.</summary>
    public void Refresh() => Post(RefreshCore);

    private void RefreshCore()
    {
        if (_control is null) return;

        State = _control.State;
        _stats = _control.Stats;
        _outputFailures = _control.OutputFailures;
        if (State == SyncState.InSync) _failureBaseline = _outputFailures;

        _pulling = true;   // the host may overwrite the session's volume/offset; adopt that without writing it back
        try
        {
            Volume = _control.Volume;
            OffsetMs = _control.OffsetMs;
        }
        finally
        {
            _pulling = false;
        }

        OnPropertyChanged(nameof(SyncErrorText));
        OnPropertyChanged(nameof(DriftText));
        OnPropertyChanged(nameof(CountersText));
        OnPropertyChanged(nameof(StatusMessage));
    }

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private async Task LeaveAsync()
    {
        try { await _control!.LeaveAsync(); }
        catch (Exception e) { Log.Warning(e, "Leaving the session failed"); }
    }

    private bool CanLeave() => _control is not null && State != SyncState.Disconnected;

    // ---- volume and offset -------------------------------------------------------------------------------------------

    /// <summary>Linear volume, 0..1. NaN is ignored; the session never sees a non-finite or out-of-range value.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            if (float.IsNaN(value)) return;
            float v = Math.Clamp(value, 0f, MaxVolume);
            if (!SetProperty(ref _volume, v)) return;
            OnPropertyChanged(nameof(VolumeText));
            if (!_pulling) Push(() => _control!.Volume = v);
        }
    }

    /// <summary>Per-device offset in ms, −100..100 (positive plays later). NaN is ignored.</summary>
    public double OffsetMs
    {
        get => _offsetMs;
        set
        {
            if (double.IsNaN(value)) return;
            double v = Math.Clamp(value, -MaxOffsetMs, MaxOffsetMs);
            if (!SetProperty(ref _offsetMs, v)) return;
            OnPropertyChanged(nameof(OffsetText));
            if (!_pulling) Push(() => _control!.OffsetMs = v);
        }
    }

    public string VolumeText => $"Volume: {Math.Round(_volume * 100).ToString("F0", CultureInfo.InvariantCulture)}%";

    public string OffsetText
    {
        get
        {
            long ms = (long)Math.Round(_offsetMs);
            return $"Offset: {(ms > 0 ? "+" : "")}{ms} ms";
        }
    }

    private void Push(Action write)
    {
        if (_control is null) return;
        try { write(); }
        catch (Exception e) { Log.Warning(e, "Applying volume/offset to the session failed"); }
    }

    // ---- marshalling -------------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="action"/> through the dispatcher hook, swallowing any exception (callers may be network threads).</summary>
    private void Post(Action action)
    {
        try
        {
            _post(() =>
            {
                try { action(); }
                catch (Exception e) { Log.Error(e, "UI update failed"); }
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not post a UI update");
        }
    }
}
