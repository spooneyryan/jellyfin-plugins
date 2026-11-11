namespace Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;

/// <summary>
/// How <see cref="MovePreflight"/> tells whether the destination can be written.
/// </summary>
internal enum DestinationWriteCheck
{
    /// <summary>
    /// Plan time (and a re-plan): an access check that writes nothing (<see cref="IFolderWriteAccess"/>).
    /// </summary>
    Access,

#if !MOVE_PREVIEW_ONLY
    /// <summary>
    /// Job start only, <b>inside</b> step 3a's watcher suppression scope (not built yet): the access check, then the
    /// dot-named probe file <c>.jflm-probe-&lt;jobId&gt;</c> made and deleted in the nearest existing folder, which also
    /// catches what an access check can't (a share that refuses writes, a full or failing disk). Outside that scope the
    /// probe's write changes a non-dot folder and starts a <c>FileRefresher</c> 60 s later. Not in the plan-only build
    /// (<c>MOVE_PREVIEW_ONLY</c>), which has no file write at all.
    /// </summary>
    ProbeFile,
#endif
}
