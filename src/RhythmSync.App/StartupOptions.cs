namespace RhythmSync.App;

/// <summary>
/// Command-line switches. <c>--skip-firewall</c> bypasses first-run firewall setup (no netsh, no UAC prompt);
/// <c>--minimized</c> starts without showing the window (the Run-key entry written by Start with Windows passes it).
/// </summary>
public sealed record StartupOptions(bool SkipFirewall, bool Minimized = false)
{
    public const string SkipFirewallSwitch = "--skip-firewall";
    public const string MinimizedSwitch = "--minimized";

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var list = args.ToList();
        return new StartupOptions(Has(list, SkipFirewallSwitch), Has(list, MinimizedSwitch));
    }

    private static bool Has(List<string> args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
