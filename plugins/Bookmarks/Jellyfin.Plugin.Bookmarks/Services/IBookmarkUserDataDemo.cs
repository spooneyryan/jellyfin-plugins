using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's users and data services behind
/// <see cref="Api.BookmarksUserDataDemoController"/>, turned on by the setting <c>EnableUserDataDemo</c>: records owned by
/// users, the deleted-user policy, the reconciler, <i>Remap by name</i>, export and import, and snapshots with <i>Restore
/// snapshot</i>. Public only so the public controller can take it; its results are plain dictionaries,
/// like the Diagnostics body. Every call answers 403 "turned off" while the setting is off.
/// </summary>
public interface IBookmarkUserDataDemo
{
    /// <summary>
    /// Lists the demo records, with their owners' names, and the store's known users.
    /// </summary>
    /// <returns>The records.</returns>
    IDictionary<string, object?> GetRecords();

    /// <summary>
    /// Adds a demo record for a user of the server: <c>{UserId, Text, HasFile}</c>.
    /// </summary>
    /// <param name="body">The record to add.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The record.</returns>
    Task<IDictionary<string, object?>> AddRecordAsync(JsonElement body, CancellationToken cancellationToken);

    /// <summary>
    /// Empties the demo store (its snapshots stay).
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task.</returns>
    Task ResetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs the kit's reconciler, as the maintenance task does.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The run.</returns>
    Task<IDictionary<string, object?>> ReconcileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Describes the users now: the policy, the unknown users and the banner, as Diagnostics shows them.
    /// </summary>
    /// <returns>The description.</returns>
    IDictionary<string, object?> GetUsers();

    /// <summary>
    /// <i>Remap by name</i> (<c>Users/Remap?by=Name&amp;dryRun=true</c>).
    /// </summary>
    /// <param name="by">Must be <c>Name</c>.</param>
    /// <param name="dryRun">Whether to only report; true when not given.</param>
    /// <param name="cancellationToken">Cancels the remap.</param>
    /// <returns>The report.</returns>
    Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Apply deleted-user policy</i> (<c>Users/ApplyPolicy</c>, <c>{UserIds:[…]}</c>).
    /// </summary>
    /// <param name="body">The ids.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The report.</returns>
    Task<IDictionary<string, object?>> ApplyPolicyAsync(JsonElement body, CancellationToken cancellationToken);

    /// <summary>
    /// The export envelope of the demo store (<c>GET Admin/Export</c>), as an attachment.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The file.</returns>
    IActionResult Export(HttpContext context);

    /// <summary>
    /// Imports an export into the demo store (<c>POST Admin/Import</c>).
    /// </summary>
    /// <param name="request">The request, whose body is the export.</param>
    /// <param name="mode"><c>Merge</c> (default) or <c>Replace</c>.</param>
    /// <param name="dryRun">Whether to only report; true when not given.</param>
    /// <param name="mapUsersBy"><c>Id</c> (default) or <c>Name</c>.</param>
    /// <returns>The report.</returns>
    Task<IDictionary<string, object?>> ImportAsync(HttpRequest request, string? mode, bool? dryRun, string? mapUsersBy);

    /// <summary>
    /// Lists the demo store's snapshots (<c>GET Admin/Snapshots</c>).
    /// </summary>
    /// <returns>The snapshots, newest first.</returns>
    IDictionary<string, object?> GetSnapshots();

    /// <summary>
    /// Takes a daily-class snapshot now, as the maintenance task will, keeping <paramref name="keep"/>.
    /// </summary>
    /// <param name="keep">How many daily snapshots to keep (1 to 100); 7 when not given.</param>
    /// <param name="cancellationToken">Cancels the snapshot.</param>
    /// <returns>The snapshot's name and the list.</returns>
    Task<IDictionary<string, object?>> TakeSnapshotAsync(int? keep, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Restore snapshot</i> (<c>POST Admin/Snapshots/Restore?name=</c>).
    /// </summary>
    /// <param name="name">A snapshot's name from <see cref="GetSnapshots"/>.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>What was restored.</returns>
    Task<IDictionary<string, object?>> RestoreSnapshotAsync(string? name, CancellationToken cancellationToken);
}
