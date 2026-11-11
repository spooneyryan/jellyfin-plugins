using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The roles and assignments API behind the admin controllers: roles, assignments, the
/// effective view, the preview, the libraries, and the library sync. Every write goes through the store with its
/// <c>IfUpdatedAt</c> check, is checked for the refusals of leaving management and the lockout guard inside the store's
/// writer, keeps the ledger
/// and is audited. Only the library sync writes users' policies (through <c>PolicyWriter</c>); nothing here blocks a
/// request (the settings filter does that).
/// </summary>
public interface IAccessRolesAdmin
{
    /// <summary>
    /// Lists the roles, by name.
    /// </summary>
    /// <returns>The roles.</returns>
    IReadOnlyList<AccessRolesRoleDto> GetRoles();

    /// <summary>
    /// Gets a role.
    /// </summary>
    /// <param name="roleId">The role's id.</param>
    /// <returns>The role (404 when there is none).</returns>
    AccessRolesRoleDto GetRole(Guid roleId);

    /// <summary>
    /// Creates a role, from a preset or from scratch.
    /// </summary>
    /// <param name="request">The role.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change.</returns>
    Task<AccessRolesChangeDto> CreateRoleAsync(AccessRolesRoleRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a role (rename, description, locks, libraries).
    /// </summary>
    /// <param name="roleId">The role's id.</param>
    /// <param name="request">The role, with <c>IfUpdatedAt</c>.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change.</returns>
    Task<AccessRolesChangeDto> UpdateRoleAsync(Guid roleId, AccessRolesRoleRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a role; its members lose it, or get another one.
    /// </summary>
    /// <param name="roleId">The role's id.</param>
    /// <param name="reassignTo">The role the members get instead, if any.</param>
    /// <param name="ifUpdatedAt">The role's <c>UpdatedUtc</c> as last read, if given.</param>
    /// <param name="ifMembers">The role's <c>MembersToken</c> as last read, if given.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change.</returns>
    Task<AccessRolesChangeDto> DeleteRoleAsync(Guid roleId, Guid? reassignTo, DateTime? ifUpdatedAt, string? ifMembers, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the roles new users get.
    /// </summary>
    /// <param name="request">The roles.</param>
    /// <param name="options">The dry run.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change.</returns>
    Task<AccessRolesChangeDto> SetDefaultRolesAsync(AccessRolesDefaultRolesRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every user of the server with their state, by name.
    /// </summary>
    /// <param name="roleId">Only users with this role, when set.</param>
    /// <param name="state">Only users in this state, when set.</param>
    /// <returns>The users.</returns>
    IReadOnlyList<AccessRolesUserDto> GetUsers(Guid? roleId, AccessRolesUserState? state);

    /// <summary>
    /// Replaces a user's assignment: roles, lock overrides, library mode and lists, state.
    /// </summary>
    /// <param name="userId">The user's id.</param>
    /// <param name="request">The assignment, with <c>IfUpdatedAt</c>.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change, with the user's effective access.</returns>
    Task<AccessRolesChangeDto> UpdateUserAsync(Guid userId, AccessRolesUserRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Gives roles to and takes roles from several users at once.
    /// </summary>
    /// <param name="request">The users and roles.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change.</returns>
    Task<AccessRolesChangeDto> BulkAsync(AccessRolesBulkRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a user out of management: the assignment goes, and the library access becomes what the choice says.
    /// </summary>
    /// <param name="userId">The user's id.</param>
    /// <param name="request">The choice.</param>
    /// <param name="options">The dry run and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change, with the user's effective access.</returns>
    Task<AccessRolesChangeDto> UnmanageAsync(Guid userId, AccessRolesUnmanageRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a user's effective access with its sources, optionally with other roles simulated.
    /// </summary>
    /// <param name="userId">The user's id.</param>
    /// <param name="simulateRoleIds">Roles to resolve the user with instead of theirs, when set.</param>
    /// <returns>The effective access (404 for a user the server doesn't have).</returns>
    AccessRolesEffectiveDto GetEffective(Guid userId, IReadOnlyList<Guid>? simulateRoleIds);

    /// <summary>
    /// Previews a proposed state against the stored one; nothing is saved.
    /// </summary>
    /// <param name="request">The proposal.</param>
    /// <returns>What it does per user, and the refusals saving it would meet.</returns>
    AccessRolesPreviewDto Preview(AccessRolesPreviewRequest request);

    /// <summary>
    /// Lists the libraries with the roles that grant them, and the ones the store refers to that are gone.
    /// </summary>
    /// <returns>The libraries, by name.</returns>
    IReadOnlyList<AccessRolesLibraryDto> GetLibraries();

    /// <summary>
    /// The admin's "grant it again" for a library deleted and created again under the same id (the Libraries tab's
    /// <i>Grant again</i>): its earlier grants (roles' <c>Only</c> lists, users' <c>Add</c>, the original access) give it again.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="options">The dry run and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change (404 for a library the server doesn't list, 409 for one that isn't re-created).</returns>
    Task<AccessRolesChangeDto> GrantLibraryAgainAsync(Guid libraryId, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Purges a deleted library's grants from the roles, the users' lists and the ledger (<c>Libraries/{id}/Purge</c>),
    /// and since 1.0.1 keeps its exclusions (<i>All except…</i> roles, users' <c>Remove</c>) and, while they name
    /// it, its known library (<see cref="Access.LibraryLifecycle.Purge"/>).
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="options">The dry run and lockout confirmation.</param>
    /// <param name="caller">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The change (409 for a library the server still lists or that only exclusions name, 404 when nothing refers
    /// to it).</returns>
    Task<AccessRolesChangeDto> PurgeLibraryAsync(Guid libraryId, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the library sync now, or only reports what it would do (<c>POST Sync</c>).
    /// </summary>
    /// <param name="options">The dry run, the lockout confirmation and the drift reapply.</param>
    /// <param name="caller">The admin or API key, for the audit lines.</param>
    /// <param name="cancellationToken">Cancels the run between writes.</param>
    /// <returns>What the run found and did (503 for a real run while the store is read-only).</returns>
    Task<AccessRolesSyncDto> SyncAsync(AccessRolesSyncOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Import current setup</i> (<c>ImportExisting</c>): one role per distinct library access
    /// among the non-admin users and the membership, so that nobody's access changes.
    /// </summary>
    /// <param name="options">The dry run.</param>
    /// <param name="manageUsersWithEveryLibrary">Whether users who see every library get an <c>All libraries</c> role; by
    /// default they are left unmanaged, with one warning.</param>
    /// <param name="caller">The admin or API key, for the audit lines.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The proposal, saved unless a dry run (409 <c>AlreadyManaging</c> once users are managed).</returns>
    Task<AccessRolesImportDto> ImportExistingAsync(AccessRolesWriteOptions options, bool manageUsersWithEveryLibrary, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Remap by name</i> (the kit's) of the store's unknown user ids, audited (<c>UsersRemapped</c>) as the caller's.
    /// </summary>
    /// <param name="by">How to match: <c>Name</c>, the only way.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="ifPlan">The dry run's <c>PlanToken</c>, which a remap that writes needs (400 without it, 409 <c>Changed</c>
    /// when the remap would now do anything else).</param>
    /// <param name="caller">The caller, for the audit line's actor.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The kit's report.</returns>
    Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, string? ifPlan, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Exports the data: roles, assignments with their overrides, default roles, known libraries, the
    /// ledger, the mirror's records and the settings, as the kit's envelope (<c>jellyfin-plugin-export/1</c>), an attachment
    /// that isn't cached.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The file.</returns>
    IActionResult ExportData(HttpContext context);

    /// <summary>
    /// Imports an export: <c>Merge</c> or <c>Replace</c> the roles, assignments and default roles, users by
    /// id or name, libraries by id, then folders, then name. A dry run (the default) reports what changes for each user; the
    /// save takes a snapshot first and is refused as any other change would be (the leaving-management choice,
    /// <c>NoLibraries</c>, <c>NoFullAdministrator</c>, 409 <c>Changed</c> against <c>ifPreview</c>).
    /// </summary>
    /// <param name="request">The request, whose body is the export.</param>
    /// <param name="mode"><c>Merge</c> (the default) or <c>Replace</c>.</param>
    /// <param name="mapUsersBy"><c>Id</c> (the default) or <c>Name</c>.</param>
    /// <param name="options">The dry run (true by default), its token and the choices.</param>
    /// <param name="caller">The admin or API key, for the audit lines.</param>
    /// <returns>The report.</returns>
    Task<AccessRolesDataImportDto> ImportDataAsync(HttpRequest request, string? mode, string? mapUsersBy, AccessRolesWriteOptions options, ClaimsPrincipal caller);

    /// <summary>
    /// Lists the data's snapshots, newest first: the daily ones and those taken before an operation.
    /// </summary>
    /// <returns>The list.</returns>
    IDictionary<string, object?> GetSnapshots();

    /// <summary>
    /// <i>Restore snapshot</i>: the roles, assignments and default roles become a snapshot's, as a
    /// <c>Replace</c> import of it, after a snapshot of the state it replaces; the library sync is then held until an admin
    /// has seen its dry run and confirmed it.
    /// </summary>
    /// <param name="name">The snapshot's name.</param>
    /// <param name="options">The dry run (true by default), its token and the choices.</param>
    /// <param name="caller">The admin or API key, for the audit lines.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The report.</returns>
    Task<AccessRolesDataImportDto> RestoreSnapshotAsync(string? name, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Queues the daily "Access Roles: Maintenance" task to run now (<c>Admin/Maintenance/Run</c>).
    /// </summary>
    /// <returns>What was queued.</returns>
    IDictionary<string, object?> RunMaintenance();

    /// <summary>
    /// <i>Apply deleted-user policy</i> (the kit's) to unknown user ids: their assignments and ledger entries go,
    /// audited (<c>UserDeleted</c>) as the caller's.
    /// </summary>
    /// <param name="request">The user ids.</param>
    /// <param name="caller">The caller, for the audit lines' actor.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The kit's report.</returns>
    Task<IDictionary<string, object?>> ApplyDeletedUserPolicyAsync(AccessRolesApplyPolicyRequest request, ClaimsPrincipal caller, CancellationToken cancellationToken);
}
