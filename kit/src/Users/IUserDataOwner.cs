using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// A part of a plugin that stores records belonging to users: Bookmarks' bookmarks, User Ratings'
/// ratings. The plugin registers each one as a singleton <see cref="IUserDataOwner"/>; the kit's
/// <see cref="KitUserDeletedConsumer"/> and <see cref="UserReconciler"/> call them.
/// </summary>
/// <remarks>
/// <para>Only an explicit deletion changes records: <see cref="HandleUserDeletedAsync"/> runs for Jellyfin's
/// <c>UserDeletedEventArgs</c> and for the admin's <i>Apply deleted-user policy</i>, after <see cref="SnapshotBeforeAsync"/>.
/// The reconciler only reads (<see cref="CountRecordsByUser"/>) and refreshes names (<see cref="RefreshKnownUsersAsync"/>),
/// and <i>Remap by name</i> moves records with <see cref="RemapUsersAsync"/>. None of them is called while
/// <see cref="IsReadOnly"/> is true.</para>
/// <para>A JSON-store owner keeps <see cref="KnownUsers"/> in its document and takes its pre-operation snapshot with
/// <c>JsonDocumentStore.PreOpSnapshotAsync</c>.</para>
/// </remarks>
internal interface IUserDataOwner
{
    /// <summary>
    /// Gets the owner's name for logs and Diagnostics, normally its store's name (<c>bookmarks</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the owner's store refuses writes: the kit then leaves it alone.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets the last seen names of the users the owner holds records of.
    /// </summary>
    KnownUsers KnownUsers { get; }

    /// <summary>
    /// Counts the records of every user id the owner stores, users the server no longer knows included. Called on every
    /// reconciliation and Diagnostics request, so it reads memory only.
    /// </summary>
    /// <returns>User id to number of records; ids without records are left out.</returns>
    IReadOnlyDictionary<Guid, int> CountRecordsByUser();

    /// <summary>
    /// Refreshes <see cref="KnownUsers"/> with the server's names (<see cref="KnownUsers.Refresh"/>), writing only when a
    /// name changed.
    /// </summary>
    /// <param name="currentNames">The server's users, id to name.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the pre-operation snapshot before the kit changes records (a pre-operation copy; the newest 5 are kept).
    /// </summary>
    /// <param name="operation">One of <see cref="Storage.SnapshotOperations"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot.</param>
    /// <returns>The snapshot's file name, or <c>null</c> when none was taken.</returns>
    Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken);

    /// <summary>
    /// Tells whether applying a policy to a user would change any of the owner's records: the reconciler takes the
    /// owner's pre-operation snapshot only when it would for one of the users, and still calls
    /// <see cref="HandleUserDeletedAsync"/> for each. <c>true</c> unless the owner says otherwise, so an owner that
    /// doesn't implement it is snapshotted as before; User Ratings answers <c>false</c> for
    /// <see cref="DeletedUserPolicy.KeepAsOrphan"/> (no rating changes) and for a user whose ratings are all gone
    /// already.
    /// </summary>
    /// <param name="userId">The deleted user.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>Whether a record would change.</returns>
    bool WouldChangeRecords(Guid userId, DeletedUserPolicy policy) => true;

    /// <summary>
    /// Applies the plugin's policy to a deleted user's records.
    /// </summary>
    /// <param name="userId">The deleted user.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many records were removed or changed.</returns>
    Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the records of <paramref name="oldId"/> that <see cref="RemapUsersAsync"/> would leave in conflict with
    /// <paramref name="newId"/>'s own (User Ratings: the old id's attached ratings of items the new id rated too, of
    /// which the older is detached as Ambiguous). A remap bound to its dry run hashes it into the plan's token, so a
    /// conflict that turned up since the dry run makes the real run refuse. 0 unless the owner says otherwise.
    /// </summary>
    /// <param name="oldId">The stored id.</param>
    /// <param name="newId">The server user's id.</param>
    /// <returns>The count.</returns>
    int CountRemapConflicts(Guid oldId, Guid newId) => 0;

    /// <summary>
    /// Gives the records of old user ids to new ones (<i>Remap by name</i>), moving their <see cref="KnownUsers"/> entries
    /// too (<see cref="KnownUsers.Remap"/>). A new id that already owns records keeps them; the old id's are added.
    /// </summary>
    /// <param name="map">Old id to new id.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many records moved.</returns>
    Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken);
}
