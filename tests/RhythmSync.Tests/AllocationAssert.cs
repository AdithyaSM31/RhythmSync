namespace RhythmSync.Tests;

/// <summary>
/// Asserts that code does not allocate on the managed heap.
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> can jump by about one allocation context (7-8 KB) across a
/// collection that another thread triggered, even when the measured code allocated nothing: seen with parallel test
/// assemblies under CPU load, and reproduced outside xUnit with allocation-free code. So a window that a collection
/// ran in and that reads non-zero is measured again. Code that really allocates does so in every window and still fails.
/// </summary>
internal static class AllocationAssert
{
    private const int Windows = 10;

    /// <summary>
    /// Runs <paramref name="measured"/> until a window reads zero bytes, or reads non-zero with no collection in it
    /// (a real allocation: fails). <paramref name="beforeRetry"/>, if given, runs unmeasured before each further window.
    /// Returns how many windows ran, for callers that count side effects.
    /// </summary>
    public static int None(Action measured, Action? beforeRetry = null)
    {
        long allocated = 0;
        for (int window = 1; window <= Windows; window++)
        {
            if (window > 1) beforeRetry?.Invoke();
            int collections = GC.CollectionCount(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            measured();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated == 0) return window;
            Assert.True(GC.CollectionCount(0) != collections, $"allocated {allocated} bytes with no collection during the measurement");
        }
        Assert.Fail($"allocated in each of {Windows} windows ({allocated} bytes in the last), each with a collection during it");
        return Windows;
    }
}
