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
    {
        var options = StartupOptions.Parse(new[] { "--skip-firewalls", "firewall", "-x", "--minimizedx" });
        Assert.False(options.SkipFirewall);
        Assert.False(options.Minimized);
    }

    [Fact]
    public void Shows_the_window_unless_told_otherwise()
        => Assert.False(StartupOptions.Parse(Array.Empty<string>()).Minimized);

    [Theory]
    [InlineData("--minimized")]
    [InlineData("--MINIMIZED")]
    public void Parses_minimized_switch(string arg)
    {
        var options = StartupOptions.Parse(new[] { arg });
        Assert.True(options.Minimized);
        Assert.False(options.SkipFirewall);
    }

    [Fact]
    public void Switches_combine()
    {
        var options = StartupOptions.Parse(new[] { "--skip-firewall", "--minimized" });
        Assert.True(options.SkipFirewall);
        Assert.True(options.Minimized);
    }
}
