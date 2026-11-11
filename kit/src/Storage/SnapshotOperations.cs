using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The operations that take a <b>pre-operation snapshot</b> first:
/// <c>snapshots/pre-op/&lt;store&gt;-&lt;operation&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c>, its own retention class (the newest
/// <see cref="ToKeep"/> of a store), so that retries never push the daily snapshots out.
/// </summary>
internal static class SnapshotOperations
{
    /// <summary>The sub-folder of the snapshot folder that holds pre-operation snapshots.</summary>
    public const string Folder = "pre-op";

    /// <summary>How many pre-operation snapshots of a store are kept (the newest 5).</summary>
    public const int ToKeep = 5;

    /// <summary>
    /// At most this many pre-operation snapshots of a store are kept when a store also keeps them by age
    /// (<see cref="SnapshotRotation.RotatePreOp"/>'s <c>keepWithin</c>).
    /// </summary>
    public const int MaxKeptByAge = 50;

    /// <summary><i>Restore snapshot</i>: the state being replaced.</summary>
    public const string Restore = "restore";

    /// <summary>An import that writes (<c>Merge</c> or <c>Replace</c>).</summary>
    public const string Import = "import";

    /// <summary><i>Remap by name</i>.</summary>
    public const string Remap = "remap";

    /// <summary><i>Apply deleted-user policy</i> on users the reconciler listed.</summary>
    public const string ApplyPolicy = "apply-policy";

    /// <summary>The deleted-user policy applied for Jellyfin's <c>UserDeletedEventArgs</c>.</summary>
    public const string UserDeleted = "user-deleted";

    /// <summary>The admin's <i>Purge</i> of detached records.</summary>
    public const string Purge = "purge";

    /// <summary>A plugin-defined risky operation, such as a JE import.</summary>
    public const string Other = "other";

    /// <summary>A moderator's removal of a member's rating or review.</summary>
    public const string Moderate = "moderate";

    /// <summary>A SQLite schema upgrade (<c>SqliteDatabase</c>).</summary>
    public const string Migrate = "migrate";

    /// <summary>
    /// Gets every operation name, the only ones a pre-operation snapshot's name may carry.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Restore, Import, Remap, ApplyPolicy, UserDeleted, Purge, Other, Migrate, Moderate];

    /// <summary>
    /// Checks an operation name.
    /// </summary>
    /// <param name="operation">The name.</param>
    /// <returns>The same name, for chaining.</returns>
    /// <exception cref="ArgumentException">It isn't one of <see cref="All"/>.</exception>
    public static string Require(string operation)
    {
        if (!All.Contains(operation, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{operation}' isn't a snapshot operation; use one of {string.Join(", ", All)}.", nameof(operation));
        }

        return operation;
    }
}
