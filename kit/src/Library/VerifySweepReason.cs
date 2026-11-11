namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Why the verify sweep ran.
/// </summary>
internal enum VerifySweepReason
{
    /// <summary>Once when the plugin starts, after the startup gate: events raised while it wasn't listening are lost.</summary>
    Startup,

    /// <summary>A batch listed removed folders, whose descendants were deleted without events.</summary>
    RemovedFolders,

    /// <summary>The daily maintenance task.</summary>
    Daily,

    /// <summary>After the data changed as a whole: a restored snapshot or an import.</summary>
    DataRestored,

    /// <summary>Asked for by an admin or a developer check.</summary>
    Requested
}
