using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace RhythmSync.App.Services;

/// <summary>
/// First-run Windows Firewall rule: one inbound allow rule for this exe on the Private profile, added by an elevated
/// <c>netsh</c> (UAC prompt). Never called from tests or when <c>--skip-firewall</c> is given.
/// </summary>
public static class FirewallSetup
{
    public const string RuleName = "RhythmSync";

    /// <summary>The <c>netsh</c> arguments for the rule (pure; no side effects).</summary>
    public static string BuildNetshArguments(string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        if (string.IsNullOrWhiteSpace(exePath) || exePath.Contains('"'))
            throw new ArgumentException("The program path must be non-empty and must not contain quotes.", nameof(exePath));
        return $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exePath}\" profile=private";
    }

    /// <summary>Runs the elevated rule creation and waits for it. False when it failed or the user declined the UAC prompt.</summary>
    public static bool EnsureRules(string exePath)
    {
        var info = new ProcessStartInfo("netsh", BuildNetshArguments(exePath))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception e)
        {
            Log.Warning(e, "Firewall rule was not added (elevation declined or netsh unavailable)");
            return false;
        }
    }
}
