using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// The hook through which whatever references saved queries (the auto-collections) takes part in their changes. Every
/// change of a saved query that could break a reference goes through it: an edit (<see cref="UpdateAsync"/>), a delete
/// (<see cref="DeleteAsync"/>), and the bulk changes, an import, a snapshot restore, a remap of owners and an owner change
/// (<see cref="BulkChangeAsync{T}"/> with <see cref="FindBrokenReferences"/>). The deleted-user policy and the lists ask
/// <see cref="CountReferences"/>.
/// </summary>
/// <remarks>
/// <para><b>Lock order.</b> Three locks take part, always in this order: the kit's user reconciler, then the store of
/// what references saved queries (the auto-collections' definitions, whose lock is this guard's), then the saved-query
/// store. A lock may be taken while holding one before it, never one before it while holding one after it.</para>
/// <list type="bullet">
/// <item><see cref="UpdateAsync"/>, <see cref="DeleteAsync"/> and <see cref="BulkChangeAsync{T}"/> are given the
/// saved-query write as a delegate. An implementation takes its own store's lock first, checks its references under it,
/// and only then calls the delegate, which takes the saved-query store's lock: so the two stores can't deadlock, and no
/// reference can be added between the check and the write.</item>
/// <item>Nothing calls the reconciler, or any <c>IUserDataOwner</c> (the reconciler's owners: this store and the
/// referencing store), while holding the guard's lock or a store's lock: the reconciler takes its own lock and then each
/// owner's, so under the guard's lock it would wait for a lock its caller holds (a semaphore isn't re-entrant), or
/// deadlock with a reconciliation that runs at the same time. <i>Remap by name</i> therefore checks its plan under the
/// guard's lock, releases it, and then runs the reconciler; each owner checks its own part of the remap again under its
/// own lock (the saved-query store through the lock-free <see cref="FindBrokenReferences"/> and
/// <see cref="CountReferences"/>). Each owner writes on its own, so a remap refused part-way leaves the owners before the
/// refusal remapped; the refusal names them, and running the remap again is safe (an owner with nothing left to remap is
/// skipped).</item>
/// </list>
/// <para><b>Lock-free reads.</b> <see cref="CountReferences"/> and <see cref="FindBrokenReferences"/> read memory only and
/// take no lock, so they may be called anywhere, also under the saved-query store's lock (the deleted-user policy does)
/// and inside the delegates above.</para>
/// <para><b>Delete with references.</b> With <see cref="SavedQueryDeletion.DetachReferences"/>, the implementation commits
/// its detached copies first and calls the delegate only after that write succeeded: a crash between the two leaves
/// detached references and a query a retry deletes, never a reference to a missing query.</para>
/// <para><b>Bulk changes</b> are checked by the saved-query side: inside the delegate of <see cref="BulkChangeAsync{T}"/>,
/// under both locks, it works out which queries the change removes and which it changes
/// (<see cref="SavedQueryBulkChange"/>), asks <see cref="FindBrokenReferences"/>, and refuses (409
/// <c>BreaksAutoCollections</c>) when the list isn't empty, unless the administrator forced it. A dry run asks
/// <see cref="FindBrokenReferences"/> alone and lists what it returns. A real run is refused before it takes its
/// pre-operation snapshot. A remap's delegate only checks its plan; the write follows outside the lock (above). After a
/// forced change the references are reconciled
/// by their owner: a removed query is missing to them, a changed one is evaluated as it now is.</para>
/// <para>Smart Search 1.0 has nothing that references a saved query: <see cref="NoSavedQueryReferences"/>.</para>
/// </remarks>
internal interface ISavedQueryReferenceGuard
{
    /// <summary>
    /// Gets a value indicating whether what references saved queries couldn't be loaded (its store is read-only without
    /// its data), so no reference can be checked. Every change that could break one is then refused with 409
    /// <c>StoreReadOnly</c>: <see cref="UpdateAsync"/> and <see cref="DeleteAsync"/> refuse on their own, and the
    /// saved-query side refuses an owner change and a bulk change that removes or changes queries, forced or not; a
    /// query's name, description and sharing still change, and no query goes with its deleted owner. Memory only and
    /// lock-free.
    /// </summary>
    bool ReferencesUnknown { get; }

    /// <summary>Counts what references a saved query (memory only, lock-free; called for every listed query).</summary>
    /// <param name="savedQueryId">The query's id.</param>
    /// <returns>The number of references; 0 for none.</returns>
    int CountReferences(Guid savedQueryId);

    /// <summary>
    /// Checks an edit against everything that references the query and, when nothing refuses it, saves it by calling
    /// <paramref name="save"/> (under the implementation's lock).
    /// </summary>
    /// <param name="edit">The edit.</param>
    /// <param name="save">Writes the edit to the saved-query store; throws its own refusals (a name taken, a changed query).</param>
    /// <param name="cancellationToken">Cancels waiting for a lock.</param>
    /// <returns>Saved, with what it affects; or refused, with why.</returns>
    Task<SavedQueryGuardOutcome> UpdateAsync(SavedQueryEdit edit, Func<CancellationToken, Task> save, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a query: refused while something references it, unless the deletion detaches the references, which are
    /// committed first; then <paramref name="delete"/> is called.
    /// </summary>
    /// <param name="deletion">The deletion.</param>
    /// <param name="delete">Removes the query from the saved-query store.</param>
    /// <param name="cancellationToken">Cancels waiting for a lock.</param>
    /// <returns>Deleted, or refused with the references.</returns>
    Task<SavedQueryGuardOutcome> DeleteAsync(SavedQueryDeletion deletion, Func<CancellationToken, Task> delete, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the references a bulk change would break: those of the queries it removes, and those a change of their
    /// definition or owner breaks. Memory only and lock-free.
    /// </summary>
    /// <param name="change">The ids the change removes and the ids it changes.</param>
    /// <returns>The references it would break, each once; empty when none.</returns>
    IReadOnlyList<SmartSearchAutoCollectionRefDto> FindBrokenReferences(SavedQueryBulkChange change);

    /// <summary>
    /// Runs a bulk change of the saved queries under the implementation's lock: <paramref name="write"/> is called with it
    /// held, so no reference is added or changed between the check the write makes (<see cref="FindBrokenReferences"/>)
    /// and the write itself.
    /// </summary>
    /// <typeparam name="T">What the write returns.</typeparam>
    /// <param name="write">Checks and writes the change to the saved-query store; throws its refusals.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>What <paramref name="write"/> returned.</returns>
    Task<T> BulkChangeAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken);
}
