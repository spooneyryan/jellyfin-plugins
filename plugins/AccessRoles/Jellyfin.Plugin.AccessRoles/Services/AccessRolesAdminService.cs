using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// Implements the roles API (<see cref="IAccessRolesAdmin"/>) and the audit log's reads (<see cref="IAccessRolesAudit"/>)
/// over the store, the library catalog, the server's users and the audit log.
/// </summary>
/// <remarks>
/// <para><b>Every write</b> runs its change inside the store's one writer (<see cref="AccessRolesStore.UpdateAsync"/>): the
/// <c>IfUpdatedAt</c> check, the request's checks against the stored records, then <see cref="ChangeAnalysis"/> with the
/// refusals of leaving management and the lockout guard (400 <c>UnmanageChoiceRequired</c>, 409 <c>NoLedgerEntry</c>,
/// 409 <c>NoLibraries</c>,
/// 409 <c>NoFullAdministrator</c>), the ledger kept, and the users' names. A dry run does the same on the stored document
/// and saves nothing. The audit lines follow a saved change; a change that changes nothing writes nothing.</para>
/// <para><b>Nothing here writes a user's policy or blocks a request</b>: the preview's <c>PolicyWrite</c> and the
/// ledger's pending releases say what the library sync will write; a save raises the store's change, which starts it
/// (since 0.9.3), and <see cref="SyncAsync"/> runs it at once.</para>
/// </remarks>
internal sealed partial class AccessRolesAdminService : IAccessRolesAdmin, IAccessRolesAudit
{
    /// <summary>The most users a bulk change takes.</summary>
    public const int MaxBulkUsers = 1000;

    /// <summary>The audit page size when the request gives none.</summary>
    public const int DefaultAuditLimit = 50;

    /// <summary>The largest audit page.</summary>
    public const int MaxAuditLimit = 200;

    /// <summary>What a saved write's answer says when its audit lines couldn't be written.</summary>
    internal const string AuditNotWritten = "The change is saved, but its audit lines couldn't be written: the server log has the details, and Diagnostics counts the audit log's write errors.";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AccessRolesStore _store;
    private readonly ILibraryCatalogSource _catalog;
    private readonly IServerUsers _users;
    private readonly AccessRolesAuditLog _audit;
    private readonly Func<UserReconciler> _reconciler;
    private readonly Func<LibrarySync> _sync;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly AccessRolesDataSupport? _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesAdminService"/> class.
    /// </summary>
    /// <param name="store">The roles and assignments store.</param>
    /// <param name="catalog">The library catalog.</param>
    /// <param name="users">The server's users.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="reconciler">The kit's user reconciler, for <i>Remap by name</i> and <i>Apply deleted-user policy</i>.</param>
    /// <param name="sync">The library sync, for <see cref="SyncAsync"/>.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="data">What the Data tab's actions need; without it they answer 503.</param>
    public AccessRolesAdminService(
        AccessRolesStore store,
        ILibraryCatalogSource catalog,
        IServerUsers users,
        AccessRolesAuditLog audit,
        Func<UserReconciler> reconciler,
        Func<LibrarySync> sync,
        ILogger logger,
        TimeProvider? timeProvider = null,
        AccessRolesDataSupport? data = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _catalog = catalog;
        _users = users;
        _audit = audit;
        _reconciler = reconciler;
        _sync = sync;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _data = data;
    }

    /// <inheritdoc />
    public IReadOnlyList<AccessRolesRoleDto> GetRoles()
    {
        var document = _store.Current;
        var catalog = _catalog.Read();
        var users = _users.GetAll();
        return [.. document.Roles.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).Select(r => AccessRolesMapper.Role(r, document, catalog, users))];
    }

    /// <inheritdoc />
    public AccessRolesRoleDto GetRole(Guid roleId)
    {
        var document = _store.Current;
        var role = FindRole(document, roleId);
        return AccessRolesMapper.Role(role, document, _catalog.Read(), _users.GetAll());
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> CreateRoleAsync(AccessRolesRoleRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireBody(request);
        if (request.Id is not null)
        {
            throw PluginException.Invalid("A new role gets its id from the server; leave Id out.");
        }

        var id = Guid.NewGuid();
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var role = AccessRolesRequests.BuildRole(request, null, id, context.Catalog, context.Now);
                AccessRolesRequests.EnsureUniqueName(context.Document.Roles, role);
                return new Proposal(context.Document with { Roles = [.. context.Document.Roles, role] }, "RoleCreated")
                {
                    Lines = report => [new AuditRecord(context.Actor, AuditActions.RoleCreated, null, id, role.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Preset"] = request.Preset?.ToString(),
                        ["Role"] = AccessRolesAuditDetails.Role(role)
                    })]
                };
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result, roleId: id);
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> UpdateRoleAsync(Guid roleId, AccessRolesRoleRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireBody(request);
        if (request.Id is { } bodyId && !bodyId.Equals(roleId))
        {
            throw PluginException.Invalid("The body's Id isn't the role of the route.");
        }

        if (request.IfUpdatedAt is not { } token)
        {
            throw PluginException.Invalid("IfUpdatedAt is required: the role's UpdatedUtc as you last read it.");
        }

        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var existing = FindRole(context.Document, roleId);
                if (!AccessRolesTokens.Matches(existing.UpdatedUtc, token))
                {
                    throw new AccessRolesException(PluginErrorKind.Conflict, AccessRolesException.Changed, "The role was changed since you read it; nothing was changed. Read it again.");
                }

                RequireMembers(context.Document, existing, request.IfMembers);
                var role = AccessRolesRequests.BuildRole(request, existing, roleId, context.Catalog, context.Now);
                AccessRolesRequests.EnsureUniqueName(context.Document.Roles, role);
                if (SameRole(existing, role))
                {
                    return new Proposal(context.Document, "RoleUpdated");
                }

                return new Proposal(context.Document with { Roles = [.. context.Document.Roles.Select(r => r.Id.Equals(roleId) ? role : r)] }, "RoleUpdated")
                {
                    Lines = report => [new AuditRecord(context.Actor, AuditActions.RoleUpdated, null, roleId, role.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Before"] = AccessRolesAuditDetails.Role(existing),
                        ["After"] = AccessRolesAuditDetails.Role(role),
                        ["Affected"] = AccessRolesAuditDetails.Affected(report, context.Catalog)
                    })]
                };
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result, roleId: roleId);
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> DeleteRoleAsync(Guid roleId, Guid? reassignTo, DateTime? ifUpdatedAt, string? ifMembers, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (reassignTo is { } same && same.Equals(roleId))
        {
            throw PluginException.Invalid("reassignTo must be another role.");
        }

        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var document = context.Document;
                var existing = FindRole(document, roleId);
                if (ifUpdatedAt is { } token && !AccessRolesTokens.Matches(existing.UpdatedUtc, token))
                {
                    throw new AccessRolesException(PluginErrorKind.Conflict, AccessRolesException.Changed, "The role was changed since you read it; nothing was changed. Read it again.");
                }

                // The members as the caller previewed them: a member added or removed, or a member's
                // assignment changed, since then is a 409, so a reassignment never moves a member nobody saw.
                RequireMembers(document, existing, ifMembers);

                if (document.DefaultRoleIds.Contains(roleId))
                {
                    throw new AccessRolesException(PluginErrorKind.Conflict, AccessRolesException.DefaultRole, "Role \"" + existing.Name + "\" is a default role of new users: choose other default roles first.");
                }

                AccessRole? target = null;
                if (reassignTo is { } other)
                {
                    target = document.Roles.FirstOrDefault(r => r.Id.Equals(other))
                        ?? throw PluginException.Invalid("Role " + AccessRolesAuditDetails.Id(other) + " doesn't exist.");
                }

                var members = document.Assignments.Count(a => a.RoleIds.Contains(roleId));
                var assignments = document.Assignments.Select(a =>
                {
                    if (!a.RoleIds.Contains(roleId))
                    {
                        return a;
                    }

                    var roles = a.RoleIds.Select(r => r.Equals(roleId) ? target?.Id ?? Guid.Empty : r).Where(r => !r.Equals(Guid.Empty)).Distinct().ToList();
                    return AccessRolesRequests.WithRoles(a, roles, context.Now);
                }).Where(a => a is not null).Select(a => a!).ToList();
                return new Proposal(document with { Roles = [.. document.Roles.Where(r => !r.Id.Equals(roleId))], Assignments = assignments }, "RoleDeleted")
                {
                    Lines = report => [new AuditRecord(context.Actor, AuditActions.RoleDeleted, null, roleId, existing.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Role"] = AccessRolesAuditDetails.Role(existing),
                        ["Members"] = members,
                        ["ReassignedTo"] = target is null ? null : AccessRolesAuditDetails.Id(target.Id),
                        ["ReassignedToName"] = target?.Name
                    })]
                };
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result);
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> SetDefaultRolesAsync(AccessRolesDefaultRolesRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireBody(request);
        var ids = (request.RoleIds ?? []).Distinct().ToList();
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var document = context.Document;
                foreach (var id in ids)
                {
                    _ = FindRole(document, id);
                }

                // The PreviewToken covers the default roles and their definitions too.
                var lines = AccessRolesTokens.DefaultRolesLines(document, ids);
                if (document.DefaultRoleIds.SequenceEqual(ids))
                {
                    return new Proposal(document, "DefaultRolesChanged") { PreviewLines = lines };
                }

                return new Proposal(document with { DefaultRoleIds = ids }, "DefaultRolesChanged")
                {
                    PreviewLines = lines,
                    Lines = report => [new AuditRecord(context.Actor, AuditActions.DefaultRolesChanged, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Before"] = AccessRolesAuditDetails.Ids(document.DefaultRoleIds),
                        ["After"] = AccessRolesAuditDetails.Ids(ids),
                        ["RoleNames"] = ids.ToDictionary(AccessRolesAuditDetails.Id, id => (object?)document.Roles.First(r => r.Id.Equals(id)).Name, StringComparer.Ordinal)
                    })]
                };
            },
            cancellationToken).ConfigureAwait(false);
        var answer = Answer(result);
        answer.DefaultRoles = [.. result.Saved.DefaultRoleIds.Select(id => result.Saved.Roles.FirstOrDefault(r => r.Id.Equals(id))).Where(r => r is not null).Select(r => new AccessRolesRoleRefDto { Id = r!.Id, Name = r.Name })];
        return answer;
    }

    /// <inheritdoc />
    public IReadOnlyList<AccessRolesUserDto> GetUsers(Guid? roleId, AccessRolesUserState? state)
    {
        // Resolved as the sync resolves: a pending rename's new library withheld, the lifecycle applied.
        var view = View();
        return [.. _users.GetAll()
            .Select(u => EffectiveRulesResolver.Resolve(view.Document, view.Catalog, u))
            .Where(e => roleId is not { } role || e.Assignment?.RoleIds.Contains(role) == true)
            .Where(e => state is not { } wanted || e.State == wanted)
            .Select(AccessRolesMapper.User)];
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> UpdateUserAsync(Guid userId, AccessRolesUserRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireBody(request);
        if (request.UserId is { } bodyId && !bodyId.Equals(userId))
        {
            throw PluginException.Invalid("The body's UserId isn't the user of the route.");
        }

        var user = FindUser(userId);
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var existing = EffectiveRulesResolver.FindAssignment(context.Document, userId);
                if (!AccessRolesTokens.MatchesOrNone(existing?.UpdatedUtc, request.IfUpdatedAt))
                {
                    throw new AccessRolesException(
                        PluginErrorKind.Conflict,
                        AccessRolesException.Changed,
                        "The user's assignment isn't the one IfUpdatedAt names (its UpdatedUtc, or none for a user without one); nothing was changed. Read it again.");
                }

                var assignment = AccessRolesRequests.BuildAssignment(request, userId, existing, context.Document, context.Catalog, context.Now);
                return ChangeAssignment(context.Document, userId, existing, assignment, "Edit");
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result, user: user);
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> BulkAsync(AccessRolesBulkRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireBody(request);
        var userIds = (request.UserIds ?? []).Distinct().ToList();
        if (userIds.Count is 0 or > MaxBulkUsers)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "UserIds must name 1 to {0} users.", MaxBulkUsers));
        }

        var add = (request.AddRoleIds ?? []).Distinct().ToList();
        var remove = (request.RemoveRoleIds ?? []).Distinct().ToList();
        if (add.Count == 0 && remove.Count == 0)
        {
            throw PluginException.Invalid("Name roles to add (AddRoleIds) or remove (RemoveRoleIds).");
        }

        if (add.Intersect(remove).Any())
        {
            throw PluginException.Invalid("A role can't be both added and removed.");
        }

        foreach (var id in userIds)
        {
            _ = FindUser(id);
        }

        var tokens = BulkTokens(request.IfUpdatedAt, userIds);
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var document = context.Document;
                _ = AccessRolesRequests.RoleIds([.. add, .. remove], document);

                // Each user as the caller previewed them: one changed since makes the whole request a
                // 409 naming them, and nothing is written.
                if (tokens is not null)
                {
                    var changedSince = userIds.Where(id => !AccessRolesTokens.MatchesOrNone(EffectiveRulesResolver.FindAssignment(document, id)?.UpdatedUtc, tokens[id])).Select(FindUser).ToList();
                    if (changedSince.Count > 0)
                    {
                        throw new AccessRolesException(
                            PluginErrorKind.Conflict,
                            AccessRolesException.Changed,
                            string.Join(", ", changedSince.Select(u => u.Name)) + (changedSince.Count == 1 ? " was" : " were") + " changed since you read them; nothing was changed. Read them again.",
                            [.. changedSince.Select(AccessRolesMapper.UserRef)]);
                    }
                }

                var assignments = document.Assignments.ToList();
                foreach (var userId in userIds)
                {
                    var index = assignments.FindIndex(a => a.UserId.Equals(userId));
                    var existing = index >= 0 ? assignments[index] : new RoleAssignment { UserId = userId };
                    var roles = existing.RoleIds.Where(r => !remove.Contains(r)).Concat(add.Where(r => !existing.RoleIds.Contains(r))).ToList();
                    if (roles.SequenceEqual(existing.RoleIds))
                    {
                        continue;
                    }

                    var changed = AccessRolesRequests.WithRoles(existing, roles, context.Now);
                    if (index >= 0)
                    {
                        if (changed is null)
                        {
                            assignments.RemoveAt(index);
                        }
                        else
                        {
                            assignments[index] = changed;
                        }
                    }
                    else if (changed is not null)
                    {
                        assignments.Add(changed);
                    }
                }

                return new Proposal(document with { Assignments = assignments }, "Bulk") { Choice = request.OnUnmanage };
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result);
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> UnmanageAsync(Guid userId, AccessRolesUnmanageRequest request, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireBody(request);
        if (request.Choice is not { } choice || !Enum.IsDefined(choice))
        {
            throw PluginException.Invalid("Choice is required: Restore, Keep or GrantAll.");
        }

        var user = FindUser(userId);
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var existing = EffectiveRulesResolver.FindAssignment(context.Document, userId);
                if (request.IfUpdatedAt is { } token && (existing is null || !AccessRolesTokens.Matches(existing.UpdatedUtc, token)))
                {
                    throw new AccessRolesException(
                        PluginErrorKind.Conflict,
                        AccessRolesException.Changed,
                        "The user's assignment was changed since you read it; nothing was changed. Read it again.",
                        [AccessRolesMapper.UserRef(user)]);
                }

                return ChangeAssignment(context.Document, userId, existing, null, "Unmanage") with { Choice = choice };
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result, user: user);
    }

    /// <inheritdoc />
    public AccessRolesEffectiveDto GetEffective(Guid userId, IReadOnlyList<Guid>? simulateRoleIds)
    {
        var user = FindUser(userId);

        // Resolved as the sync resolves: a pending rename's new library withheld, the lifecycle applied, so Effective
        // never shows as granted what the sync won't write.
        var view = View();
        var document = view.Document;
        var assignment = EffectiveRulesResolver.FindAssignment(document, userId);
        if (simulateRoleIds is not null)
        {
            var roles = AccessRolesRequests.RoleIds(simulateRoleIds, document);
            assignment = (assignment ?? new RoleAssignment { UserId = userId }) with { RoleIds = roles };
        }

        return AccessRolesMapper.Effective(EffectiveRulesResolver.Resolve(document, view.Catalog, user, assignment));
    }

    /// <inheritdoc />
    public AccessRolesPreviewDto Preview(AccessRolesPreviewRequest request)
    {
        RequireBody(request);

        // The proposal is built on the document as the sync would resolve it (the lifecycle applied), checked against
        // the libraries listed now, and judged against the sync's catalog (a pending rename's new library withheld).
        var catalog = _catalog.Read();
        var view = _sync().ResolutionView(_store.Current, catalog);
        var document = view.Document;
        var users = _users.GetAll();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var proposed = document;
        var deleted = (request.DeleteRoleIds ?? []).Distinct().ToList();

        foreach (var id in deleted)
        {
            _ = FindRole(proposed, id);
            proposed = proposed with
            {
                Roles = [.. proposed.Roles.Where(r => !r.Id.Equals(id))],
                Assignments = [.. proposed.Assignments.Select(a => a.RoleIds.Contains(id) ? AccessRolesRequests.WithRoles(a, [.. a.RoleIds.Where(r => !r.Equals(id))], now) : a).Where(a => a is not null).Select(a => a!)]
            };
        }

        foreach (var roleRequest in request.Roles ?? [])
        {
            RequireBody(roleRequest);
            var existing = roleRequest.Id is { } id ? FindRole(proposed, id) : null;
            var role = AccessRolesRequests.BuildRole(roleRequest, existing, existing?.Id ?? Guid.NewGuid(), catalog, now);
            AccessRolesRequests.EnsureUniqueName(proposed.Roles, role);
            proposed = proposed with { Roles = existing is null ? [.. proposed.Roles, role] : [.. proposed.Roles.Select(r => r.Id.Equals(role.Id) ? role : r)] };
        }

        foreach (var assignmentRequest in request.Assignments ?? [])
        {
            RequireBody(assignmentRequest);
            if (assignmentRequest.UserId is not { } userId)
            {
                throw PluginException.Invalid("Each proposed assignment needs its UserId.");
            }

            _ = FindUser(userId);
            var existing = EffectiveRulesResolver.FindAssignment(proposed, userId);
            var assignment = AccessRolesRequests.BuildAssignment(assignmentRequest, userId, existing, proposed, catalog, now);
            proposed = ChangeAssignment(proposed, userId, existing, assignment, "Preview").After;
        }

        // The default roles the proposal keeps (its own, else the stored ones): a role among them can't be deleted, as a
        // DELETE answers 409 DefaultRole; the preview lists it and goes on without it.
        var defaults = (request.DefaultRoleIds ?? document.DefaultRoleIds).Distinct().ToList();
        var defaultDeleted = defaults.Where(deleted.Contains).ToList();
        foreach (var id in request.DefaultRoleIds is null ? [] : defaults.Where(id => !deleted.Contains(id)))
        {
            _ = FindRole(proposed, id);
        }

        if (request.DefaultRoleIds is not null || defaultDeleted.Count > 0)
        {
            proposed = proposed with { DefaultRoleIds = [.. defaults.Where(id => !deleted.Contains(id))] };
        }

        var preview = AccessRolesMapper.Preview(ChangeAnalysis.Analyze(document, proposed, view.Catalog, users, request.OnUnmanage));
        preview.DefaultRole = [.. defaultDeleted.Select(id => document.Roles.First(r => r.Id.Equals(id))).Select(r => new AccessRolesRoleRefDto { Id = r.Id, Name = r.Name })];
        return preview;
    }

    /// <inheritdoc />
    public IReadOnlyList<AccessRolesLibraryDto> GetLibraries()
    {
        var document = _store.Current;
        var catalog = _catalog.Read();
        var users = _users.GetAll();

        // Who opens each library: the native access, what the server enforces now, next to what the
        // rules give, resolved as the sync resolves (the view Effective uses); and the overrides by user.
        var view = View();
        var sync = _sync();
        var pause = sync.PauseReason();
        var reportOnly = string.Equals(sync.CurrentDriftPolicy(), LibrarySync.ReportOnlyPolicy, StringComparison.Ordinal);
        var resolved = users.Select(u => (User: u, Effective: EffectiveRulesResolver.Resolve(view.Document, view.Catalog, u), Opens: catalog.Expand(u.Native.EnableAllFolders, u.Native.EnabledFolders).ToHashSet())).ToList();
        List<AccessRolesLibraryUserDto> Who(Guid libraryId, bool opens) =>
        [
            .. resolved.Select(r => (r.User, r.Effective, Opens: r.Opens.Contains(libraryId), Given: r.Effective.Libraries.Visible.FirstOrDefault(l => l.Library.Id.Equals(libraryId))))
                .Where(r => opens ? r.Opens : r.Effective.Libraries.Writes && r.Given is not null && !r.Opens)
                .OrderBy(r => r.User.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new AccessRolesLibraryUserDto
                {
                    UserId = r.User.Id,
                    Name = r.User.Name,
                    IsAdministrator = r.User.IsAdministrator,
                    IsDisabled = r.User.IsDisabled,
                    Sources = r.Given?.Sources ?? [],
                    Mismatch = !r.Effective.Libraries.Writes || r.Opens == (r.Given is not null) ? null : LibraryMismatch(r.Effective, pause, reportOnly)
                })
        ];
        List<AccessRolesUserRefDto> Overriding(Guid libraryId, Func<AssignmentLibraries, IReadOnlyList<Guid>> list) =>
        [
            .. document.Assignments.Where(a => list(a.Libraries).Contains(libraryId))
                .Select(a => users.FirstOrDefault(u => u.Id.Equals(a.UserId)))
                .Where(u => u is not null)
                .OrderBy(u => u!.Name, StringComparer.OrdinalIgnoreCase)
                .Select(u => AccessRolesMapper.UserRef(u!))
        ];
        var rows = catalog.Libraries.Select(library =>
        {
            var roles = document.Roles.Where(r => EffectiveRulesResolver.Grants(document, catalog, r, library.Id)).Select(r => r.Id).ToList();
            return new AccessRolesLibraryDto
            {
                Users = Who(library.Id, opens: true),
                NotOpenedBy = Who(library.Id, opens: false),
                AddedFor = Overriding(library.Id, l => l.Add),
                RemovedFor = Overriding(library.Id, l => l.Remove),
                Id = library.Id,
                Name = library.Name,
                Enabled = library.Enabled,
                State = AccessRolesLibraryState.Active,
                CreatedUtc = library.CreatedUtc,
                CollectionType = library.CollectionType,
                RoleIds = roles,
                InNoRole = roles.Count == 0,
                Locations = library.Locations,
                Recreated = EffectiveRulesResolver.IsRecreated(document, catalog, library.Id),
                Known = document.KnownLibraries.Any(k => k.Id.Equals(library.Id))
            };
        }).ToList();

        // What the roles, the overrides and the catalog's own record still refer to that the server no longer lists.
        var gone = document.Roles.SelectMany(r => r.Libraries.Refs.Select(x => x.Id))
            .Concat(document.Assignments.SelectMany(a => a.Libraries.Add.Concat(a.Libraries.Remove)))
            .Concat(document.KnownLibraries.Where(k => k.State.Is(AccessRolesLibraryState.Orphaned)).Select(k => k.Id))
            .Distinct()
            .Where(id => !catalog.Contains(id));
        foreach (var id in gone)
        {
            var reference = document.Roles.SelectMany(r => r.Libraries.Refs).FirstOrDefault(r => r.Id.Equals(id));
            var known = document.KnownLibraries.FirstOrDefault(k => k.Id.Equals(id));
            rows.Add(new AccessRolesLibraryDto
            {
                Id = id,
                Name = reference?.NameAtSelection ?? known?.Name ?? AccessRolesAuditDetails.Id(id),
                Enabled = false,
                State = AccessRolesLibraryState.Orphaned,
                CreatedUtc = known?.CreatedUtc,
                RoleIds = [.. document.Roles.Where(r => r.Libraries.Refs.Any(x => x.Id.Equals(id))).Select(r => r.Id)],
                InNoRole = !document.Roles.Any(r => r.Libraries.Refs.Any(x => x.Id.Equals(id))),
                Locations = reference?.Locations ?? known?.Locations ?? [],
                Known = known is not null,
                AddedFor = Overriding(id, l => l.Add),
                RemovedFor = Overriding(id, l => l.Remove),
                Purgeable = LibraryLifecycle.Purge(document, id) is not null
            });
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> GrantLibraryAgainAsync(Guid libraryId, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var library = context.Catalog.Find(libraryId)
                    ?? throw PluginException.NotFound("The server lists no library " + AccessRolesAuditDetails.Id(libraryId) + ".");
                var granted = LibraryLifecycle.GrantAgain(context.Document, context.Catalog, libraryId, context.Now)
                    ?? throw PluginException.Conflict("Library \"" + library.Name + "\" wasn't deleted and created again, and no reference to it is orphaned: there is nothing to grant again.");
                return new Proposal(granted, "LibraryGrantedAgain")
                {
                    Lines = report => [new AuditRecord(context.Actor, AuditActions.LibraryGrantedAgain, null, null, library.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["LibraryId"] = AccessRolesAuditDetails.Id(libraryId),
                        ["CreatedUtc"] = library.CreatedUtc,
                        ["Roles"] = AccessRolesAuditDetails.Ids(context.Document.Roles.Where(r => r.Libraries.Refs.Any(x => x.Id.Equals(libraryId))).Select(r => r.Id)),
                        ["Affected"] = AccessRolesAuditDetails.Affected(report, context.Catalog)
                    })]
                };
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result);
    }

    /// <inheritdoc />
    public async Task<AccessRolesChangeDto> PurgeLibraryAsync(Guid libraryId, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                if (context.Catalog.Find(libraryId) is { } listed)
                {
                    throw PluginException.Conflict("Library \"" + listed.Name + "\" still exists: only a deleted library's references can be purged.");
                }

                var document = context.Document;
                var name = document.KnownLibraries.FirstOrDefault(k => k.Id.Equals(libraryId))?.Name
                    ?? document.Roles.SelectMany(r => r.Libraries.Refs).FirstOrDefault(x => x.Id.Equals(libraryId))?.NameAtSelection;
                // Its grants go and its exclusions stay (1.0.1): a library only exclusions name has nothing to purge.
                var purged = LibraryLifecycle.Purge(document, libraryId)
                    ?? throw (LibraryLifecycle.IsExcluded(document, libraryId)
                        ? PluginException.Conflict("Only exclusions refer to library " + (name is null ? AccessRolesAuditDetails.Id(libraryId) : "\"" + name + "\"") + " now (an All except… role or a user's own Remove list): they keep leaving it out, so a library created again under its id stays left out of them, also after Grant again. To drop one, take the library out of that role or out of that user's Remove list.")
                        : PluginException.NotFound("Nothing refers to library " + AccessRolesAuditDetails.Id(libraryId) + "."));
                return new Proposal(purged, "LibraryPurged")
                {
                    Lines = report => [new AuditRecord(context.Actor, AuditActions.LibraryPurged, null, null, name, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["LibraryId"] = AccessRolesAuditDetails.Id(libraryId),
                        ["RolesChanged"] = AccessRolesAuditDetails.Ids(document.Roles.Where(r => !purged.Roles.Any(p => ReferenceEquals(p, r))).Select(r => r.Id)),
                        ["UsersChanged"] = AccessRolesAuditDetails.Ids(document.Assignments.Where(a => a.Libraries.Add.Contains(libraryId)).Select(a => a.UserId)),
                        ["LedgerEntriesChanged"] = document.Ledger.Count(e => e.Value.OriginalEnabledFolders.Contains(libraryId)),
                        ["ExclusionsKept"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["Roles"] = AccessRolesAuditDetails.Ids(purged.Roles.Where(r => r.Libraries.Refs.Any(x => x.Id.Equals(libraryId))).Select(r => r.Id)),
                            ["Users"] = AccessRolesAuditDetails.Ids(purged.Assignments.Where(a => a.Libraries.Remove.Contains(libraryId)).Select(a => a.UserId))
                        }
                    })]
                };
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result);
    }

    /// <inheritdoc />
    public async Task<AccessRolesSyncDto> SyncAsync(AccessRolesSyncOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.DryRun)
        {
            _store.Documents.ThrowIfReadOnly();
        }

        var sync = _sync();

        // A run for one user (the per-user Revert): a user the server has, and never the restore's confirmation,
        // which is for everyone.
        if (options.UserId is { } userId)
        {
            FindUser(userId);
            if (options.ConfirmRestore)
            {
                throw PluginException.Invalid("confirmRestore confirms the library sync for everyone: leave userId out.");
            }
        }

        // The hold after a restore lifts only once the admin has seen the sync's dry run.
        if (options.ConfirmRestore && !options.DryRun && sync.RestoreNeedsDryRun())
        {
            throw new AccessRolesException(
                PluginErrorKind.Conflict,
                AccessRolesException.DryRunRequired,
                "Look at what the library sync would write first: POST /AccessRoles/Admin/Sync?dryRun=true, then confirm with confirmRestore=true and its PlanToken as ifPlan. Nothing was changed, and the sync stays paused.");
        }

        // The confirmation is of the plan the admin saw (since 0.9.16): while held, ifPlan is required, so a hold another
        // restore began since that dry run (whose own dry run is then required) or another plan is never confirmed by it.
        if (options.ConfirmRestore && !options.DryRun && string.IsNullOrWhiteSpace(options.IfPlan) && sync.RestoreHeld())
        {
            throw new AccessRolesException(
                PluginErrorKind.Invalid,
                AccessRolesException.IfPlanRequired,
                "confirmRestore=true needs ifPlan: the PlanToken of the sync's dry run you looked at (POST /AccessRoles/Admin/Sync?dryRun=true). Nothing was changed, and the sync stays paused.");
        }

        var report = await sync.RunAsync(
            new SyncRequest(SyncTrigger.Manual, Actor(caller)) { DryRun = options.DryRun, ConfirmNoFullAdmin = options.ConfirmNoFullAdmin, ReapplyDrift = options.ReapplyDrift, ConfirmRestore = options.ConfirmRestore, IfPlan = options.IfPlan, UserId = options.UserId },
            cancellationToken).ConfigureAwait(false);
        return AccessRolesMapper.Sync(report, sync.CurrentDriftPolicy(), _catalog.Read());
    }

    /// <inheritdoc />
    public async Task<AccessRolesImportDto> ImportExistingAsync(AccessRolesWriteOptions options, bool manageUsersWithEveryLibrary, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var users = _users.GetAll();
        ImportPlan? plan = null;
        var result = await WriteAsync(
            options,
            caller,
            context =>
            {
                var document = context.Document;
                if (document.Assignments.Count > 0)
                {
                    throw new AccessRolesException(
                        PluginErrorKind.Conflict,
                        AccessRolesException.AlreadyManaging,
                        "Access Roles already manages users, so there is nothing to import: Import current setup is for the first run.",
                        [.. document.Assignments.Select(a => users.FirstOrDefault(u => u.Id.Equals(a.UserId))).Where(u => u is not null).Select(u => AccessRolesMapper.UserRef(u!))]);
                }

                plan = AccessRolesImport.Plan(document, context.Catalog, d => RulesCatalog(d, context.Catalog), users, context.Now, manageUsersWithEveryLibrary);
                var imported = plan;
                return new Proposal(AccessRolesImport.With(document, imported.Roles), "Import")
                {
                    Lines = report =>
                    [
                        .. imported.Roles.Select(r => new AuditRecord(context.Actor, AuditActions.RoleCreated, null, r.Role.Id, r.Role.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["Preset"] = null,
                            ["Imported"] = true,
                            ["Role"] = AccessRolesAuditDetails.Role(r.Role)
                        })),
                        new AuditRecord(context.Actor, AuditActions.ImportApplied, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["Roles"] = imported.Roles.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["RoleId"] = AccessRolesAuditDetails.Id(r.Role.Id),
                                ["Name"] = r.Role.Name,
                                ["Members"] = r.Members.Select(m => new Dictionary<string, object?>(StringComparer.Ordinal) { ["UserId"] = AccessRolesAuditDetails.Id(m.Id), ["Name"] = m.Name }).ToList()
                            }).ToList(),
                            ["Users"] = imported.Roles.Sum(r => r.Members.Count),
                            ["NoChange"] = report.Users.Count(c => c.AssignmentChanged && c.LibrariesAdded.Count == 0 && c.LibrariesRemoved.Count == 0 && !c.PolicyWrite),
                            ["ManageUsersWithEveryLibrary"] = manageUsersWithEveryLibrary,
                            ["Warnings"] = imported.Warnings.Select(w => new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["Code"] = w.Code,
                                ["Users"] = w.Users.Select(u => new Dictionary<string, object?>(StringComparer.Ordinal) { ["UserId"] = AccessRolesAuditDetails.Id(u.UserId), ["Name"] = u.Name }).ToList(),
                                ["Imported"] = w.Imported
                            }).ToList()
                        })
                    ]
                };
            },
            cancellationToken).ConfigureAwait(false);

        var changes = AccessRolesMapper.Preview(result.Report).Users.Where(u => plan!.Roles.Any(r => r.Members.Any(m => m.Id.Equals(u.UserId)))).ToList();
        return new AccessRolesImportDto
        {
            DryRun = result.DryRun,
            ProposedRoles = [.. plan!.Roles.Select(r => new AccessRolesImportRoleDto
            {
                Id = result.DryRun ? null : r.Role.Id,
                Name = r.Role.Name,
                Mode = r.Role.Libraries.Mode.Value ?? AccessRolesRoleLibraryMode.NotManaged,
                Libraries = [.. r.Role.Libraries.Refs.Select(x => new AccessRolesLibraryNameDto { Id = x.Id, Name = x.NameAtSelection ?? AccessRolesAuditDetails.Id(x.Id) })],
                Members = [.. r.Members.Select(AccessRolesMapper.UserRef)]
            })],
            Assignments = [.. plan.Roles.SelectMany(r => r.Members.Select(m => new AccessRolesImportAssignmentDto { UserId = m.Id, Name = m.Name, RoleId = result.DryRun ? null : r.Role.Id, RoleName = r.Role.Name })).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)],
            EffectiveChanges = changes,
            NoChange = changes.Count(c => c.LibrariesAdded.Count == 0 && c.LibrariesRemoved.Count == 0 && !c.PolicyWrite),
            Warnings = plan.Warnings,
            AuditSeq = result.AuditSeq,
            PreviewToken = AccessRolesTokens.Preview(result.Report)
        };
    }

    /// <summary>
    /// Gives a new user the default roles ("Default roles for new users", one of the library sync's triggers): called by the
    /// user-created consumer while Jellyfin creates the user (the stock <i>Add user</i> page, the API, an LDAP or SSO
    /// plugin), so the page's own policy post right after it is pre-merged. Saved as the plugin's change (actor
    /// <c>System</c>), with the ledger entry marked <see cref="LedgerEntry.DefaultRolesUtc"/> in the same save, audited
    /// <c>AssignmentChanged</c> and <c>UserCreatedDefaulted</c>. Never refused: a new user has nothing to keep (the sync
    /// writes an empty result for them, fail closed). The save raises no store change: the consumer asks
    /// for the runs once the creating request is done, and a store change's run 2 s later would write the user inside a
    /// request that takes longer. Nothing is done without default roles, for a user who already has an assignment, or
    /// while the store is read-only.
    /// </summary>
    /// <param name="userId">The new user.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns><c>true</c> when the default roles were given.</returns>
    internal async Task<bool> GiveDefaultRolesAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_store.IsReadOnly)
        {
            if (_store.Current.DefaultRoleIds.Count > 0)
            {
                _logger.LogWarning("Access Roles couldn't give the new user {UserId} the default roles: its data file is read-only", userId);
            }

            return false;
        }

        if (_store.Current.DefaultRoleIds.Count == 0 || EffectiveRulesResolver.FindAssignment(_store.Current, userId) is not null || _users.Find(userId) is not { } user)
        {
            return false;
        }

        var result = await WriteAsync(
            new AccessRolesWriteOptions(),
            AuditActor.System,
            context =>
            {
                var document = context.Document;
                var roles = document.DefaultRoleIds.Distinct().Where(id => document.Roles.Any(r => r.Id.Equals(id))).ToList();
                if (roles.Count == 0 || EffectiveRulesResolver.FindAssignment(document, userId) is not null)
                {
                    return new Proposal(document, "UserCreated");
                }

                var assignment = new RoleAssignment { UserId = userId, RoleIds = roles, UpdatedUtc = context.Now };
                return ChangeAssignment(document, userId, null, assignment, "UserCreated") with
                {
                    // The mark that the user's access is the server's default for a new user, nothing an admin gave them:
                    // until the first write, the sync's no-libraries rule writes an empty result; and their original access
                    // is none, so Restore never widens (LedgerEntry.DefaultRolesUtc).
                    Finish = d => d.Ledger.TryGetValue(userId, out var entry) && entry.DefaultRolesUtc is null && entry.LastWrite is null
                        ? d with { Ledger = new Dictionary<Guid, LedgerEntry>(d.Ledger) { [userId] = entry with { DefaultRolesUtc = context.Now } } }
                        : d,
                    Lines = report =>
                    {
                        var change = report.Users.FirstOrDefault(c => c.User.Id.Equals(userId));
                        var libraries = change?.After.Libraries;
                        return [new AuditRecord(context.Actor, AuditActions.UserCreatedDefaulted, userId, null, user.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["RoleIds"] = AccessRolesAuditDetails.Ids(roles),
                            ["RoleNames"] = roles.ToDictionary(AccessRolesAuditDetails.Id, id => (object?)document.Roles.First(r => r.Id.Equals(id)).Name, StringComparer.Ordinal),
                            ["MissingRoleIds"] = AccessRolesAuditDetails.Ids(document.DefaultRoleIds.Where(id => !roles.Contains(id))),
                            ["EnableAllFolders"] = libraries?.EnableAllFolders,
                            ["EnabledFolders"] = AccessRolesAuditDetails.Ids(libraries?.EnabledFolders ?? []),
                            ["LibraryNames"] = (libraries?.EnabledFolders ?? []).ToDictionary(AccessRolesAuditDetails.Id, id => (object?)context.Catalog.Find(id)?.Name, StringComparer.Ordinal),
                            ["NoLibraries"] = change?.NoLibrariesRefused ?? false
                        })];
                    }
                };
            },
            refuse: false,
            cancellationToken,
            notify: false).ConfigureAwait(false);
        if (!result.Committed)
        {
            return false;
        }

        _logger.LogInformation("Access Roles gave the new user {UserId} the default roles", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ApplyDeletedUserPolicyAsync(AccessRolesApplyPolicyRequest request, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (request?.UserIds is not { } ids)
        {
            throw PluginException.Invalid("The body must be {UserIds: [...]}.");
        }

        // The kit's reconciler calls the store's user-data owner, whose UserDeleted lines name this caller.
        using (AuditActorScope.Begin(Actor(caller)))
        {
            return (await _reconciler().ApplyPolicyAsync([.. ids], cancellationToken).ConfigureAwait(false)).ToDictionary();
        }
    }

    /// <inheritdoc />
    public QueryResult<AccessRolesAuditEntryDto> Query(int? startIndex, int? limit, Guid? userId, string? action, DateTime? fromUtc, DateTime? toUtc = null, string? rule = null, long? before = null)
    {
        var start = startIndex ?? 0;
        var size = limit ?? DefaultAuditLimit;
        if (start < 0)
        {
            throw PluginException.Invalid("startIndex can't be negative.");
        }

        if (size is < 1 or > MaxAuditLimit)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "limit must be 1 to {0}.", MaxAuditLimit));
        }

        if (before is < 0)
        {
            throw PluginException.Invalid("before can't be negative: it is the Seq of the last line shown.");
        }

        var (since, until) = AuditRange(fromUtc, toUtc);
        var page = _audit.Read(start, size, userId, string.IsNullOrWhiteSpace(action) ? null : action.Trim(), since, until, string.IsNullOrWhiteSpace(rule) ? null : rule.Trim(), before);
        return new QueryResult<AccessRolesAuditEntryDto>(page.StartIndex, page.TotalRecordCount, [.. page.Items.Select(AccessRolesMapper.Audit)]);
    }

    /// <inheritdoc />
    public IActionResult Export(HttpContext context, DateTime? fromUtc, DateTime? toUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (since, until) = AuditRange(fromUtc, toUtc);
        var text = new StringBuilder();
        foreach (var line in _audit.ReadLines(since, until))
        {
            text.Append(line).Append('\n');
        }

        context.Response.Headers.CacheControl = "no-store";
        var stamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new FileContentResult(_utf8.GetBytes(text.ToString()), "application/x-ndjson") { FileDownloadName = "accessroles-audit-" + stamp + ".jsonl" };
    }

    private static bool SameRole(AccessRole a, AccessRole b)
        => string.Equals(
            JsonSerializer.Serialize(a with { UpdatedUtc = default }, JsonStoreFormat.Options),
            JsonSerializer.Serialize(b with { UpdatedUtc = default }, JsonStoreFormat.Options),
            StringComparison.Ordinal);

    // The audit's time range in UTC: a time with a zone converted, one without read as UTC as the parameters' names say
    // (MVC binds "2026-10-31T12:00:00" with no kind, which ToUniversalTime read as the server's local time).
    private static (DateTime? Since, DateTime? Until) AuditRange(DateTime? fromUtc, DateTime? toUtc)
    {
        DateTime? since = fromUtc is { } from ? StoreValues.Utc(from) : null;
        DateTime? until = toUtc is { } to ? StoreValues.Utc(to) : null;
        if (since > until)
        {
            throw PluginException.Invalid("fromUtc must be before toUtc.");
        }

        return (since, until);
    }

    private static AccessRole FindRole(AccessRolesDocument document, Guid roleId)
        => document.Roles.FirstOrDefault(r => r.Id.Equals(roleId))
            ?? throw PluginException.NotFound("Role " + AccessRolesAuditDetails.Id(roleId) + " doesn't exist.");

    private static void RequireBody(object? body)
    {
        if (body is null)
        {
            throw PluginException.Invalid("The request needs a body.");
        }
    }

    // Users/Bulk's IfUpdatedAt by user: every user of the request once, or no list at all.
    private static Dictionary<Guid, DateTime?>? BulkTokens(IReadOnlyList<AccessRolesUserTokenRequest>? given, List<Guid> userIds)
    {
        if (given is null)
        {
            return null;
        }

        const string Shape = "IfUpdatedAt must name each user of UserIds once: {UserId, UpdatedUtc}, UpdatedUtc null for a user without an assignment.";
        var tokens = new Dictionary<Guid, DateTime?>();
        foreach (var entry in given)
        {
            if (entry is null || !userIds.Contains(entry.UserId) || !tokens.TryAdd(entry.UserId, entry.UpdatedUtc))
            {
                throw PluginException.Invalid(Shape);
            }
        }

        if (tokens.Count != userIds.Count)
        {
            throw PluginException.Invalid(Shape);
        }

        return tokens;
    }

    private static Proposal ChangeAssignment(AccessRolesDocument document, Guid userId, RoleAssignment? existing, RoleAssignment? assignment, string reason)
    {
        if (ChangeAnalysis.SameAssignment(existing, assignment))
        {
            return new Proposal(document, reason);
        }

        var others = document.Assignments.Where(a => !a.UserId.Equals(userId));
        return new Proposal(document with { Assignments = assignment is null ? [.. others] : [.. others, assignment] }, reason);
    }

    private static void Refuse(ChangeReport report, AccessRolesWriteOptions options)
    {
        if (report.ChoiceRequired is { Count: > 0 } leaving)
        {
            throw new AccessRolesException(
                PluginErrorKind.Invalid,
                AccessRolesException.UnmanageChoiceRequired,
                Users(leaving) + (leaving.Count == 1 ? " is" : " are") + " no longer managed for libraries after this change: choose what their library access becomes with onUnmanage=Restore, Keep or GrantAll.",
                Refs(leaving));
        }

        if (report.NoLedgerEntry is { Count: > 0 } unrecorded)
        {
            throw new AccessRolesException(
                PluginErrorKind.Conflict,
                AccessRolesException.NoLedgerEntry,
                "Restore needs the original library access, which Access Roles never recorded for " + Users(unrecorded) + ": choose Keep or GrantAll.",
                Refs(unrecorded));
        }

        if (report.NoLibraries is { Count: > 0 } empty)
        {
            throw new AccessRolesException(
                PluginErrorKind.Conflict,
                AccessRolesException.NoLibraries,
                Users(empty) + " would have no libraries. To give a user no libraries on purpose, set their library mode to Only with nothing selected and confirm it (ConfirmNoLibraries).",
                Refs(empty));
        }

        if (report.NoFullAdministrator && !options.ConfirmNoFullAdmin)
        {
            throw new AccessRolesException(
                PluginErrorKind.Conflict,
                AccessRolesException.NoFullAdministrator,
                "After this change no enabled administrator would have access to every library. Confirm it with confirmNoFullAdmin=true.");
        }
    }

    private static string Users(IReadOnlyList<UserChange> changes) => string.Join(", ", changes.Select(c => c.User.Name));

    private static List<AccessRolesUserRefDto> Refs(IReadOnlyList<UserChange> changes) => [.. changes.Select(c => AccessRolesMapper.UserRef(c.User))];

    private static AccessRolesDocument KeepKnownUsers(AccessRolesDocument document, ChangeReport report)
    {
        var known = document.KnownUsers;
        foreach (var change in report.Users)
        {
            var id = change.User.Id;
            known = document.Assignments.Any(a => a.UserId.Equals(id)) || document.Ledger.ContainsKey(id)
                ? known.With(id, change.User.Name)
                : known.Without(id);
        }

        return ReferenceEquals(known, document.KnownUsers) ? document : document with { KnownUsers = known };
    }

    // A save's effect as its dry run showed it (ifPreview): another one writes nothing, and the
    // 409 names the users whose result differs (when the dry run's lines are remembered; otherwise nobody).
    private static void RequirePreview(ChangeReport report, string given)
    {
        var lines = AccessRolesTokens.PreviewLines(report);
        if (AccessRolesTokens.Same(AccessRolesTokens.Of(lines), given))
        {
            return;
        }

        var differing = AccessRolesTokens.Differing(given, lines) ?? new HashSet<string>(StringComparer.Ordinal);
        var named = report.Users.Where(c => differing.Contains(AccessRolesTokens.Key(c.User.Id))).ToList();
        throw new AccessRolesException(
            PluginErrorKind.Conflict,
            AccessRolesException.Changed,
            "What this change does isn't what its preview showed any more"
                + (named.Count > 0 ? " (for " + Users(named) + ")" : string.Empty)
                + ": a role, a library or a user it concerns changed since. Nothing was changed. Preview it again.",
            Refs(named));
    }

    // A role's members as the caller read them (MembersToken), when the request names them. The 409 names
    // the members added, removed or changed since, or every member now when the token's lines aren't
    // remembered.
    private void RequireMembers(AccessRolesDocument document, AccessRole role, string? given)
    {
        if (given is null)
        {
            return;
        }

        var lines = AccessRolesTokens.MembersLines(document, role.Id);
        if (AccessRolesTokens.Same(AccessRolesTokens.Of(lines), given))
        {
            return;
        }

        var differing = AccessRolesTokens.Differing(given, lines);
        var named = differing is null
            ? document.Assignments.Where(a => a.RoleIds.Contains(role.Id)).Select(a => a.UserId)
            : differing.Select(key => Guid.TryParseExact(key, "N", out var id) ? id : Guid.Empty).Where(id => !id.Equals(Guid.Empty));
        throw new AccessRolesException(
            PluginErrorKind.Conflict,
            AccessRolesException.Changed,
            "The members of role \"" + role.Name + "\", or their assignments, changed since you read them; nothing was changed. Read the role again.",
            [.. named.Select(id => _users.Find(id)).Where(u => u is not null).Select(u => AccessRolesMapper.UserRef(u!)).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private ServerUser FindUser(Guid userId)
        => (userId.Equals(Guid.Empty) ? null : _users.Find(userId))
            ?? throw PluginException.NotFound("The server has no user " + AccessRolesAuditDetails.Id(userId) + ".");

    private AuditActor Actor(ClaimsPrincipal caller) => AuditActor.Of(caller, id => _users.Find(id)?.Name);

    // The document and catalog the sync resolves with now (LibrarySync.ResolutionView): the previews use them, so they show
    // no grant the sync won't write.
    // Why a managed user's native access to a library isn't what the rules give (GET Libraries):
    // a result of no library the sync skips (never Drift), a drift ReportOnly keeps, the sync's pause, or a write
    // the next run makes.
    private static string LibraryMismatch(EffectiveAccess effective, string? pause, bool reportOnly)
        => effective.Libraries.NoLibraries && !effective.Libraries.NoLibrariesConfirmed ? SyncReasons.NoLibraries
            : effective.Drift && reportOnly ? SyncReasons.Drift
            : pause ?? "Pending";

    private LifecycleResult View() => _sync().ResolutionView();

    // The catalog the sync resolves a document with: the listed libraries, plus the ones a rename waits for, without the
    // withheld ones.
    private LibraryCatalog RulesCatalog(AccessRolesDocument document, LibraryCatalog listed) => _sync().ResolutionView(document, listed).Catalog;

    private Task<WriteResult> WriteAsync(AccessRolesWriteOptions options, ClaimsPrincipal caller, Func<WriteContext, Proposal> propose, CancellationToken cancellationToken)
        => WriteAsync(options, Actor(caller), propose, refuse: true, cancellationToken);

    private async Task<WriteResult> WriteAsync(AccessRolesWriteOptions options, AuditActor actor, Func<WriteContext, Proposal> propose, bool refuse, CancellationToken cancellationToken, bool notify = true, SaveProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var catalog = _catalog.Read();
        var users = _users.GetAll();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        Proposal? proposal = null;
        ChangeReport? report = null;
        var changed = false;

        AccessRolesDocument Evaluate(AccessRolesDocument current)
        {
            // The request is checked against the libraries listed now; what it does is judged against the catalog the sync
            // resolves with (a renamed library withheld until its rename is re-pointed), so a refusal or a preview row
            // never counts a grant the sync wouldn't write.
            proposal = propose(new WriteContext(current, catalog, actor, now));
            report = ChangeAnalysis.Analyze(current, proposal.After, RulesCatalog(current, catalog), users, proposal.Choice ?? options.OnUnmanage);
            if (proposal.PreviewLines.Count > 0)
            {
                report = report with { ExtraLines = proposal.PreviewLines };
            }

            // What the save does is what its dry run showed: the roles it adds, reassigns to or
            // names, the libraries and the users' access are under no other token, so the effect itself is compared.
            if (!options.DryRun && !string.IsNullOrWhiteSpace(options.IfPreview))
            {
                RequirePreview(report, options.IfPreview);
            }

            if (refuse)
            {
                Refuse(report, options);
            }

            changed = !ReferenceEquals(proposal.After, current);
            return changed
                ? proposal.Finish(KeepKnownUsers(ChangeAnalysis.KeepConfirmations(ChangeAnalysis.KeepLedger(proposal.After, report, now), report, options.ConfirmNoFullAdmin, catalog, now), report))
                : current;
        }

        if (options.DryRun)
        {
            var stored = _store.Current;
            AccessRolesDocument preview;
            try
            {
                preview = Evaluate(stored);
            }
            catch (AccessRolesException refused) when (report is not null && refused.Preview is null)
            {
                // A refused dry run answers its refusal with the preview of what the change would do: the
                // admin page shows the users it affects and why it can't be saved yet, from the very request it would save.
                throw refused.WithPreview(AccessRolesMapper.Preview(report));
            }

            return new WriteResult(preview, report!, proposal!, true, []) { Stored = stored };
        }

        _store.Documents.ThrowIfReadOnly();

        // The token cancels only the wait for the store's writer. Once the change may be saving, the caller is told what
        // was done whatever happens next: an aborted request, a failed audit write or a
        // failed handler never turns a saved change into a failure, so a restore that saved keeps its hold of the sync.
        // MayHaveSaved is set once the change hands the store a new document: a write that fails after it (503) may have
        // been swapped in, so the caller keeps what it holds.
        var saved = await _store.UpdateAsync(
            current =>
            {
                var next = Evaluate(current);
                if (!ReferenceEquals(next, current) && progress is not null)
                {
                    progress.MayHaveSaved = true;
                }

                return next;
            },
            cancellationToken,
            notify).ConfigureAwait(false);
        if (!changed)
        {
            return new WriteResult(saved, report!, proposal!, false, []);
        }

        if (progress is not null)
        {
            progress.Saved = true;
        }

        IReadOnlyList<long> seq = [];
        string? auditNote = null;
        try
        {
            var lines = AccessRolesAuditDetails.Lines(proposal!.Lines(report!), report!, proposal.Reason, actor, catalog);
            var written = await _audit.AppendAsync(lines, CancellationToken.None).ConfigureAwait(false);
            seq = [.. written.Select(e => e.Seq)];
            if (lines.Count > 0 && written.Count == 0)
            {
                auditNote = AuditNotWritten;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Access Roles saved a change ({Reason}), but couldn't write its audit lines", proposal!.Reason);
            auditNote = AuditNotWritten;
        }

        _logger.LogInformation(
            "Access Roles saved a change ({Reason}): {Affected} users affected, {Assignments} assignments changed, {Lines} audit lines",
            proposal!.Reason,
            report!.Affected.Count,
            report.Users.Count(u => u.AssignmentChanged),
            seq.Count);
        return new WriteResult(saved, report, proposal, false, seq) { Committed = true, AuditNote = auditNote };
    }

    private AccessRolesChangeDto Answer(WriteResult result, Guid? roleId = null, ServerUser? user = null)
    {
        var catalog = _catalog.Read();
        var role = roleId is { } id ? result.Saved.Roles.FirstOrDefault(r => r.Id.Equals(id)) : null;
        var roleDto = role is null ? null : AccessRolesMapper.Role(role, result.Saved, catalog, _users.GetAll());
        var saved = user is null ? null : _sync().ResolutionView(result.Saved, catalog);
        var effective = user is null ? null : AccessRolesMapper.Effective(EffectiveRulesResolver.Resolve(saved!.Document, saved.Catalog, user));

        // A dry run saved nothing: its tokens are the stored records' (none for a record the change would create), so a
        // client that previews and then saves with the answer's UpdatedUtc isn't refused as Changed.
        if (result.Stored is { } stored)
        {
            if (roleDto is not null)
            {
                roleDto.UpdatedUtc = stored.Roles.FirstOrDefault(r => r.Id.Equals(roleDto.Id))?.UpdatedUtc;
                roleDto.MembersToken = AccessRolesTokens.Members(stored, roleDto.Id);
            }

            if (effective is not null)
            {
                effective.UpdatedUtc = EffectiveRulesResolver.FindAssignment(stored, effective.UserId)?.UpdatedUtc;
            }
        }

        return new AccessRolesChangeDto
        {
            DryRun = result.DryRun,
            Role = roleDto,
            Effective = effective,
            Changed = [.. result.Report.Users.Where(u => u.AssignmentChanged).Select(u => u.User.Id)],
            Preview = AccessRolesMapper.Preview(result.Report),
            AuditSeq = result.AuditSeq,
            Notes = result.AuditNote is { } note ? [note] : []
        };
    }

    /// <summary>What a write's change sees: the stored document, the libraries, the actor and the time.</summary>
    /// <param name="Document">The stored document.</param>
    /// <param name="Catalog">The libraries now.</param>
    /// <param name="Actor">Who writes.</param>
    /// <param name="Now">The time, UTC.</param>
    private sealed record WriteContext(AccessRolesDocument Document, LibraryCatalog Catalog, AuditActor Actor, DateTime Now);

    /// <summary>A write's proposed document, its reason, the request's <c>onUnmanage</c> choice and its own audit lines.</summary>
    /// <param name="After">The document as the change leaves it (the stored instance when it changes nothing).</param>
    /// <param name="Reason">Why assignments change, for their audit lines.</param>
    private sealed record Proposal(AccessRolesDocument After, string Reason)
    {
        /// <summary>Gets the choice the request's body gives, which wins over the query's.</summary>
        public AccessRolesUnmanageChoice? Choice { get; init; }

        /// <summary>Gets the proposal's own audit lines, given the change's report.</summary>
        public Func<ChangeReport, IReadOnlyList<AuditRecord>> Lines { get; init; } = static _ => [];

        /// <summary>Gets the last change of a saved document, after the ledger was kept (the default roles' mark).</summary>
        public Func<AccessRolesDocument, AccessRolesDocument> Finish { get; init; } = static d => d;

        /// <summary>Gets the proposal's own lines of its preview's token (<see cref="ChangeReport.ExtraLines"/>).</summary>
        public IReadOnlyDictionary<string, string> PreviewLines { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>A write's outcome.</summary>
    /// <param name="Saved">The document stored afterwards (a dry run's: the one it would store).</param>
    /// <param name="Report">The change's report.</param>
    /// <param name="Proposal">The proposal.</param>
    /// <param name="DryRun">Whether it was a dry run.</param>
    /// <param name="AuditSeq">The numbers of the audit lines written.</param>
    private sealed record WriteResult(AccessRolesDocument Saved, ChangeReport Report, Proposal Proposal, bool DryRun, IReadOnlyList<long> AuditSeq)
    {
        /// <summary>Gets, for a dry run, the stored document it was evaluated on (whose records' tokens it answers).</summary>
        public AccessRolesDocument? Stored { get; init; }

        /// <summary>Gets a value indicating whether the change was saved (a real write that changed the document).</summary>
        public bool Committed { get; init; }

        /// <summary>Gets what the answer says when the saved change's audit lines couldn't be written, else <c>null</c>.</summary>
        public string? AuditNote { get; init; }
    }

    /// <summary>
    /// How far a real write got, for a caller that holds something until the save (a restore's hold of the sync): it lets
    /// go only of what a write that certainly saved nothing leaves behind.
    /// </summary>
    private sealed class SaveProgress
    {
        /// <summary>Gets or sets a value indicating whether the store was handed the new document: from then on it may be saved.</summary>
        public bool MayHaveSaved { get; set; }

        /// <summary>Gets or sets a value indicating whether the save is done.</summary>
        public bool Saved { get; set; }
    }
}
