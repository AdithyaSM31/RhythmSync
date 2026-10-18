using RhythmSync.App.Services;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Config;

namespace RhythmSync.App.Tests;

/// <summary>The Settings page's view model against a fake <see cref="IStartupRegistration"/> and a SettingsStore in a temp directory (never the registry, never %AppData%).</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    internal sealed class FakeStartup : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public Exception? Failure { get; set; }
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public void Enable()
        {
            EnableCalls++;
            if (Failure is not null) throw Failure;
            IsEnabled = true;
        }

        public void Disable()
        {
            DisableCalls++;
            if (Failure is not null) throw Failure;
            IsEnabled = false;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rhythmsync-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;
    private readonly SettingsPersister _persister;
    private readonly FakeStartup _startup = new();
    private readonly List<string> _opened = new();

    public SettingsViewModelTests()
    {
        _store = new SettingsStore(_dir);
        var initial = _store.Load() with { DeviceName = "Kitchen Laptop" };
        _store.Save(initial);   // on disk too, so "not saved" can be told from "saved"
        _persister = new SettingsPersister(_store, initial, TimeSpan.FromHours(1));
    }

    public void Dispose()
    {
        _persister.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private SettingsViewModel Make() => new(_persister, _startup, "1.2.3", _opened.Add, @"C:\Data\RhythmSync");

    private string PersistedName
    {
        get
        {
            _persister.Flush();
            return _store.Load().DeviceName;
        }
    }

    [Fact]
    public void Starts_from_the_saved_settings()
    {
        _startup.IsEnabled = true;
        _persister.Update(s => s with { StartMinimized = true });

        var vm = Make();

        Assert.Equal("Kitchen Laptop", vm.DeviceName);
        Assert.True(vm.StartWithWindows);
        Assert.True(vm.StartMinimized);
        Assert.Null(vm.NameErrorText);
        Assert.Null(vm.StartupErrorText);
        Assert.Equal("RhythmSync 1.2.3", vm.VersionText);
    }

    [Fact]
    public void Copy_is_exact()
    {
        Assert.Equal(64, SettingsViewModel.MaxNameLength);
        Assert.Equal("Enter a name (up to 64 characters).", SettingsViewModel.NameError);
        Assert.Equal("Couldn't change the startup setting.", SettingsViewModel.StartupError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Invalid_name_is_not_saved(string? name)
    {
        var vm = Make();
        vm.DeviceName = name!;

        vm.CommitNameCommand.Execute(null);

        Assert.Equal(SettingsViewModel.NameError, vm.NameErrorText);
        Assert.Equal("Kitchen Laptop", _persister.Current.DeviceName);
        Assert.Equal("Kitchen Laptop", PersistedName);
    }

    [Fact]
    public void A_name_over_64_characters_is_not_saved()
    {
        var vm = Make();
        vm.DeviceName = new string('x', SettingsViewModel.MaxNameLength + 1);

        vm.CommitNameCommand.Execute(null);

        Assert.Equal(SettingsViewModel.NameError, vm.NameErrorText);
        Assert.Equal("Kitchen Laptop", _persister.Current.DeviceName);
    }

    [Fact]
    public void A_name_of_exactly_64_characters_is_saved()
    {
        var vm = Make();
        string name = new('x', SettingsViewModel.MaxNameLength);
        vm.DeviceName = name;

        vm.CommitNameCommand.Execute(null);

        Assert.Null(vm.NameErrorText);
        Assert.Equal(name, PersistedName);
    }

    [Fact]
    public void Valid_name_is_trimmed_and_saved()
    {
        var vm = Make();
        vm.DeviceName = "  Kitchen  ";

        vm.CommitNameCommand.Execute(null);

        Assert.Equal("Kitchen", PersistedName);
        Assert.Equal("Kitchen", _persister.Current.DeviceName);
        Assert.Equal("Kitchen", vm.DeviceName);   // the box shows what was saved
        Assert.Null(vm.NameErrorText);
    }

    [Fact]
    public void A_valid_name_clears_an_earlier_error()
    {
        var vm = Make();
        vm.DeviceName = "";
        vm.CommitNameCommand.Execute(null);
        Assert.NotNull(vm.NameErrorText);

        vm.DeviceName = "Den";
        vm.CommitNameCommand.Execute(null);

        Assert.Null(vm.NameErrorText);
        Assert.Equal("Den", PersistedName);
    }

    [Fact]
    public void Committing_a_name_leaves_the_other_settings_alone()
    {
        var vm = Make();
        var before = _persister.Current;
        vm.DeviceName = "Den";

        vm.CommitNameCommand.Execute(null);

        Assert.Equal(before with { DeviceName = "Den" }, _persister.Current);
    }

    [Fact]
    public void Start_with_windows_reads_and_writes_registration()
    {
        _startup.IsEnabled = true;
        var vm = Make();
        Assert.True(vm.StartWithWindows);

        vm.StartWithWindows = false;

        Assert.Equal(1, _startup.DisableCalls);
        Assert.False(_startup.IsEnabled);
        Assert.False(vm.StartWithWindows);

        vm.StartWithWindows = true;

        Assert.Equal(1, _startup.EnableCalls);
        Assert.True(_startup.IsEnabled);
        Assert.True(vm.StartWithWindows);
        Assert.Null(vm.StartupErrorText);
    }

    [Fact]
    public void Setting_the_same_start_with_windows_value_does_not_touch_the_registration()
    {
        var vm = Make();

        vm.StartWithWindows = false;

        Assert.Equal(0, _startup.EnableCalls + _startup.DisableCalls);
    }

    [Fact]
    public void Failed_registration_reverts_toggle_and_shows_error()
    {
        _startup.Failure = new UnauthorizedAccessException("denied");
        var vm = Make();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.StartWithWindows = true;

        Assert.False(vm.StartWithWindows);
        Assert.Equal(SettingsViewModel.StartupError, vm.StartupErrorText);
        Assert.Contains(nameof(SettingsViewModel.StartWithWindows), changed);   // so a bound toggle flips back
    }

    [Fact]
    public void A_later_successful_change_clears_the_startup_error()
    {
        _startup.Failure = new UnauthorizedAccessException("denied");
        var vm = Make();
        vm.StartWithWindows = true;
        Assert.NotNull(vm.StartupErrorText);

        _startup.Failure = null;
        vm.StartWithWindows = true;

        Assert.True(vm.StartWithWindows);
        Assert.Null(vm.StartupErrorText);
    }

    [Fact]
    public void Start_minimized_is_persisted()
    {
        var vm = Make();

        vm.StartMinimized = true;

        Assert.True(_persister.Current.StartMinimized);
        Assert.True(_store.Load().StartMinimized);   // written at once, not after the debounce

        vm.StartMinimized = false;

        Assert.False(_store.Load().StartMinimized);
    }

    [Fact]
    public void Folder_buttons_open_logs_and_data_folders()
    {
        var vm = Make();

        vm.OpenLogFolderCommand.Execute(null);
        vm.OpenSettingsFolderCommand.Execute(null);

        Assert.Equal(new[] { @"C:\Data\RhythmSync\logs", @"C:\Data\RhythmSync" }, _opened);
    }
}
