using System.IO;
using System.Security;
using Microsoft.Win32;

namespace RhythmSync.App.Services;

/// <summary>
/// Start with Windows through <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>: the value <c>RhythmSync</c> holds
/// the quoted exe path and <c>--minimized</c>. Per-user, so it needs no elevation. Never used by tests (they would write the
/// real registry); they use a fake <see cref="IStartupRegistration"/>.
/// </summary>
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "RhythmSync";

    private readonly string _exePath;

    public RegistryStartupRegistration(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        _exePath = exePath;
    }

    /// <summary>The Run value: the path in quotes (it may contain spaces) followed by the minimized switch.</summary>
    public static string RunValue(string exePath) => $"\"{exePath}\" {StartupOptions.MinimizedSwitch}";

    /// <summary>True when the Run value exists. An unreadable key counts as not enabled.</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new IOException("The Windows Run registry key could not be opened.");
        key.SetValue(ValueName, RunValue(_exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
