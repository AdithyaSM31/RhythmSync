using RhythmSync.App.Services;

namespace RhythmSync.App.Tests;

// Only the pure argument builder is tested; EnsureRules (elevation, netsh) is never run from tests.
public class FirewallSetupTests
{
    [Fact]
    public void Builds_the_private_profile_allow_rule_for_the_exe()
    {
        string args = FirewallSetup.BuildNetshArguments(@"C:\Program Files\RhythmSync\RhythmSync.exe");

        Assert.Equal(
            """advfirewall firewall add rule name="RhythmSync" dir=in action=allow program="C:\Program Files\RhythmSync\RhythmSync.exe" profile=private""",
            args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""C:\evil" dir=out action=block program="x""")]
    public void Rejects_empty_or_quote_containing_paths(string path)
        => Assert.Throws<ArgumentException>(() => FirewallSetup.BuildNetshArguments(path));

    [Fact]
    public void Rejects_null_path()
        => Assert.Throws<ArgumentNullException>(() => FirewallSetup.BuildNetshArguments(null!));
}
