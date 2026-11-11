namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The places of the daily maintenance steps, in the kit's order: snapshots, the retention archive step, user
/// reconciliation, the verify sweep of attached records, re-resolution of detached records.
/// </summary>
internal static class KitMaintenanceOrder
{
    /// <summary>
    /// A SQLite database's <c>PRAGMA quick_check</c>, at the start of the run, so a damaged database is
    /// read-only before its snapshot would be taken.
    /// </summary>
    public const int IntegrityCheck = 50;

    /// <summary>The plugin's daily snapshots.</summary>
    public const int Snapshots = 100;

    /// <summary>Detached records past retention move to the archive (<c>DetachedRecords.ArchiveDueAsync</c>).</summary>
    public const int Archive = 200;

    /// <summary>The user reconciliation (<c>UserReconciler.ReconcileAsync</c>).</summary>
    public const int Users = 300;

    /// <summary>The verify sweep of attached records (<c>AttachedRecordVerifier</c>).</summary>
    public const int VerifySweep = 400;

    /// <summary>Live detached records re-resolved (<c>DetachedRecords.ResolveDetachedAsync</c>).</summary>
    public const int ResolveDetached = 500;

    /// <summary>Archived records re-resolved and moved back (<c>DetachedRecords.ReattachFromArchiveAsync</c>).</summary>
    public const int Reattach = 510;

    /// <summary>The plugin's own steps after the kit's (Bookmarks' clip-file retries).</summary>
    public const int Plugin = 600;

    /// <summary>
    /// A SQLite database's checkpoint, after every step that may write: the backstop of the
    /// checkpoints asked for since the last run (<c>SqliteDatabase.CheckpointAsync</c>).
    /// </summary>
    public const int Checkpoint = 900;
}
