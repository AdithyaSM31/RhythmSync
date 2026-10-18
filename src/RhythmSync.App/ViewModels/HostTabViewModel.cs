using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmSync.App.Services;
using RhythmSync.Platform.Windows;
using Serilog;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// The Host tab: before hosting, the session name, the speakers to play on and a Start button; while hosting, the running
/// host (<see cref="Current"/>), the speakers to play on (changeable while running) and a Stop button. Runs on the UI thread.
/// </summary>
public sealed partial class HostTabViewModel : ObservableObject
{
    public const string DefaultSpeakersText = "Windows default";

    private readonly IHostRuntime _runtime;
    private bool _syncingSpeaker;

    public HostTabViewModel(IHostRuntime runtime, string sessionName)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _sessionName = sessionName ?? "";
        _runtime.Changed += OnRuntimeChanged;
        ReloadSpeakers();
        OnRuntimeChanged();
    }

    /// <summary>The name for the next session (once hosting, the running host's own <see cref="HostViewModel.SessionName"/> is the live one).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _sessionName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotHosting))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isHosting;

    public bool IsNotHosting => !IsHosting;

    /// <summary>The running host; null while not hosting.</summary>
    [ObservableProperty]
    private HostViewModel? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isBusy;

    public ObservableCollection<AudioDeviceInfo> Speakers { get; } = new();

    private AudioDeviceInfo? _selectedSpeaker;

    /// <summary>The speakers this PC plays on while hosting. Choosing one saves it and, while hosting, switches local playback to it.</summary>
    public AudioDeviceInfo? SelectedSpeaker
    {
        get => _selectedSpeaker;
        set
        {
            if (!SetProperty(ref _selectedSpeaker, value) || _syncingSpeaker || value is null) return;
            try { _runtime.SetLocalSpeaker(value.Id); }
            catch (Exception e) { Log.Warning(e, "Choosing the local speakers failed"); }
        }
    }

    private bool CanStart() => !IsHosting && !IsBusy && !string.IsNullOrWhiteSpace(SessionName);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ErrorText = null;
        IsBusy = true;
        try
        {
            await _runtime.StartAsync(SessionName.Trim());
        }
        catch (HostStartException e)
        {
            ErrorText = e.Message;
        }
        catch (Exception e)
        {
            Log.Error(e, "Starting the host failed");
            ErrorText = $"Can't start hosting: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStop() => IsHosting && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsBusy = true;
        try { await _runtime.StopAsync(); }
        catch (Exception e) { Log.Error(e, "Stopping the host failed"); }
        finally { IsBusy = false; }
    }

    /// <summary>Refreshes the device list (call when the tab is shown).</summary>
    public void ReloadSpeakers()
    {
        try
        {
            var devices = _runtime.Speakers();
            string? selected = _runtime.LocalSpeakerId;
            _syncingSpeaker = true;
            try
            {
                Speakers.Clear();
                foreach (var d in devices) Speakers.Add(d);
                SelectedSpeaker = selected is null ? null : Speakers.FirstOrDefault(d => string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _syncingSpeaker = false;
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Listing the speakers failed");
        }
    }

    private void OnRuntimeChanged()
    {
        IsHosting = _runtime.IsHosting;
        Current = _runtime.Current;
        if (!IsHosting && _runtime.LastError is { } error) ErrorText = error;
        ReloadSpeakers();   // the running host's picker needs a fresh list too (devices come and go)
    }
}
