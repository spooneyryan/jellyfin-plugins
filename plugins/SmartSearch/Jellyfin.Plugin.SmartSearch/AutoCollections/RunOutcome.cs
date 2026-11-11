namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The outcomes a run records in the history.</summary>
internal static class RunOutcome
{
    /// <summary>The collection was created.</summary>
    public const string Created = "Created";

    /// <summary>The collection was written.</summary>
    public const string Written = "Written";

    /// <summary>Nothing differed: no write.</summary>
    public const string NoChange = "NoChange";

    /// <summary>The run stopped before writing (see the state's status and reason).</summary>
    public const string Stopped = "Stopped";

    /// <summary>The run failed.</summary>
    public const string Failed = "Failed";
}
