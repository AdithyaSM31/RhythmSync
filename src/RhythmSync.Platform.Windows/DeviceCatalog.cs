using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace RhythmSync.Platform.Windows;

public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Enumerates active render endpoints.</summary>
public static class DeviceCatalog
{
    public static IReadOnlyList<AudioDeviceInfo> RenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
        }
        return result;
    }

    /// <summary>Id of the default (multimedia) render endpoint, or null when the PC has none.</summary>
    public static string? DefaultRenderId()
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (COMException)
        {
            return null;    // E_NOTFOUND: no active render device
        }
    }

    /// <summary>The VB-CABLE virtual render device (its name contains "CABLE Input"), if installed.</summary>
    public static AudioDeviceInfo? FindVbCableRender() =>
        RenderDevices().FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
}
