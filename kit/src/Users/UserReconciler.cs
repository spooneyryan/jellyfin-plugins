using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// Deleted and unknown users: the policy on an explicit deletion, the maintenance reconciler that
/// never deletes, <i>Remap by name</i> and <i>Apply deleted-user policy</i>, over every <see cref="IUserDataOwner"/> of the
/// plugin.
/// </summary>
/// <remarks>
/// <para><b>Only an explicit deletion applies the policy</b>: Jellyfin's <c>UserDeletedEventArgs</c>
/// (<see cref="HandleUserDeletedAsync"/>, through <see cref="KitUserDeletedConsumer"/>) or the admin's
/// <see cref="ApplyPolicyAsync"/>. An unknown id found later is not one: after a server is rebuilt and a data folder
/// restored, every stored id is unknown, and a policy applied then would wipe everything before the admin could remap.</para>
/// <para><b>The reconciler</b> (<see cref="ReconcileAsync"/>, run by the plugin's maintenance) compares the stored user ids
/// with <see cref="KitUsers.GetAll"/> and marks unknown ids as orphans (<c>kit-users.json</c>): admin-visible, excluded
/// from user views and aggregates (<see cref="IsOrphan"/>), and listed in Diagnostics with their last seen names. It
/// never changes a record. <b>Guard</b>: when more than one id, or more than 10 % of the records, would become orphans in
/// one run, it changes nothing at all (no marks, no names), logs a Warning, and Diagnostics shows "Stored users don't match
/// this server" until the admin remaps or applies the policy. Ids marked before (users deleted on this server while
/// they held records, whose records may come back with a restored snapshot, and ids of an earlier run) don't count
/// towards it.</para>
/// <para>Every operation skips owners whose store is read-only; the admin actions refuse (409) while one is. The marks of
/// ids such an owner may hold stay meanwhile, so its orphans don't count as new when it is back.</para>
/// <para><b>Pending policies</b>: when Jellyfin reports a deletion and an owner fails to count or change the user's
/// records (User Ratings while its archive can't be read), the user is still marked deleted and the owner is recorded
/// in the mark (<see cref="OrphanMark.PolicyPending"/>); every reconciliation applies the policy (the setting as it is
/// then) to it again, after the owner's pre-operation snapshot, until it succeeds. Diagnostics counts them
/// (<c>PolicyPending</c>). A read-only owner is left for the admin, as before. Since User Ratings 1.0.1 a pending
/// policy is applied only while the server still doesn't know the id: a user who is back (Jellyfin's database restored
/// with them in it) keeps every record, and their mark goes; while the server's user list can't be read or is empty,
/// nothing pending is applied. A mark of any kind goes once the server knows its id.</para>
/// </remarks>
internal sealed class UserReconciler : IDisposable
{
    /// <summary>The Diagnostics banner while the guard holds unknown ids back.</summary>
    public const string MismatchBanner = "Stored users don't match this server; was data restored from another server? Nothing was changed. Use Remap by name, or apply the deleted-user policy to users that were really deleted.";

    /// <summary>The 503 of <see cref="ApplyPolicyAsync"/> while the server's user list can't be read or is
    /// empty.</summary>
    public const string UserListUnavailableMessage = "The server's user list is unavailable, so the deleted-user policy can't tell deleted users from current ones. Nothing was changed; try again later.";

    /// <summary>The 400 of a bound remap's real run without the dry run's token (<see cref="RemapByNameAsync(bool, string?, CancellationToken)"/>).</summary>
    public const string PlanRequiredMessage = "A remap that writes needs ifPlan: the PlanToken of the dry run you checked.";

    /// <summary>The 409 of a bound remap whose plan changed since its dry run (<c>Changed</c>).</summary>
    public const string PlanChangedMessage = "Changed: the users to remap, their records, or the conflicts the remap would make, changed since the dry run. Nothing was written; run the dry run again and check it.";

    private readonly Func<IReadOnlyDictionary<Guid, string>> _serverUsers;
    private readonly Func<IReadOnlyList<IUserDataOwner>> _owners;
    private readonly Func<IReadOnlyList<string>> _unavailable;
    private readonly KitUserMarks _marks;
    private readonly Func<DeletedUserPolicy> _policy;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile UserReconcileResult? _last;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserReconciler"/> class with a fixed list of owners (tests, tools).
    /// </summary>
    /// <param name="serverUsers">The server's users, id to name (<see cref="KitUsers.GetAll"/>).</param>
    /// <param name="owners">The plugin's owners.</param>
    /// <param name="marks">The orphan marks.</param>
    /// <param name="policy">The plugin's <c>DeletedUserPolicy</c> setting.</param>
    /// <param name="logger">The logger (<c>Jellyfin.Plugin.&lt;X&gt;.Kit.Users</c>).</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public UserReconciler(
        Func<IReadOnlyDictionary<Guid, string>> serverUsers,
        IEnumerable<IUserDataOwner> owners,
        KitUserMarks marks,
        Func<DeletedUserPolicy> policy,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : this(serverUsers, FixedOwners(owners), static () => [], marks, policy, logger, timeProvider)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UserReconciler"/> class with owners asked for at every use:
    /// <c>AddKitUsers</c> passes the owners of <c>AddKitUserDataOwner</c>, each created on its own
    /// (<see cref="KitComponent{T}"/>), so one that can't be created costs only itself and is tried again at the next
    /// use.
    /// </summary>
    /// <param name="serverUsers">The server's users, id to name (<see cref="KitUsers.GetAll"/>).</param>
    /// <param name="owners">The owners that can be created now, asked at every use.</param>
    /// <param name="unavailable">The names of the owners that couldn't be created, asked right after
    /// <paramref name="owners"/>: they are left out of every run like a read-only owner (the ids they may hold keep their
    /// marks, and the admin actions refuse with 409).</param>
    /// <param name="marks">The orphan marks.</param>
    /// <param name="policy">The plugin's <c>DeletedUserPolicy</c> setting.</param>
    /// <param name="logger">The logger (<c>Jellyfin.Plugin.&lt;X&gt;.Kit.Users</c>).</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public UserReconciler(
        Func<IReadOnlyDictionary<Guid, string>> serverUsers,
        Func<IReadOnlyList<IUserDataOwner>> owners,
        Func<IReadOnlyList<string>> unavailable,
        KitUserMarks marks,
        Func<DeletedUserPolicy> policy,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(serverUsers);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(unavailable);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _serverUsers = serverUsers;
        _owners = owners;
        _unavailable = unavailable;
        _marks = marks;
        _policy = policy;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the last run of <see cref="ReconcileAsync"/> in this process, or <c>null</c>.
    /// </summary>
    public UserReconcileResult? LastResult => _last;

    /// <summary>
    /// Gets the ids of users deleted on this server while they held records (Jellyfin's event, or the admin's
    /// <see cref="ApplyPolicyAsync"/>). <i>Remap by name</i> never maps them, an export flags them
    /// (<c>ExportUser.Deleted</c>), and an import never gives their records to anyone by name.
    /// </summary>
    public IReadOnlySet<Guid> DeletedUserIds => _marks.DeletedIds;

    /// <summary>
    /// Gets the plugin's policy now; a setting that can't be read gives <see cref="DeletedUserPolicy.KeepAsOrphan"/>,
    /// which changes the least.
    /// </summary>
    public DeletedUserPolicy Policy
    {
        get
        {
            try
            {
                return _policy();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the deleted-user policy; using KeepAsOrphan");
                return DeletedUserPolicy.KeepAsOrphan;
            }
        }
    }

    /// <summary>
    /// Tells whether a user id is marked as an orphan: its records are left out of views and aggregates.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> when marked.</returns>
    public bool IsOrphan(Guid userId) => _marks.Current.ContainsKey(userId);

    /// <summary>
    /// Lists the stored ids the server doesn't know, with their names, records and marks, without changing anything.
    /// </summary>
    /// <returns>The unknown users, most records first.</returns>
    public IReadOnlyList<OrphanUser> FindUnknownUsers() => Survey().Unknown;

    /// <summary>
    /// The maintenance run (see the remarks): marks unknown ids as orphans and refreshes the owners' names, unless the guard
    /// trips. Never changes a record.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for another operation.</param>
    /// <returns>The run.</returns>
    public async Task<UserReconcileResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Applies the policy for a user Jellyfin reports deleted (<c>UserDeletedEventArgs</c>): each owner holding records
    /// of the user takes a pre-operation snapshot and applies it, and the id is remembered as deleted here (records it
    /// kept are orphans). An owner that is read-only is logged and left for the admin (the reconciler lists the id
    /// later); one that fails is logged and its policy is pending, applied again by every reconciliation until it
    /// succeeds. A user without data leaves no trace.
    /// </summary>
    /// <param name="userId">The deleted user.</param>
    /// <param name="cancellationToken">Cancels waiting for the stores.</param>
    /// <returns>How many records were removed or changed.</returns>
    public async Task<int> HandleUserDeletedAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId.Equals(Guid.Empty))
        {
            return 0;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var policy = Policy;
            var (affected, _) = await ApplyLockedAsync([userId], policy, SnapshotOperations.UserDeleted, OrphanMarkSource.Deleted, strict: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("User {UserId} was deleted: the deleted-user policy {Policy} changed {Affected} records", userId, policy, affected);
            return affected;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// <i>Apply deleted-user policy</i> (<c>POST /&lt;Route&gt;/Admin/Users/ApplyPolicy</c>): the plugin's
    /// policy for ids the reconciler lists, after a pre-operation snapshot of each owner that holds their records.
    /// </summary>
    /// <param name="userIds">The ids; none may be a current user.</param>
    /// <param name="cancellationToken">Cancels waiting for the stores.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PluginException">400 no ids, too many, an empty id or a current user's id; 409 a store is read-only
    /// (also one that becomes read-only part way: the ids every owner finished are marked, the others aren't); 503 the server's
    /// user list can't be read or is empty (nothing written).</exception>
    public async Task<UserPolicyReport> ApplyPolicyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0 || userIds.Count > 1000)
        {
            throw PluginException.Invalid("Give between 1 and 1000 user ids.");
        }

        if (userIds.Any(id => id.Equals(Guid.Empty)))
        {
            throw PluginException.Invalid("A user id is empty.");
        }

        // The current-user check needs the server's users: an empty list (a directory service down) or one that can't
        // be read would let a current user's id through, so the run is refused before anything is written.
        IReadOnlyDictionary<Guid, string> server;
        try
        {
            server = _serverUsers();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The server's user list couldn't be read; Apply deleted-user policy was refused");
            throw new PluginException(PluginErrorKind.Unavailable, UserListUnavailableMessage, ex);
        }

        if (server.Count == 0)
        {
            _logger.LogWarning("The server's user list is empty; Apply deleted-user policy was refused");
            throw PluginException.Unavailable(UserListUnavailableMessage);
        }

        var current = userIds.FirstOrDefault(server.ContainsKey);
        if (!current.Equals(Guid.Empty))
        {
            throw PluginException.Invalid($"{KnownUsers.FormatId(current)} is a user of this server; the deleted-user policy is only for users that are gone.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ids = userIds.Distinct().ToList();
            var policy = Policy;
            var (affected, report) = await ApplyLockedAsync(ids, policy, SnapshotOperations.ApplyPolicy, OrphanMarkSource.PolicyApplied, strict: true, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("An admin applied the deleted-user policy {Policy} to {Users} unknown users: {Affected} records changed", policy, ids.Count, affected);
            await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            return report;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// <i>Remap by name</i> (<c>POST /&lt;Route&gt;/Admin/Users/Remap?by=Name&amp;dryRun=true</c>): each unknown stored
    /// id whose last seen name is a server user's name (ignoring case) is given to that user, after a pre-operation
    /// snapshot of each owner concerned. Ids without a name, names no server user or two stored ids have, and users
    /// Jellyfin reported deleted here are left alone and listed. While the list of users deleted here
    /// (<c>kit-users.json</c>) can't be read, it refuses, a dry run too: it can't tell those users (it fails closed).
    /// </summary>
    /// <param name="dryRun">Whether to only report.</param>
    /// <param name="cancellationToken">Cancels waiting for the stores.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PluginException">409 the list of deleted users can't be read, or a store is read-only (not in a dry
    /// run).</exception>
    public Task<UserRemapReport> RemapByNameAsync(bool dryRun, CancellationToken cancellationToken)
        => RemapAsync(dryRun, bound: false, ifPlan: null, cancellationToken);

    /// <summary>
    /// <i>Remap by name</i> bound to its dry run (opt-in, User Ratings' route): as
    /// <see cref="RemapByNameAsync(bool, CancellationToken)"/>, and the report carries
    /// <see cref="UserRemapReport.PlanToken"/>, a hash of the plan (each old id, new id and record count mapped and the
    /// records each owner would leave in conflict, <see cref="IUserDataOwner.CountRemapConflicts"/>). A real run must
    /// be given the token of the dry run the admin checked, in any case: it maps nothing, and makes no conflict, a dry
    /// run didn't show.
    /// </summary>
    /// <param name="dryRun">Whether to only report.</param>
    /// <param name="ifPlan">The dry run's <see cref="UserRemapReport.PlanToken"/>; required for a real run.</param>
    /// <param name="cancellationToken">Cancels waiting for the stores.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PluginException">400 a real run without <paramref name="ifPlan"/>; 409 the plan is no longer the
    /// one of <paramref name="ifPlan"/> (<see cref="PlanChangedMessage"/>, nothing written), or as the other overload.</exception>
    public Task<UserRemapReport> RemapByNameAsync(bool dryRun, string? ifPlan, CancellationToken cancellationToken)
    {
        if (!dryRun && string.IsNullOrWhiteSpace(ifPlan))
        {
            throw PluginException.Invalid(PlanRequiredMessage);
        }

        return RemapAsync(dryRun, bound: true, ifPlan?.Trim(), cancellationToken);
    }

    /// <summary>
    /// The token of a remap's plan: the first 10 bytes of a SHA-256 over each mapped old id, new id, record count and
    /// conflict count, in the report's order, as lower-case hex with a version prefix (<c>p1-</c>).
    /// </summary>
    /// <param name="mapped">The plan.</param>
    /// <param name="conflicts">Each entry's records the owners would leave in conflict, in the same order; none, 0 each.</param>
    /// <returns>The token.</returns>
    internal static string PlanTokenOf(IReadOnlyList<UserRemapEntry> mapped, IReadOnlyList<int>? conflicts = null)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        var text = string.Join('\n', mapped.Select((m, i) => string.Create(CultureInfo.InvariantCulture, $"{KnownUsers.FormatId(m.OldId)}>{KnownUsers.FormatId(m.NewId)}:{m.Records}:{(conflicts is null ? 0 : conflicts[i])}")));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "p1-" + Convert.ToHexStringLower(hash.AsSpan(0, 10));
    }

    private async Task<UserRemapReport> RemapAsync(bool dryRun, bool bound, string? ifPlan, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _marks.ThrowIfDeletedUsersUnknown("Remap by name");
            var survey = Survey();
            if (!dryRun)
            {
                ThrowIfAnyReadOnly();
            }

            var serverByName = survey.ServerUsers
                .Where(u => !string.IsNullOrWhiteSpace(u.Value))
                .GroupBy(u => u.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var namesTaken = survey.Unknown
                .Where(u => u.LastKnownName is not null)
                .GroupBy(u => u.LastKnownName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var mapped = new List<UserRemapEntry>();
            var unmatched = new List<UserRemapUnmatched>();
            foreach (var user in survey.Unknown.OrderBy(u => u.LastKnownName ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(u => KnownUsers.FormatId(u.UserId), StringComparer.Ordinal))
            {
                var name = user.LastKnownName?.Trim();
                string? reason = null;
                List<KeyValuePair<Guid, string>>? matches = null;
                if (user.Mark is { Source: not OrphanMarkSource.Reconciler })
                {
                    reason = UserRemapUnmatched.DeletedOnThisServer;
                }
                else if (string.IsNullOrEmpty(name))
                {
                    reason = UserRemapUnmatched.NoKnownName;
                }
                else if (namesTaken[name] > 1)
                {
                    reason = UserRemapUnmatched.SameNameTwice;
                }
                else if (!serverByName.TryGetValue(name, out matches) || matches.Count != 1)
                {
                    reason = UserRemapUnmatched.NoUserWithThisName;
                }

                if (reason is null)
                {
                    var target = matches![0];
                    mapped.Add(new UserRemapEntry(user.UserId, target.Key, target.Value, user.Records));
                }
                else
                {
                    unmatched.Add(new UserRemapUnmatched(user.UserId, user.LastKnownName, user.Records, reason));
                }
            }

            var token = bound ? PlanTokenOf(mapped, mapped.Select(m => survey.Active.Sum(o => o.CountRemapConflicts(m.OldId, m.NewId))).ToList()) : null;
            if (bound && !dryRun && !string.Equals(ifPlan, token, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Remap by name was refused: its plan changed since the dry run ({Mapped} ids would be mapped now)", mapped.Count);
                throw PluginException.Conflict(PlanChangedMessage);
            }

            if (dryRun || mapped.Count == 0)
            {
                return new UserRemapReport(dryRun, mapped, unmatched, 0, []) { PlanToken = token };
            }

            var map = mapped.ToDictionary(m => m.OldId, m => m.NewId);
            var snapshots = new List<string>();
            var moved = 0;
            foreach (var owner in survey.Active)
            {
                var counts = owner.CountRecordsByUser();
                if (!map.Keys.Any(counts.ContainsKey))
                {
                    continue;
                }

                if (await owner.SnapshotBeforeAsync(SnapshotOperations.Remap, cancellationToken).ConfigureAwait(false) is { } snapshot)
                {
                    snapshots.Add(snapshot);
                }

                moved += await owner.RemapUsersAsync(map, cancellationToken).ConfigureAwait(false);
            }

            await _marks.UnmarkAsync(map.Keys.ToList(), cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("An admin remapped {Users} unknown user ids by name: {Records} records moved to current users", map.Count, moved);
            await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            return new UserRemapReport(false, mapped, unmatched, moved, snapshots) { PlanToken = token };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Refuses (409) what would give records to users by name, such as an import that maps users by name, while the list of
    /// users deleted on this server (<c>kit-users.json</c>) can't be read (<see cref="KitUserMarks.ThrowIfDeletedUsersUnknown"/>).
    /// </summary>
    /// <param name="action">What is refused, to start the sentence ("An import by name").</param>
    /// <exception cref="PluginException">409 while the list can't be read.</exception>
    public void ThrowIfDeletedUsersUnknown(string action) => _marks.ThrowIfDeletedUsersUnknown(action);

    /// <summary>
    /// An admin's <i>Start empty</i> for the list of users deleted on this server while <c>kit-users.json</c> can't be
    /// read (<see cref="KitUserMarks.StartEmptyAsync"/>): what couldn't be read is kept aside, the marks made in this
    /// process are saved, and the reconciler runs at once, so the orphan marks are rebuilt from the server's users.
    /// Marks of users deleted while the file couldn't be read, in an earlier process, are lost: their records count as
    /// unknown users (Remap by name may then give them to a user of the same name).
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for another operation.</param>
    /// <returns><c>{StartedEmpty, MovedAside, MarksKept, Orphans}</c>.</returns>
    /// <exception cref="PluginException">409 the list isn't unreadable, or a readable copy is in place now; 503 the write
    /// failed.</exception>
    public async Task<IDictionary<string, object?>> StartMarksEmptyAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (recovery, kept) = await _marks.StartEmptyAsync(cancellationToken).ConfigureAwait(false);
            var run = await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "The list of users deleted on this server ({File}) was started empty: {Kept} marks made since the server started were kept, and the reconciler marked {Orphans} unknown users",
                _marks.Store.FilePath,
                kept,
                run.Orphans.Count);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["StartedEmpty"] = true,
                ["MovedAside"] = recovery.MovedAside,
                ["MarksKept"] = kept,
                ["Orphans"] = run.Orphans.Count
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The <c>Users</c> entry of the plugin's Diagnostics (<c>OrphanUsers</c>): the policy, the unknown users now
    /// (with their last seen names; Diagnostics is admin-only), the last run, the banner while the guard holds ids back, and
    /// the marks' own file (<c>Marks</c>: <see cref="KitUserMarks.Describe"/>).
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        var survey = Survey();
        var last = _last;
        var mismatch = last is { GuardTripped: true } && survey.Unknown.Any(u => u.Mark is null);
        var orphanRecords = survey.Unknown.Sum(u => u.Records);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DeletedUserPolicy"] = Policy.ToString(),
            ["LastReconciledUtc"] = last?.RanUtc,
            ["ServerUsers"] = survey.ServerUsers.Count,
            ["StoredUsers"] = survey.StoredUsers,
            ["Records"] = survey.Records,
            ["OrphanUsers"] = survey.Unknown.Select(u => u.ToDictionary()).ToList(),
            ["OrphanRecords"] = orphanRecords,
            ["StoredUsersMismatch"] = mismatch,
            ["Banner"] = mismatch ? MismatchBanner : null,
            ["Message"] = survey.Unknown.Count == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{orphanRecords} records belong to {survey.Unknown.Count} unknown users."),
            ["SkippedOwners"] = survey.SkippedOwners,
            ["PolicyPending"] = _marks.Current.Values.Count(m => m.PolicyPending is { Count: > 0 }),
            ["Marks"] = _marks.Describe()
        };
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private static Func<IReadOnlyList<IUserDataOwner>> FixedOwners(IEnumerable<IUserDataOwner> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var list = owners.ToList();
        return () => list;
    }

    private void ThrowIfAnyReadOnly()
    {
        var readOnly = _owners().FirstOrDefault(o => o.IsReadOnly);
        if (readOnly is not null)
        {
            throw PluginException.Conflict($"The {readOnly.Name} data is read-only, so nothing can be changed; see the store's status in Diagnostics.");
        }

        if (_unavailable() is { Count: > 0 } missing)
        {
            throw PluginException.Conflict($"{missing[0]} can't be started now, so its data can't be changed; see the plugin's Diagnostics (Users, Unavailable).");
        }
    }

    // Runs under _lock.
    private async Task<(int Affected, UserPolicyReport Report)> ApplyLockedAsync(
        IReadOnlyList<Guid> ids,
        DeletedUserPolicy policy,
        string operation,
        OrphanMarkSource source,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (strict)
        {
            ThrowIfAnyReadOnly();
        }

        var perUser = ids.ToDictionary(id => id, _ => 0);
        var snapshots = new List<string>();
        var hadRecords = new HashSet<Guid>();

        // The owners whose policy failed for an id, in the event's run (not strict): pending, applied again daily.
        var failed = new Dictionary<Guid, IReadOnlyCollection<string>>();
        void Fail(IUserDataOwner owner, IEnumerable<Guid> which)
        {
            foreach (var id in which)
            {
                failed[id] = (failed.GetValueOrDefault(id) ?? []).Append(owner.Name).ToList();
            }
        }

        var owners = _owners();
        foreach (var missing in _unavailable())
        {
            _logger.LogWarning("{Owner} can't be started now, so the deleted-user policy was not applied to its data; Diagnostics lists the user once it can be", missing);
        }

        // Which owner holds which ids, first, so that a strict run that fails part way remembers only the ids every
        // owner holding them finished.
        var holders = new List<(IUserDataOwner Owner, List<Guid> Ids)>();
        foreach (var owner in owners)
        {
            try
            {
                var counts = owner.CountRecordsByUser();
                var held = ids.Where(counts.ContainsKey).ToList();
                hadRecords.UnionWith(held);
                if (held.Count > 0)
                {
                    holders.Add((owner, held));
                }
            }
            catch (Exception ex) when (!strict && ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not apply the deleted-user policy to the {Owner} data; it is applied again at the next reconciliation", owner.Name);
                Fail(owner, ids);
            }
        }

        var pending = ids.ToDictionary(id => id, id => holders.Count(h => h.Ids.Contains(id)));
        try
        {
            foreach (var (owner, held) in holders)
            {
                var finished = new List<Guid>();
                try
                {
                    if (owner.IsReadOnly)
                    {
                        if (strict)
                        {
                            throw PluginException.Conflict($"The {owner.Name} data became read-only, so the deleted-user policy was applied only in part; apply it again once the data can be written.");
                        }

                        _logger.LogWarning("The {Owner} data is read-only; the deleted-user policy was not applied to it. Diagnostics lists the user once it can be written", owner.Name);
                        continue;
                    }

                    // A snapshot only before a change: applying KeepAsOrphan again, or a policy to ids with nothing
                    // left to change, takes none.
                    if (held.Any(id => owner.WouldChangeRecords(id, policy))
                        && await owner.SnapshotBeforeAsync(operation, cancellationToken).ConfigureAwait(false) is { } snapshot)
                    {
                        snapshots.Add(snapshot);
                    }

                    foreach (var id in held)
                    {
                        perUser[id] += await owner.HandleUserDeletedAsync(id, policy, cancellationToken).ConfigureAwait(false);
                        pending[id]--;
                        finished.Add(id);
                    }
                }
                catch (Exception ex) when (!strict && ex is not OperationCanceledException)
                {
                    // Jellyfin's EventManager would log it too, but the other owners must still run.
                    _logger.LogError(ex, "Could not apply the deleted-user policy to the {Owner} data; it is applied again at the next reconciliation", owner.Name);
                    Fail(owner, held.Except(finished));
                }
            }
        }
        catch (Exception) when (strict)
        {
            await MarkFinishedAsync(ids.Where(id => pending[id] == 0).ToList(), source).ConfigureAwait(false);
            throw;
        }

        // Ids that held records (and every id an admin applied the policy to) are remembered as deleted here: records that
        // were kept (KeepAsOrphan, Anonymize, a failed owner) are orphans the admin already knows about, and records that
        // come back later (a restored snapshot, an import) are never counted by the guard nor remapped to a new user of the
        // same name. A user without data leaves no trace.
        var remember = strict ? ids : ids.Where(id => hadRecords.Contains(id) || failed.ContainsKey(id)).ToList();
        if (remember.Count > 0)
        {
            await _marks.MarkDeletedAsync(remember, source, cancellationToken).ConfigureAwait(false);
        }

        if (failed.Count > 0)
        {
            await _marks.AddPolicyPendingAsync(failed, source, cancellationToken).ConfigureAwait(false);
        }

        var affected = perUser.Values.Sum();
        return (affected, new UserPolicyReport(policy, affected, perUser, snapshots));
    }

    // A strict run that failed part way: the ids every owner holding them finished are remembered as applied, and a failure
    // to remember them is logged without hiding the run's own exception.
    private async Task MarkFinishedAsync(IReadOnlyList<Guid> done, OrphanMarkSource source)
    {
        if (done.Count == 0)
        {
            return;
        }

        try
        {
            await _marks.MarkDeletedAsync(done, source, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The deleted-user policy was applied to {Count} users before it failed, but they couldn't be marked; apply it to them again", done.Count);
        }
    }

    // Runs under _lock: the policies that failed when Jellyfin reported a deletion (OrphanMark.PolicyPending), applied
    // again to each owner that can be read and written now, after its pre-operation snapshot; one that succeeds is no
    // longer pending, one that fails again stays. Since User Ratings 1.0.1: only to ids the server still doesn't know.
    // An id that is a user of this server again (Jellyfin's database restored with the user in it) is that user's:
    // nothing of theirs is changed, and their mark goes. While the server's user list can't be read, or is empty,
    // nothing is applied at all (fail closed: such a user can't be told from one who is gone).
    private async Task RetryPendingLockedAsync(CancellationToken cancellationToken)
    {
        var pending = _marks.Current.Values.Where(m => m.PolicyPending is { Count: > 0 }).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<Guid, string> server;
        try
        {
            server = _serverUsers();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The server's user list couldn't be read, so the deleted-user policy still pending for {Users} users wasn't applied: a user who is back can't be told from one who is gone. It is tried again at the next reconciliation", pending.Count);
            return;
        }

        if (server.Count == 0)
        {
            _logger.LogWarning("The server's user list is empty, so the deleted-user policy still pending for {Users} users wasn't applied: a user who is back can't be told from one who is gone. It is tried again at the next reconciliation", pending.Count);
            return;
        }

        var back = pending.Where(m => server.ContainsKey(m.UserId)).Select(m => m.UserId).ToList();
        if (back.Count > 0)
        {
            await _marks.UnmarkAsync(back, cancellationToken).ConfigureAwait(false);
            foreach (var id in back)
            {
                _logger.LogWarning("User {UserId} is a user of this server again (was Jellyfin's database restored?), so the deleted-user policy still pending for them was dropped: their records are theirs, unchanged", id);
            }
        }

        var policy = Policy;
        var owners = _owners();
        foreach (var mark in pending.Where(m => !server.ContainsKey(m.UserId)))
        {
            foreach (var name in mark.PolicyPending!)
            {
                var owner = owners.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
                if (owner is null || owner.IsReadOnly)
                {
                    continue;
                }

                try
                {
                    var affected = 0;
                    if (owner.CountRecordsByUser().ContainsKey(mark.UserId))
                    {
                        if (owner.WouldChangeRecords(mark.UserId, policy))
                        {
                            await owner.SnapshotBeforeAsync(SnapshotOperations.UserDeleted, cancellationToken).ConfigureAwait(false);
                        }

                        affected = await owner.HandleUserDeletedAsync(mark.UserId, policy, cancellationToken).ConfigureAwait(false);
                    }

                    await _marks.ClearPolicyPendingAsync(mark.UserId, name, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("The deleted-user policy {Policy} that failed for user {UserId} in the {Owner} data was applied now: {Affected} records changed", policy, mark.UserId, name, affected);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The deleted-user policy for user {UserId} still can't be applied to the {Owner} data; it is tried again at the next reconciliation", mark.UserId, name);
                }
            }
        }
    }

    // Runs under _lock.
    private async Task<UserReconcileResult> ReconcileLockedAsync(CancellationToken cancellationToken)
    {
        await RetryPendingLockedAsync(cancellationToken).ConfigureAwait(false);
        var survey = Survey();
        var unknownIds = survey.Unknown.Select(u => u.UserId).ToList();
        var fresh = survey.Unknown.Where(u => u.Mark is null).ToList();
        var freshRecords = fresh.Sum(u => u.Records);
        var tripped = fresh.Count > 1 || (survey.Records > 0 && freshRecords * 10L > survey.Records);

        if (tripped)
        {
            _logger.LogWarning(
                "{Records} of {Total} records belong to {Users} user ids this server doesn't know. Nothing was changed: was the data folder restored from another server? Remap by name, or apply the deleted-user policy to users that were really deleted, from the plugin's Diagnostics",
                freshRecords,
                survey.Records,
                fresh.Count);
        }
        else
        {
            // An owner left out of the run (read-only, unreadable) may still hold records of ids marked before; their marks
            // stay until it is back, or the server knows the id again, so its orphans don't count as new then. A mark of an id
            // the server knows again goes whatever made it (since User Ratings 1.0.1): the user is back, so it is done.
            Func<Guid, bool>? canUnmark = survey.SkippedOwners.Count == 0 ? null : survey.ServerUsers.ContainsKey;
            await _marks.SetReconciledAsync(unknownIds, canUnmark, survey.ServerUsers.ContainsKey, cancellationToken).ConfigureAwait(false);
            foreach (var owner in survey.Active)
            {
                try
                {
                    await owner.RefreshKnownUsersAsync(survey.ServerUsers, cancellationToken).ConfigureAwait(false);
                }
                catch (PluginException ex)
                {
                    _logger.LogWarning(ex, "Could not refresh the user names of the {Owner} data", owner.Name);
                }
            }

            if (fresh.Count > 0)
            {
                _logger.LogInformation("{Records} records of {Users} users this server doesn't know are now kept as orphans; Diagnostics lists them", freshRecords, fresh.Count);
            }
        }

        var marks = _marks.Current;
        var result = new UserReconcileResult
        {
            RanUtc = _timeProvider.GetUtcNow().UtcDateTime,
            ServerUsers = survey.ServerUsers.Count,
            StoredUsers = survey.StoredUsers,
            Records = survey.Records,
            Orphans = survey.Unknown.Select(u => u with { Mark = marks.GetValueOrDefault(u.UserId) }).ToList(),
            NewlyOrphaned = fresh.Select(u => u.UserId).ToList(),
            GuardTripped = tripped,
            SkippedOwners = survey.SkippedOwners
        };
        _last = result;
        return result;
    }

    private static Dictionary<Guid, int> RecordsByUser(IEnumerable<(IUserDataOwner Owner, IReadOnlyDictionary<Guid, int> Counts)> owners)
    {
        var total = new Dictionary<Guid, int>();
        foreach (var (_, counts) in owners)
        {
            foreach (var (id, count) in counts)
            {
                if (count > 0 && !id.Equals(Guid.Empty))
                {
                    total[id] = total.GetValueOrDefault(id) + count;
                }
            }
        }

        return total;
    }

    // The owners that can be read and written now, with their counts; the others (read-only, or unreadable at the moment)
    // are named in skipped and left out of everything.
    private List<(IUserDataOwner Owner, IReadOnlyDictionary<Guid, int> Counts)> ReadOwners(List<string> skipped)
    {
        var result = new List<(IUserDataOwner, IReadOnlyDictionary<Guid, int>)>();
        var owners = _owners();

        // An owner that can't be created is left out like one that can't be read: its ids aren't counted, and marks stay.
        skipped.AddRange(_unavailable());
        foreach (var owner in owners)
        {
            try
            {
                if (owner.IsReadOnly)
                {
                    skipped.Add(owner.Name);
                    continue;
                }

                result.Add((owner, owner.CountRecordsByUser()));
            }
            catch (PluginException ex)
            {
                _logger.LogDebug(ex, "The {Owner} data can't be read now; it is left out", owner.Name);
                skipped.Add(owner.Name);
            }
        }

        return result;
    }

    private UserSurvey Survey()
    {
        var server = _serverUsers();
        var skipped = new List<string>();
        var active = ReadOwners(skipped);
        var records = RecordsByUser(active);
        var marks = _marks.Current;
        var unknown = records
            .Where(r => !server.ContainsKey(r.Key))
            .Select(r => new OrphanUser(
                r.Key,
                active.Select(o => NameOf(o.Owner, r.Key)).FirstOrDefault(n => n is not null),
                r.Value,
                marks.GetValueOrDefault(r.Key)))
            .OrderByDescending(u => u.Records)
            .ThenBy(u => KnownUsers.FormatId(u.UserId), StringComparer.Ordinal)
            .ToList();
        return new UserSurvey(server, records.Count, records.Values.Sum(), unknown, active.Select(o => o.Owner).ToList(), skipped);
    }

    private string? NameOf(IUserDataOwner owner, Guid userId)
    {
        try
        {
            return owner.KnownUsers.NameOf(userId);
        }
        catch (PluginException ex)
        {
            _logger.LogDebug(ex, "Could not read the user names of the {Owner} data", owner.Name);
            return null;
        }
    }

    private sealed record UserSurvey(
        IReadOnlyDictionary<Guid, string> ServerUsers,
        int StoredUsers,
        int Records,
        IReadOnlyList<OrphanUser> Unknown,
        IReadOnlyList<IUserDataOwner> Active,
        IReadOnlyList<string> SkippedOwners);
}
