using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The store as the kit's <see cref="IUserDataOwner"/>: a user's records are
/// their assignment and their ledger entry. Access Roles' deleted-user policy is <c>Delete</c>: when Jellyfin
/// deletes a user, both go and <c>UserDeleted</c> is audited. The reconciler's counts and names, and <i>Remap by name</i>,
/// as the kit asks. The lines are the plugin's own (<c>System</c>) when Jellyfin deleted the user, and the administrator's
/// or API key's when it asked through <i>Apply deleted-user policy</i> or <i>Remap by name</i> (<see cref="AuditActorScope"/>).
/// </summary>
internal sealed class AccessRolesUserDataOwner : IUserDataOwner
{
    private readonly AccessRolesStore _store;
    private readonly AccessRolesAuditLog _audit;
    private readonly string _snapshotDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesUserDataOwner"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="snapshotDirectory">The snapshot folder (<c>AccessRolesPaths.SnapshotsDirectory</c>).</param>
    /// <param name="logger">The logger.</param>
    public AccessRolesUserDataOwner(AccessRolesStore store, AccessRolesAuditLog audit, string snapshotDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _audit = audit;
        _snapshotDirectory = snapshotDirectory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "accessroles";

    /// <inheritdoc />
    public bool IsReadOnly => _store.IsReadOnly;

    /// <inheritdoc />
    public KnownUsers KnownUsers => _store.Current.KnownUsers;

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser()
    {
        var document = _store.Current;
        return document.Assignments.Select(a => a.UserId).Concat(document.Ledger.Keys).Concat(document.PreferenceMirror.Keys)
            .Where(id => !id.Equals(Guid.Empty))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc />
    public async Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentNames);
        if (_store.IsReadOnly)
        {
            return;
        }

        await _store.UpdateAsync(
            document =>
            {
                var known = document.KnownUsers.Refresh(document.Assignments.Select(a => a.UserId).Concat(document.Ledger.Keys), currentNames);
                return ReferenceEquals(known, document.KnownUsers) ? document : document with { KnownUsers = known };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
        => await _store.Documents.PreOpSnapshotAsync(_snapshotDirectory, operation, cancellationToken).ConfigureAwait(false) is { } path
            ? System.IO.Path.GetFileName(path)
            : null;

    /// <inheritdoc />
    public async Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken)
    {
        if (userId.Equals(Guid.Empty) || _store.IsReadOnly)
        {
            return 0;
        }

        RoleAssignment? assignment = null;
        var hadLedger = false;
        var hadMirror = false;
        string? name = null;
        await _store.UpdateAsync(
            document =>
            {
                assignment = document.Assignments.FirstOrDefault(a => a.UserId.Equals(userId));
                hadLedger = document.Ledger.ContainsKey(userId);
                hadMirror = document.PreferenceMirror.ContainsKey(userId);
                name = document.KnownUsers.NameOf(userId);
                if (assignment is null && !hadLedger && !hadMirror)
                {
                    return document;
                }

                return document with
                {
                    Assignments = [.. document.Assignments.Where(a => !a.UserId.Equals(userId))],
                    Ledger = document.Ledger.Where(p => !p.Key.Equals(userId)).ToDictionary(p => p.Key, p => p.Value),
                    PreferenceMirror = document.PreferenceMirror.Where(p => !p.Key.Equals(userId)).ToDictionary(p => p.Key, p => p.Value),
                    KnownUsers = document.KnownUsers.Without(userId)
                };
            },
            cancellationToken).ConfigureAwait(false);

        var removed = (assignment is null ? 0 : 1) + (hadLedger ? 1 : 0) + (hadMirror ? 1 : 0);
        if (removed == 0)
        {
            return 0;
        }

        await _audit.AppendAsync(
            [new AuditRecord(AuditActorScope.Current ?? AuditActor.System, AuditActions.UserDeleted, userId, null, name, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Policy"] = policy.ToString(),
                ["RoleIds"] = AccessRolesAuditDetails.Ids(assignment?.RoleIds ?? []),
                ["State"] = assignment?.State.Name,
                ["HadLedgerEntry"] = hadLedger
            })],
            CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Deleted user {UserId}: Access Roles removed {Records} records (assignment and ledger entry)", userId, removed);
        return removed;
    }

    /// <inheritdoc />
    public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Count == 0 || _store.IsReadOnly)
        {
            return 0;
        }

        var moved = 0;
        var pairs = new List<Dictionary<string, object?>>();
        await _store.UpdateAsync(
            document =>
            {
                moved = 0;
                pairs.Clear();
                var assignments = document.Assignments.ToList();
                var ledger = document.Ledger.ToDictionary(p => p.Key, p => p.Value);

                // The mirror's record of an old id goes, never to the new id: the new user's flag isn't one the mirror
                // turned off, so it must never turn it on (the mirror looks at the new user afresh).
                var mirror = document.PreferenceMirror.Where(p => !map.ContainsKey(p.Key) || map[p.Key].Equals(p.Key)).ToDictionary(p => p.Key, p => p.Value);
                foreach (var (oldId, newId) in map)
                {
                    if (oldId.Equals(newId) || newId.Equals(Guid.Empty))
                    {
                        continue;
                    }

                    // A new id that already has an assignment or a ledger entry keeps its own; the old id's go.
                    var index = assignments.FindIndex(a => a.UserId.Equals(oldId));
                    if (index >= 0)
                    {
                        if (assignments.Exists(a => a.UserId.Equals(newId)))
                        {
                            assignments.RemoveAt(index);
                        }
                        else
                        {
                            // A lockout confirmation is the admin's for the user it was given for: the new id is
                            // another account, whose narrowing is confirmed afresh.
                            assignments[index] = assignments[index] with { UserId = newId, ConfirmedNoFullAdminUtc = null };
                            moved++;
                        }
                    }

                    if (ledger.Remove(oldId, out var entry))
                    {
                        if (ledger.TryAdd(newId, entry))
                        {
                            moved++;
                        }
                    }

                    pairs.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["OldId"] = AccessRolesAuditDetails.Id(oldId), ["NewId"] = AccessRolesAuditDetails.Id(newId) });
                }

                return moved == 0 && assignments.Count == document.Assignments.Count && ledger.Count == document.Ledger.Count && mirror.Count == document.PreferenceMirror.Count
                    ? document
                    : document with { Assignments = assignments, Ledger = ledger, PreferenceMirror = mirror, KnownUsers = document.KnownUsers.Remap(map) };
            },
            cancellationToken).ConfigureAwait(false);

        if (moved > 0)
        {
            await _audit.AppendAsync(
                [new AuditRecord(AuditActorScope.Current ?? AuditActor.System, AuditActions.UsersRemapped, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["Users"] = pairs, ["Records"] = moved })],
                CancellationToken.None).ConfigureAwait(false);
        }

        return moved;
    }
}
