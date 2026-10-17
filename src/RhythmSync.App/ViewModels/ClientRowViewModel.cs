using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// One connected client as the host sees it: name, sync state, the statistics from its heartbeats, and the volume and
/// offset the host can set for it. Created and updated by <see cref="HostViewModel"/> on the UI thread. A value change
/// made by the user (not one adopted from the host) is reported through the callback given at construction.
/// </summary>
public sealed partial class ClientRowViewModel : ObservableObject
{
    /// <summary>Late-packet percentage above which the row warns about the Wi-Fi.</summary>
    public const double CongestedLatePct = 5;

    public const string CongestedMessage = "Wi-Fi congested – try higher latency";
    public const float MaxVolume = 1f;
    public const double MaxOffsetMs = 100;

    private readonly Action<ClientRowViewModel> _userChanged;
    private float _volume = MaxVolume;
    private double _offsetMs;
    private bool _adopting;

    internal ClientRowViewModel(ConnectedClient client, Action<ClientRowViewModel> userChanged)
    {
        ArgumentNullException.ThrowIfNull(client);
        _userChanged = userChanged ?? throw new ArgumentNullException(nameof(userChanged));
        DeviceId = client.DeviceId;
        Apply(client);
        Adopt(client.Volume, client.OffsetMs);
    }

    public Guid DeviceId { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SyncState _state = SyncState.Connecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncErrorText))]
    private double _syncErrorMs = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LossText))]
    private double _lossPct = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LateText), nameof(Congested), nameof(CongestedText))]
    private double _latePct = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriftText))]
    private double _driftPpm = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FitResidualText))]
    private double _fitResidualMs = double.NaN;

    /// <summary>More than <see cref="CongestedLatePct"/> % of packets arrived late. Unknown (NaN) is not congested.</summary>
    public bool Congested => LatePct > CongestedLatePct;

    /// <summary>The warning for a congested row; empty otherwise.</summary>
    public string CongestedText => Congested ? CongestedMessage : "";

    public string StatusText => State switch
    {
        SyncState.Connecting => "Connecting…",
        SyncState.Syncing => "Syncing…",
        SyncState.InSync => "In sync",
        SyncState.Degraded => "Sync is degraded",
        _ => "Not connected",
    };

    public string SyncErrorText => StatFormat.WithUnit(SyncErrorMs, 2, "ms");
    public string LossText => StatFormat.WithUnit(LossPct, 1, "%");
    public string LateText => StatFormat.WithUnit(LatePct, 1, "%");
    public string DriftText => StatFormat.WithUnit(DriftPpm, 0, "ppm");
    public string FitResidualText => StatFormat.WithUnit(FitResidualMs, 2, "ms");

    // ---- volume and offset -------------------------------------------------------------------------------------------

    /// <summary>Linear volume, 0..1. NaN is ignored; infinities clamp.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            if (float.IsNaN(value)) return;
            float v = Math.Clamp(value, 0f, MaxVolume);
            if (!SetProperty(ref _volume, v)) return;
            OnPropertyChanged(nameof(VolumeText));
            if (!_adopting) _userChanged(this);
        }
    }

    /// <summary>Per-client offset in ms, −100..100 (positive plays later). NaN is ignored; infinities clamp.</summary>
    public double OffsetMs
    {
        get => _offsetMs;
        set
        {
            if (double.IsNaN(value)) return;
            double v = Math.Clamp(value, -MaxOffsetMs, MaxOffsetMs);
            if (!SetProperty(ref _offsetMs, v)) return;
            OnPropertyChanged(nameof(OffsetText));
            if (!_adopting) _userChanged(this);
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

    // ---- updates from the host ---------------------------------------------------------------------------------------

    /// <summary>Takes the name, readiness and latest heartbeat statistics from the host's snapshot.</summary>
    internal void Apply(ConnectedClient client)
    {
        Name = client.Name;
        if (client.LastStats is { } s)
        {
            State = s.State;
            SyncErrorMs = s.SyncErrorMs;
            LossPct = s.LossPct;
            LatePct = s.LatePct;
            DriftPpm = s.DriftPpm;
            FitResidualMs = s.FitResidualMs;
        }
        else
        {
            State = client.Ready ? SyncState.Syncing : SyncState.Connecting;
            SyncErrorMs = LossPct = LatePct = DriftPpm = FitResidualMs = double.NaN;
        }
    }

    /// <summary>Sets volume and offset without reporting them as a user change (values the host or the settings store supplied).</summary>
    internal void Adopt(float volume, double offsetMs)
    {
        _adopting = true;
        try
        {
            Volume = volume;
            OffsetMs = offsetMs;
        }
        finally
        {
            _adopting = false;
        }
    }
}
