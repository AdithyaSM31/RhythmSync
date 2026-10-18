using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmSync.App.Services;
using Serilog;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// The Settings page: this PC's name, Start with Windows, Start minimized, and the About card (version and folder buttons).
/// Runs on the UI thread.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const int MaxNameLength = 64;
    public const string NameError = "Enter a name (up to 64 characters).";
    public const string StartupError = "Couldn't change the startup setting.";

    private readonly SettingsPersister _settings;
    private readonly IStartupRegistration _startup;
    private readonly Action<string> _openFolder;
    private readonly string _dataDir;
    private bool _startWithWindows;
    private bool _startMinimized;

    public SettingsViewModel(SettingsPersister settings, IStartupRegistration startup, string version, Action<string> openFolder, string dataDir)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _openFolder = openFolder ?? throw new ArgumentNullException(nameof(openFolder));
        _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        VersionText = $"RhythmSync {version}";
        _deviceName = settings.Current.DeviceName;
        _startMinimized = settings.Current.StartMinimized;
        _startWithWindows = startup.IsEnabled;
    }

    public string VersionText { get; }

    // ---- this PC's name ----------------------------------------------------------------------------------------------

    /// <summary>The text in the name box. It is saved by <see cref="CommitNameCommand"/> (Enter or lost focus), not on every keystroke.</summary>
    [ObservableProperty]
    private string _deviceName;

    [ObservableProperty]
    private string? _nameErrorText;

    /// <summary>Saves the trimmed name when it is 1–64 characters; otherwise shows <see cref="NameError"/> and saves nothing.</summary>
    [RelayCommand]
    private void CommitName()
    {
        string name = (DeviceName ?? "").Trim();
        if (name.Length is 0 or > MaxNameLength)
        {
            NameErrorText = NameError;
            return;
        }

        NameErrorText = null;
        DeviceName = name;
        if (name != _settings.Current.DeviceName)
            _settings.Update(s => s with { DeviceName = name }, immediate: true);
    }

    // ---- start with Windows ------------------------------------------------------------------------------------------

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (value == _startWithWindows) return;
            try
            {
                if (value) _startup.Enable();
                else _startup.Disable();
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not change the Start with Windows setting");
                StartupErrorText = StartupError;
                OnPropertyChanged();   // the field is unchanged: tells a bound toggle to flip back
                return;
            }

            _startWithWindows = value;
            StartupErrorText = null;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string? _startupErrorText;

    // ---- start minimized ---------------------------------------------------------------------------------------------

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!SetProperty(ref _startMinimized, value)) return;
            _settings.Update(s => s with { StartMinimized = value }, immediate: true);
        }
    }

    // ---- about -------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenLogFolder() => _openFolder(Path.Combine(_dataDir, "logs"));

    [RelayCommand]
    private void OpenSettingsFolder() => _openFolder(_dataDir);
}
