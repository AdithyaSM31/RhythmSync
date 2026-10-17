using Serilog;

namespace RhythmSync.App.Services;

/// <summary>Reads Windows' network list (read-only) to tell whether a connected network is categorised Public.</summary>
public static class NetworkProfile
{
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int EnumNetworkConnected = 1;   // NLM_ENUM_NETWORK_CONNECTED
    private const int CategoryPublic = 0;         // NLM_NETWORK_CATEGORY_PUBLIC

    /// <summary>True when any connected network is Public. False when none is, or when the list cannot be read.</summary>
    public static bool AnyConnectedPublic()
    {
        try { return ReadConnectedCategories().Any(c => c == CategoryPublic); }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read the network category");
            return false;
        }
    }

    /// <summary>The category (0 Public, 1 Private, 2 Domain) of every connected network. Throws when the list cannot be read.</summary>
    internal static List<int> ReadConnectedCategories()
    {
        var type = Type.GetTypeFromCLSID(NetworkListManagerClsid) ?? throw new InvalidOperationException("NetworkListManager is not registered.");
        dynamic manager = Activator.CreateInstance(type)!;
        var categories = new List<int>();
        try
        {
            foreach (dynamic network in manager.GetNetworks(EnumNetworkConnected))
                categories.Add((int)network.GetCategory());
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(manager);
        }
        return categories;
    }
}
