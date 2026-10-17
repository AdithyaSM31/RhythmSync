using RhythmSync.App;

namespace RhythmSync.App.Tests;

public class StartupOptionsTests
{
    [Fact]
    public void Defaults_to_running_firewall_setup()
        => Assert.False(StartupOptions.Parse(Array.Empty<string>()).SkipFirewall);

    [Theory]
    [InlineData("--skip-firewall")]
    [InlineData("--SKIP-FIREWALL")]
    public void Skip_firewall_switch(string arg)
        => Assert.True(StartupOptions.Parse(new[] { "--other", arg }).SkipFirewall);

    [Fact]
    public void Unknown_arguments_are_ignored()
        => Assert.False(StartupOptions.Parse(new[] { "--skip-firewalls", "firewall", "-x" }).SkipFirewall);
}
