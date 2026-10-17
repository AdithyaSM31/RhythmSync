using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace RhythmSync.App.ViewModels;

/// <summary>The main window: the client screen, the host screen and the app-wide Public-network warning.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string PublicNetworkText = "This Wi-Fi is set to Public – set it to Private so other PCs can connect.";

    private readonly Action<Action> _post;

    public MainViewModel(ClientViewModel client, Action<Action>? post = null, HostTabViewModel? host = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Host = host;
        _post = post ?? (a => a());
        if (host is not null) host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostTabViewModel.IsHosting)) OnPropertyChanged(nameof(ClientEnabled));
        };
    }

    public ClientViewModel Client { get; }

    /// <summary>The Host tab; null when host mode is unavailable.</summary>
    public HostTabViewModel? Host { get; }

    /// <summary>False while hosting: this PC cannot also be a client of another host.</summary>
    public bool ClientEnabled => Host is not { IsHosting: true };

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
