namespace RhythmSync.Platform.Windows;

public enum HostAudioStatus { Ok, VbCableMissing, VbCableNotDefault, LocalSpeakerIsCaptured }

/// <summary>Checks that the host's audio routing suits loopback capture through VB-CABLE.</summary>
public static class HostAudioCheck
{
    /// <summary>
    /// Evaluates the routing; the first matching condition wins:
    /// <list type="number">
    /// <item><see cref="HostAudioStatus.VbCableMissing"/>: <paramref name="vbCableId"/> is null or empty.</item>
    /// <item><see cref="HostAudioStatus.LocalSpeakerIsCaptured"/>: the local speaker is the captured device (playing there
    /// would feed the delayed output back into the capture).</item>
    /// <item><see cref="HostAudioStatus.VbCableNotDefault"/>: the default render device is not the VB-CABLE.</item>
    /// <item><see cref="HostAudioStatus.Ok"/>.</item>
    /// </list>
    /// Only <see cref="HostAudioStatus.Ok"/> allows local playback. Endpoint ids are compared ignoring case. Pure; takes
    /// endpoint ids from <see cref="DeviceCatalog"/>.
    /// </summary>
    /// <param name="capturedId">The endpoint the loopback capture records; null means it captures the default render device.</param>
    public static HostAudioStatus Evaluate(string? defaultRenderId, string? vbCableId, string? capturedId, string? localSpeakerId)
    {
        if (string.IsNullOrEmpty(vbCableId)) return HostAudioStatus.VbCableMissing;
        string? captured = capturedId ?? defaultRenderId;
        if (localSpeakerId is not null && Same(localSpeakerId, captured)) return HostAudioStatus.LocalSpeakerIsCaptured;
        if (!Same(defaultRenderId, vbCableId)) return HostAudioStatus.VbCableNotDefault;
        return HostAudioStatus.Ok;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
