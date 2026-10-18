using RhythmSync.App;
using RhythmSync.App.Services;
using RhythmSync.Core.Config;

namespace RhythmSync.App.Tests;

/// <summary>
/// Startup registration. SAFETY: <see cref="RegistryStartupRegistration"/> is never instantiated here (it writes HKCU);
/// only its pure value and key names are checked, and the startup decision is tested directly, never by running the App.
/// </summary>
public class StartupRegistrationTests
{
    [Fact]
    public void Run_value_quotes_paths_with_spaces()
        => Assert.Equal("\"C:\\Program Files\\RhythmSync\\RhythmSync.App.exe\" --minimized",
            RegistryStartupRegistration.RunValue(@"C:\Program Files\RhythmSync\RhythmSync.App.exe"));

    [Fact]
    public void Registration_targets_the_run_key_under_the_name_rhythmsync()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryStartupRegistration.RunKey);
        Assert.Equal("RhythmSync", RegistryStartupRegistration.ValueName);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Window_shown_unless_minimized(bool flag, bool setting, bool shown)
    {
        var options = new StartupOptions(SkipFirewall: false, Minimized: flag);
        var settings = new AppSettings(Guid.NewGuid(), "PC", null, null, SettingsStore.DefaultTargetLatencyMs, null, false,
            new Dictionary<Guid, DeviceSettings>()) { StartMinimized = setting };

        Assert.Equal(shown, global::RhythmSync.App.App.ShouldShowWindowAtStartup(options, settings));
    }
}
