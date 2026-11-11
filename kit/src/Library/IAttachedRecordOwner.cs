using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A plugin store of item-linked records as the kit's <see cref="AttachedRecordVerifier"/> sees it: Bookmarks'
/// bookmarks, User Ratings' ratings, Library Maintenance's ignore marks. Every record stores <c>ItemId</c>,
/// <c>Identity</c> (an <see cref="IdentitySnapshot"/>) and <c>DetachedUtc</c>; a record is <b>attached</b> while
/// <c>DetachedUtc</c> is null. The plugin registers each one as a singleton of its own type and adds it with
/// <c>AddKitRecordOwner&lt;T&gt;()</c>, which creates each owner on its own (one that can't be created costs only
/// itself); an owner registered as <see cref="IAttachedRecordOwner"/> directly is not seen.
/// </summary>
/// <remarks>
/// <para>The verify sweep asks <see cref="GetAttachedItemIds"/>, looks each id up with the cache-backed
/// <c>GetItemById</c> and calls <see cref="DetachAsync"/> with the ids the library no longer has. Neither is called
/// while <see cref="IsReadOnly"/> is true (every read-only status is inert).</para>
/// <para>Records that never had a library item (<see cref="Guid.Empty"/>, Bookmarks' adopted clips) are the owner's to
/// leave out; the kit never looks up the empty GUID, which throws on 10.11.5.</para>
/// </remarks>
internal interface IAttachedRecordOwner
{
    /// <summary>
    /// Gets the owner's name for logs and Diagnostics, normally its store's name (<c>bookmarks</c>). An archive of the owner
    /// is <c>archive/&lt;Name&gt;-archived.jsonl</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the owner's store refuses writes: the kit then leaves it alone.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets the distinct item ids of the attached records. Called at every sweep, so it reads memory only.
    /// </summary>
    /// <returns>The ids.</returns>
    IReadOnlyCollection<Guid> GetAttachedItemIds();

    /// <summary>
    /// Detaches the records still attached to any of the items (sets <c>DetachedUtc</c>, keeps <c>ItemId</c> and the
    /// stored snapshot, by which they are found again), under the store's lock, so a record relinked since the sweep looked
    /// is left alone. The sweep never passes an id it didn't find missing.
    /// </summary>
    /// <param name="itemIds">The ids the library no longer has.</param>
    /// <param name="detachedUtc">The time to record.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many records were detached.</returns>
    Task<int> DetachAsync(IReadOnlyCollection<Guid> itemIds, DateTime detachedUtc, CancellationToken cancellationToken);
}
