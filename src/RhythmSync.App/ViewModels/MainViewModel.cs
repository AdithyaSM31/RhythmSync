using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace RhythmSync.App.ViewModels;

/// <summary>The main window: the client screen plus the app-wide Public-network warning. Host mode joins it in the next task.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string PublicNetworkText = "This Wi-Fi is set to Public – set it to Private so other PCs can connect.";

    private readonly Action<Action> _post;

    public MainViewModel(ClientViewModel client, Action<Action>? post = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _post = post ?? (a => a());
    }

    public ClientViewModel Client { get; }

    [ObservableProperty]
    private bool _showPublicNetworkWarning;

    public string PublicNetworkWarningText => PublicNetworkText;

    /// <summary>Shows or hides the banner. Safe to call from any thread.</summary>
    public void SetPublicNetwork(bool isPublic)
    {
        try { _post(() => ShowPublicNetworkWarning = isPublic); }
        catch (Exception e) { Log.Warning(e, "Could not post the network warning"); }
    }
}
