using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The administrator's tools for the saved queries: every user's queries, a new owner for a query whose owner was
/// deleted, export and import, the deleted-user tools, and the store's snapshots.
/// </summary>
public interface ISmartSearchSavedQueriesAdmin
{
    /// <summary>Lists every user's queries, or one user's, ordered by owner and name.</summary>
    /// <param name="userId">The owner to list, or <c>null</c> for all.</param>
    /// <param name="startIndex">The index of the page's first query.</param>
    /// <param name="limit">The page size, 1 to 500.</param>
    /// <returns>The page.</returns>
    SmartSearchSavedQueryListDto List(Guid? userId, int startIndex, int limit);

    /// <summary>
    /// Gives a query to another user (a query whose owner was deleted, typically): one auto-collections use only when
    /// forced (they move with it by reassigning them), never one with an administrator-only field to a user who isn't an
    /// administrator, never past the user's quota.
    /// </summary>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The new owner.</param>
    /// <param name="force">Whether to move a query auto-collections use alone; they then stop until they follow another query.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    Task SetOwnerAsync(Guid id, SmartSearchSavedQueryOwnerDto request, bool force, CancellationToken cancellationToken);

    /// <summary>Exports the saved queries as the kit's envelope (an attachment that isn't cached).</summary>
    /// <param name="context">The request.</param>
    /// <returns>The file.</returns>
    IActionResult Export(HttpContext context);

    /// <summary>
    /// Imports an export: a dry run by default. The report lists the auto-collection references the import would break
    /// (<c>BrokenAutoCollections</c>); a real run that would break any is refused unless forced.
    /// </summary>
    /// <param name="request">The request, whose body is the export.</param>
    /// <param name="mode"><c>Merge</c> (the default) or <c>Replace</c>.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="mapUsersBy"><c>Id</c> (the default) or <c>Name</c>.</param>
    /// <param name="force">Whether a real run goes ahead although it breaks references.</param>
    /// <returns>The report.</returns>
    Task<IDictionary<string, object?>> ImportAsync(HttpRequest request, string? mode, bool? dryRun, string? mapUsersBy, bool? force);

    /// <summary>Describes the users of the saved queries: unknown ids, pending policies, the marks.</summary>
    /// <returns>The description.</returns>
    IDictionary<string, object?> GetUsers();

    /// <summary><i>Remap by name</i>: unknown owner ids whose last seen name is one user's get that user.</summary>
    /// <param name="by">How to match: <c>Name</c>.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="force">Whether a real run goes ahead although it breaks references.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The report, with the references it would break and, in a dry run, why the real run would be refused.</returns>
    Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, bool? force, CancellationToken cancellationToken);

    /// <summary><i>Apply deleted-user policy</i> to unknown owner ids.</summary>
    /// <param name="request">The ids.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The report.</returns>
    Task<IDictionary<string, object?>> ApplyPolicyAsync(SmartSearchApplyPolicyDto request, CancellationToken cancellationToken);

    /// <summary>Lists the store's snapshots, newest first.</summary>
    /// <returns>The snapshots.</returns>
    IDictionary<string, object?> GetSnapshots();

    /// <summary>
    /// Restores a snapshot (after a pre-operation snapshot of what is there), or with a dry run only reports; a real run
    /// that would break auto-collection references is refused unless forced.
    /// </summary>
    /// <param name="name">The snapshot's name, as listed.</param>
    /// <param name="dryRun">Whether to only report; false when left out.</param>
    /// <param name="force">Whether a real run goes ahead although it breaks references.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What was, or would be, restored.</returns>
    Task<IDictionary<string, object?>> RestoreSnapshotAsync(string? name, bool? dryRun, bool? force, CancellationToken cancellationToken);
}
