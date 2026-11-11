namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// Why the movie index is built, for its log line and Diagnostics.
/// </summary>
internal enum BuildReason
{
    /// <summary>An administrator's <c>POST /Index/Rebuild</c>.</summary>
    Rebuild,

    /// <summary>A read of the index (<see cref="MovieIndexService.GetSnapshotAsync"/>) that found none, or found it out of date.</summary>
    Request,

    /// <summary>A batch of library changes from the kit's monitor, after its quiet period.</summary>
    Changes,

    /// <summary>The post-scan task's full pass, once the scan has read the libraries.</summary>
    Scan,

    /// <summary>A save of the settings that changed what the index reads, or turned Duplicates on.</summary>
    Settings
}
