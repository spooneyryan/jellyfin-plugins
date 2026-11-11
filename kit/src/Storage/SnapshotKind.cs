namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The two retention classes of snapshots.
/// </summary>
internal enum SnapshotKind
{
    /// <summary>The daily snapshots of the maintenance task, <c>snapshots/&lt;store&gt;-&lt;ts&gt;.json</c> (<c>SnapshotsToKeep</c>).</summary>
    Daily,

    /// <summary>Snapshots before risky operations, <c>snapshots/pre-op/&lt;store&gt;-&lt;operation&gt;-&lt;ts&gt;.json</c> (newest 5).</summary>
    PreOp
}
