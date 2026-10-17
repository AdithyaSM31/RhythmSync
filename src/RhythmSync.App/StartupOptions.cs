namespace RhythmSync.App;

/// <summary>Command-line switches. <c>--skip-firewall</c> bypasses first-run firewall setup (no netsh, no UAC prompt).</summary>
public sealed record StartupOptions(bool SkipFirewall)
{
    public const string SkipFirewallSwitch = "--skip-firewall";

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return new StartupOptions(args.Any(a => string.Equals(a, SkipFirewallSwitch, StringComparison.OrdinalIgnoreCase)));
    }
}
