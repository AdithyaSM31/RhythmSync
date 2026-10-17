using RhythmSync.App.Services;

namespace RhythmSync.App.Tests;

public class NetworkProfileTests
{
    // Read-only query of the Windows network list; it must never throw whatever the machine's state.
    [Fact]
    public void Network_list_can_be_read_and_categories_are_valid()
    {
        var categories = NetworkProfile.ReadConnectedCategories();   // throws if the COM query is broken
        Assert.All(categories, c => Assert.InRange(c, 0, 2));
        Assert.Equal(categories.Contains(0), NetworkProfile.AnyConnectedPublic());
    }
}
