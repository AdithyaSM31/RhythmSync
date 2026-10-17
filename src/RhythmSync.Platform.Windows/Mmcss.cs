using System.Runtime.InteropServices;

namespace RhythmSync.Platform.Windows;

/// <summary>Multimedia Class Scheduler Service registration for audio threads.</summary>
public static partial class Mmcss
{
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>
    /// Registers the calling thread with the "Pro Audio" MMCSS task. Dispose on the same thread to revert.
    /// Failure to join is non-fatal: the returned object is then a no-op.
    /// </summary>
    public static IDisposable JoinProAudio()
    {
        try
        {
            uint taskIndex = 0;
            IntPtr handle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            return handle == IntPtr.Zero ? NoOp.Instance : new Registration(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return NoOp.Instance;
        }
    }

    private sealed class Registration(IntPtr handle) : IDisposable
    {
        private IntPtr _handle = handle;

        public void Dispose()
        {
            IntPtr h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero) AvRevertMmThreadCharacteristics(h);
        }
    }

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }
}
