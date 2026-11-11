namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Why the mass-removal guard held removals back (stored, and given to clients as they are).</summary>
internal static class GuardReasons
{
    /// <summary>The collection would lose every member.</summary>
    public const string Empty = "Empty";

    /// <summary>The run (or its pass) started while a library scan was running.</summary>
    public const string ScanRunning = "ScanRunning";

    /// <summary>A folder of a library the definition reads can't be reached.</summary>
    public const string LocationUnreachable = "LocationUnreachable";

    /// <summary>The query found less than half as many candidates as in the last run.</summary>
    public const string CandidateDrop = "CandidateDrop";

    /// <summary>The pass after the server started.</summary>
    public const string StartupPass = "StartupPass";
}
