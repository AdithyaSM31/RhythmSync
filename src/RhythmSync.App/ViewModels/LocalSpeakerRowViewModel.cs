using CommunityToolkit.Mvvm.ComponentModel;
using RhythmSync.App.Controls;
using RhythmSync.Core.Audio.Eq;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// The "This PC" row on the Host page: the host's own speakers, with an EQ of their own. It is not a connected client
/// (it has no volume, offset or sync statistics); <see cref="HostViewModel"/> creates it, keeps <see cref="IsPlaying"/>
/// in line with the audio routing and applies and saves the EQ when the user changes it.
/// </summary>
public sealed partial class LocalSpeakerRowViewModel : ObservableObject
{
    public const string NameText = "This PC";
    public const string PlayingText = "Playing here";
    public const string NotPlayingText = "Not playing on this PC";

    internal LocalSpeakerRowViewModel(EqGains initial) => Eq = new EqViewModel(initial);

    /// <summary>The EQ of this PC's speakers.</summary>
    public EqViewModel Eq { get; }

    /// <summary>True while this PC plays the stream on its own speakers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Kind))]
    private bool _isPlaying;

    public string StatusText => IsPlaying ? PlayingText : NotPlayingText;

    public StatusKind Kind => IsPlaying ? StatusKind.Hosting : StatusKind.NotConnected;
}
