using System;
using System.IO;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// Locations of the plugin's files inside its data folder (the kit's <see cref="KitPaths"/>: <c>data/accessroles</c>).
/// </summary>
internal sealed class AccessRolesPaths
{
    /// <summary>The store file's name.</summary>
    public const string StoreFileName = "accessroles.json";

    /// <summary>
    /// The emergency stop's file name (<c>SafeModeFile</c>): its existence at startup makes the plugin inert.
    /// Honoured since 0.9.13; not a setting.
    /// </summary>
    public const string SafeModeFileName = "SAFE-MODE";

    /// <summary>The library sync's hold after a restore at load (<see cref="RestoreHoldFile"/>).</summary>
    public const string RestoreHoldFileName = "library-sync-hold.json";

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesPaths"/> class.
    /// </summary>
    /// <param name="kitPaths">The plugin's data folder.</param>
    public AccessRolesPaths(KitPaths kitPaths)
        : this(kitPaths?.Root ?? throw new ArgumentNullException(nameof(kitPaths)))
    {
    }

    // Private, so dependency injection only ever sees the KitPaths constructor.
    private AccessRolesPaths(string root)
    {
        Root = root;
    }

    /// <summary>Gets the plugin's data folder.</summary>
    public string Root { get; }

    /// <summary>Gets the path of the roles and assignments file.</summary>
    public string StoreFile => Path.Combine(Root, StoreFileName);

    /// <summary>
    /// Gets the snapshot folder: daily snapshots, and pre-operation ones in its <c>pre-op</c> sub-folder.
    /// </summary>
    public string SnapshotsDirectory => Path.Combine(Root, KitPaths.SnapshotsFolder);

    /// <summary>Gets the audit log's folder, <c>audit/audit-&lt;yyyyMM&gt;.jsonl</c> (written since 0.9.2).</summary>
    public string AuditDirectory => Path.Combine(Root, "audit");

    /// <summary>Gets the emergency stop's path (honoured since 0.9.13).</summary>
    public string SafeModeFile => Path.Combine(Root, SafeModeFileName);

    /// <summary>
    /// Gets the library sync's hold after the store was restored from a snapshot at load (<c>Sync/RestoreHold</c>): while it
    /// exists, nothing is written to users' library access until an admin confirms (<c>POST Sync?confirmRestore=true</c>).
    /// </summary>
    public string RestoreHoldFile => Path.Combine(Root, RestoreHoldFileName);

    /// <summary>
    /// Creates the paths of a data folder given directly, for a store opened outside the plugin's services (tests, tools).
    /// </summary>
    /// <param name="root">The data folder.</param>
    /// <returns>The paths.</returns>
    public static AccessRolesPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return new AccessRolesPaths(root);
    }
}
