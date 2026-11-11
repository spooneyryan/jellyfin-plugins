using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's record lifecycle and library reads behind
/// <see cref="Api.BookmarksLibraryDemoController"/>, turned on by the setting <c>EnableLibraryDemo</c>. Public only so the
/// public controller can take it; its results are plain dictionaries, like the Diagnostics body.
/// </summary>
/// <remarks>
/// A <c>userId</c> names the user whose view a read takes (the admin's own when omitted; an API key without one works over
/// the whole server), as an admin can read any user's data natively.
/// </remarks>
public interface IBookmarkLibraryDemo
{
    /// <summary>
    /// Describes the demo's records, live and archived, and the kit's verify sweep and detached records.
    /// </summary>
    /// <returns>The description.</returns>
    IDictionary<string, object?> DescribeRecords();

    /// <summary>
    /// Writes a record on a library item, for a user, with the item's identity snapshot.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="userId">The record's user, or null for the caller.</param>
    /// <param name="note">A free text.</param>
    /// <param name="withoutContainerIds">Leaves the snapshot's series, season and album ids out, as a record written
    /// before they existed (a 1.0 bookmark before the backfill), which only the verify sweep detaches.</param>
    /// <param name="caller">The signed-in admin, or null for an API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The record.</returns>
    Task<IDictionary<string, object?>> AddRecordAsync(Guid itemId, Guid? userId, string? note, bool withoutContainerIds, User? caller, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every live record (the archive stays; <see cref="PurgeAsync"/> empties it).
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    Task ClearRecordsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs the kit's verify sweep now (joining one that hasn't started).
    /// </summary>
    /// <param name="cancellationToken">Stops the waiting.</param>
    /// <returns>The sweep's result.</returns>
    Task<IDictionary<string, object?>> SweepAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-resolves the demo's detached records now.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done.</returns>
    Task<IDictionary<string, object?>> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The kit's <i>Purge</i> of the demo's detached records (the only hard delete, after a snapshot).
    /// </summary>
    /// <param name="olderThanDays">Only records detached at least this many days ago; null for all.</param>
    /// <param name="includeArchive">Whether archived records go too.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was deleted.</returns>
    Task<IDictionary<string, object?>> PurgeAsync(int? olderThanDays, bool includeArchive, CancellationToken cancellationToken);

    /// <summary>
    /// Exports the demo's data as the kit's export envelope, with the archived records in its <c>Archive</c>.
    /// </summary>
    /// <param name="context">The request, for the download headers.</param>
    /// <returns>The file.</returns>
    IActionResult Export(HttpContext context);

    /// <summary>
    /// Reads a user's data for an item: <c>Cached</c> through core's cached instance as <c>GetUserData</c> does (without
    /// changing it), or <c>Fresh</c> through the kit's <c>FreshUserData</c>, which also repairs the cached instance.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="userId">The user, or null for the caller.</param>
    /// <param name="read"><c>Fresh</c> (the default) or <c>Cached</c>.</param>
    /// <param name="path">For a fresh read: <c>Query</c> (the default) or <c>Repository</c>.</param>
    /// <param name="caller">The signed-in admin, or null for an API key.</param>
    /// <returns>The data and where it came from.</returns>
    IDictionary<string, object?> ReadUserData(Guid itemId, Guid? userId, string? read, string? path, User? caller);

    /// <summary>
    /// The kit's <c>LibraryAccess</c> for a user: the visible libraries, the requested ones resolved, the factory's query
    /// and what it finds (movies, and whether any is owned).
    /// </summary>
    /// <param name="userId">The user, or null for the caller.</param>
    /// <param name="libraryIds">Library or view ids to limit to.</param>
    /// <param name="caller">The signed-in admin, or null for an API key.</param>
    /// <returns>The description.</returns>
    IDictionary<string, object?> DescribeLibraries(Guid? userId, IReadOnlyList<Guid>? libraryIds, User? caller);

    /// <summary>
    /// The kit's <c>VersionSets</c> over items: their sets with the broken states, and the representatives a user's scoped
    /// search would show (<c>Collapse</c>, with <c>LibraryAccess</c>'s eligibility).
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <param name="userId">The user, or null for the caller.</param>
    /// <param name="caller">The signed-in admin, or null for an API key.</param>
    /// <returns>The sets and representatives.</returns>
    IDictionary<string, object?> DescribeVersionSets(IReadOnlyList<Guid> itemIds, Guid? userId, User? caller);
}
