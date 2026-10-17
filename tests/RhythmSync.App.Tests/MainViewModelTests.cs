using RhythmSync.App.ViewModels;
using RhythmSync.Core.Client;

namespace RhythmSync.App.Tests;

public class MainViewModelTests
{
    private static MainViewModel Make(Action<Action>? post = null)
        => new(new ClientViewModel(() => Array.Empty<RhythmSync.Core.Net.DiscoveredHost>(),
            (_, _) => Task.FromResult(new JoinResult(JoinOutcome.Joined))), post);

    [Fact]
    public void Public_network_banner_uses_exact_copy_and_starts_hidden()
    {
        var vm = Make();
        Assert.False(vm.ShowPublicNetworkWarning);
        Assert.Equal("This Wi-Fi is set to Public – set it to Private so other PCs can connect.", vm.PublicNetworkWarningText);
    }

    [Fact]
    public void Public_network_flag_is_marshalled_and_toggles_the_banner()
    {
        var queue = new Queue<Action>();
        var vm = Make(queue.Enqueue);

        vm.SetPublicNetwork(true);
        Assert.False(vm.ShowPublicNetworkWarning);
        queue.Dequeue()();
        Assert.True(vm.ShowPublicNetworkWarning);

        vm.SetPublicNetwork(false);
        queue.Dequeue()();
        Assert.False(vm.ShowPublicNetworkWarning);
    }
}
