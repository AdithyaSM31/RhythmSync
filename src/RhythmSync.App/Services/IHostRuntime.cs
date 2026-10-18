using RhythmSync.App.ViewModels;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Services;

/// <summary>Start, stop and observe host mode (lets the host tab view model be tested without audio or sockets).</summary>
public interface IHostRuntime
{
    bool IsHosting { get; }

    /// <summary>The running host's view model; null while not hosting.</summary>
    HostViewModel? Current { get; }

    /// <summary>Why hosting ended or could not continue after it had started (e.g. the audio device vanished); null otherwise.</summary>
    string? LastError { get; }

    /// <summary>Raised on the UI thread when hosting starts, stops or restarts.</summary>
    event Action? Changed;

    /// <summary>Devices to offer for local playback (the captured endpoint is left out).</summary>
    IReadOnlyList<AudioDeviceInfo> Speakers();

    /// <summary>The device local playback uses (the saved choice, else a real speaker); null if there is none.</summary>
    string? LocalSpeakerId { get; }

    /// <exception cref="HostStartException">Hosting could not start; the message is fit to show the user.</exception>
    Task StartAsync(string sessionName);

    Task StopAsync();

    /// <summary>Chooses the speakers the host plays on itself and saves the choice.</summary>
    void SetLocalSpeaker(string deviceId);
}

/// <summary>Hosting could not start; <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class HostStartException(string message, Exception? inner = null) : Exception(message, inner);
