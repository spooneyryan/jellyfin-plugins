using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// One library sync run (<c>LibrarySyncService</c> schedules it): the library lifecycle, then for every user
/// what the rules say, written through <see cref="PolicyWriter"/>; and the post-write checks. <see cref="LibrarySyncService"/>
/// schedules the runs, <c>POST /AccessRoles/Admin/Sync</c> starts one; runs never overlap.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Paused</b> (nothing written, nothing saved): <c>data/accessroles/SAFE-MODE</c> exists, <c>EnforcementEnabled</c>
/// or <c>LibrarySyncEnabled</c> is off, or the store is read-only or started empty after an unreadable file
/// (Degraded mode leaves library access as last synced). A pause and a resume are audited once.</item>
/// <item><b>Managed users</b> get the resolver's result (Access Roles owns their library access). When the native access
/// is neither the result nor what
/// Access Roles last wrote (the ledger's <c>LastWrite</c>), it drifted: <c>DriftPolicy=Reapply</c> writes the result again
/// (audited <c>PolicySynced</c>, reason <c>Drift</c>, with the access it replaced); <c>ReportOnly</c> keeps the admin's edit,
/// applies only the changes in the safe direction (ids of deleted libraries, of re-created ones nothing grants again, and of
/// ones Access Roles granted that the rules no longer give) and reports the rest, audited <c>DriftDetected</c> with what
/// was kept, applied and not applied (<see cref="DriftDecision"/>).</item>
/// <item><b>Every write says what it was planned against</b> (the native access the run read): when the user's lock finds
/// another, nothing is written (<c>NativeChanged</c>, audited <c>DriftDetected</c>; a release <c>ReleaseDropped</c>,
/// <c>BaselineChanged</c>) and the next run decides.</item>
/// <item><b>A failed save</b> (<see cref="PolicyWriter.HasFailedSave"/>) is written again, from the committed row, at the next
/// run (<c>RewriteAfterFailedSave</c>) while the user's libraries are managed or a release waits; otherwise it is
/// forgotten.</item>
/// <item><b>No libraries:</b> a result with no library is written only when the admin chose it (<c>Only</c> with nothing and
/// <c>ConfirmNoLibraries</c>), or when library deletions already left the user nothing (the write then only drops ids that
/// are gone or re-created; audited <c>UsersWithoutLibraries</c>); otherwise it is skipped (no empty result by accident).</item>
/// <item><b>LockoutGuard</b>: writes that would leave no enabled administrator with every library are skipped
/// unless the administrator's narrower access was confirmed (<see cref="RoleAssignment.ConfirmedNoFullAdminUtc"/>, or
/// <c>confirmNoFullAdmin=true</c> on a manual sync).</item>
/// <item><b>Releases</b> (<c>Storage/LedgerRelease</c>'s contract): written only while the native access still is the
/// baseline they were computed against (a <c>Restore</c> filtered against the libraries now), dropped and audited
/// otherwise, and dropped without a write when already applied.</item>
/// <item><b>The ledger:</b> an original access is captured before a user's first write if none was, and every
/// write of the rules' result records <c>LastWrite</c>.</item>
/// <item>Skips and drift are audited once per user and situation, not at every run. Once a run has started writing, what it
/// wrote is recorded and audited even when its request is cancelled (an aborted <c>POST Sync</c>, a shutdown).</item>
/// </list>
/// </remarks>
internal sealed class LibrarySync : IDisposable
{
    /// <summary>The value of <c>DriftPolicy</c> that only reports drift; anything else re-applies.</summary>
    public const string ReportOnlyPolicy = "ReportOnly";

    /// <summary>The value of <c>DriftPolicy</c> that writes the rules' result over drift (the default).</summary>
    public const string ReapplyPolicy = "Reapply";

    /// <summary>
    /// How long a user being created stays left alone when the request creating them never says it is done (a
    /// fallback; <see cref="BeginCreation"/>): the next run after it writes them, the backstop's at the latest.
    /// </summary>
    public static readonly TimeSpan CreationTimeout = TimeSpan.FromSeconds(60);

    private readonly AccessRolesStore _store;
    private readonly ILibraryCatalogSource _catalog;
    private readonly IServerUsers _users;
    private readonly PolicyWriter _writer;
    private readonly AccessRolesAuditLog _audit;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly AccessRolesPaths _paths;
    private readonly LibrarySyncStatus _status;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PreferenceMirrorSync? _mirror;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly object _removedLock = new();
    private readonly HashSet<Guid> _removedSeen = [];
    private readonly ConcurrentDictionary<Guid, string> _reported = new();
    private readonly ConcurrentDictionary<Guid, CreationMark> _creating = new();
    private string? _lastPause = "(not run yet)";

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySync"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="catalog">The library catalog.</param>
    /// <param name="users">The server's users (the user manager's objects: what the server enforces).</param>
    /// <param name="writer">The policy writer.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="paths">The data folder's paths, for SAFE-MODE.</param>
    /// <param name="status">What Diagnostics shows.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="mirror">The optional native mirror, run after each real run; <c>null</c> for none.</param>
    public LibrarySync(
        AccessRolesStore store,
        ILibraryCatalogSource catalog,
        IServerUsers users,
        PolicyWriter writer,
        AccessRolesAuditLog audit,
        IPluginConfigurationProvider configuration,
        AccessRolesPaths paths,
        LibrarySyncStatus status,
        ILogger logger,
        TimeProvider? timeProvider = null,
        PreferenceMirrorSync? mirror = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _catalog = catalog;
        _users = users;
        _writer = writer;
        _audit = audit;
        _configuration = configuration;
        _paths = paths;
        _status = status;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mirror = mirror;
    }

    /// <summary>
    /// Raised after a run or a post-write check that left a write undone (aborted, the lock busy, a failed save): the
    /// scheduler runs again soon.
    /// </summary>
    public event EventHandler? RetryWanted;

    /// <summary>
    /// Raised after a real run for everyone that wasn't refused, with the time it started (it read the settings then): the
    /// scheduler drops the run a settings save asked for before it, which this run has done.
    /// </summary>
    public event EventHandler<DateTime>? RanForEveryone;

    /// <summary>
    /// Gets the drift policy in effect: <c>ReportOnly</c>, or <c>Reapply</c> for anything else (an unknown value a newer
    /// build wrote reads as the default, PluginConfiguration's rule).
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The policy.</returns>
    public static string DriftPolicy(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.Equals(configuration.DriftPolicy?.Trim(), ReportOnlyPolicy, StringComparison.OrdinalIgnoreCase) ? ReportOnlyPolicy : ReapplyPolicy;
    }

    /// <summary>
    /// Gets the drift policy in effect now.
    /// </summary>
    /// <returns><c>Reapply</c> or <c>ReportOnly</c>.</returns>
    public string CurrentDriftPolicy() => DriftPolicy(_configuration.GetConfiguration());

    /// <summary>
    /// Gets the backstop interval in effect: <c>ReconcileIntervalMinutes</c> within 1 to 60.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The interval.</returns>
    public static TimeSpan Interval(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return TimeSpan.FromMinutes(Math.Clamp(configuration.ReconcileIntervalMinutes, 1, 60));
    }

    /// <summary>
    /// Gets why the sync may write nothing now, or <c>null</c> when it may (the order is the reasons' precedence).
    /// </summary>
    /// <returns>The pause reason (<see cref="SyncReasons"/>), or <c>null</c>.</returns>
    public string? PauseReason() => PauseReason(_configuration.GetConfiguration(), _paths, _store);

    /// <summary>
    /// Gets why the library sync may write nothing, from the settings, the SAFE-MODE file and the store.
    /// Every store status but <c>Ok</c>, <c>Migrated</c> and <c>RecoveredFromBackup</c> pauses it (fail closed): the
    /// read-only ones (<c>ReadOnlyNewerVersion</c>, <c>ReadOnlyMigrationFailed</c>, <c>ReadOnlyDataMissing</c>,
    /// <c>ReadOnlyDataUnreadable</c>),
    /// <c>RecoveredEmpty</c> (Degraded mode), and <c>RestoredFromSnapshot</c> until an admin confirms (<see cref="RestoreHold"/>).
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <returns>The pause reason (<see cref="SyncReasons"/>), or <c>null</c> when the sync may write.</returns>
    public static string? PauseReason(PluginConfiguration configuration, AccessRolesPaths paths, AccessRolesStore store)
        => PauseReason(configuration, paths, store, confirmed: null);

    /// <summary>
    /// Gets why the optional native mirror (<see cref="PreferenceMirrorSync"/>) may write nothing now: the sync's
    /// reasons but <c>LibrarySyncDisabled</c>, since the mirror's flag isn't library access.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <returns>The pause reason, or <c>null</c>.</returns>
    public static string? MirrorPauseReason(PluginConfiguration configuration, AccessRolesPaths paths, AccessRolesStore store)
        => PauseReason(configuration, paths, store, confirmed: null, ignoreLibrarySync: true);

    // The pause, with the hold a confirmation confirmed lifted (its own hold only: another hold, or a restore in progress,
    // still pauses; since 1.0.1).
    private static string? PauseReason(PluginConfiguration configuration, AccessRolesPaths paths, AccessRolesStore store, RestoreHold.HoldIdentity? confirmed, bool ignoreLibrarySync = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        if (File.Exists(paths.SafeModeFile))
        {
            return SyncReasons.SafeMode;
        }

        if (!configuration.EnforcementEnabled)
        {
            return SyncReasons.EnforcementDisabled;
        }

        if (!configuration.LibrarySyncEnabled && !ignoreLibrarySync)
        {
            return SyncReasons.LibrarySyncDisabled;
        }

        if (store.IsReadOnly)
        {
            return SyncReasons.StoreReadOnly;
        }

        return store.Status switch
        {
            StoreStatus.RecoveredEmpty when store.Degraded.Current is not null => SyncReasons.StoreDegraded,
            _ when RestoreHold.IsHeld(paths, store, confirmed) => SyncReasons.StoreRestored,
            _ => null
        };
    }

    /// <summary>
    /// Gets whether a confirmation would be refused for want of a dry run: the sync is held after a restore
    /// (<see cref="RestoreHold"/>) and no admin made its dry run since the hold began.
    /// </summary>
    /// <returns><c>true</c> when <c>confirmRestore=true</c> must wait for a dry run.</returns>
    public bool RestoreNeedsDryRun() => RestoreHold.IsHeld(_paths, _store) && !RestoreHold.DryRunSeen(_paths, _store);

    /// <summary>
    /// Gets whether the sync is held after a restore (<see cref="RestoreHold"/>), whatever else pauses it.
    /// </summary>
    /// <returns><c>true</c> while held.</returns>
    public bool RestoreHeld() => RestoreHold.IsHeld(_paths, _store);

    /// <summary>
    /// Records a library removal the library structure filter saw, for the next run: it is deleted, not renamed.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    public void RecordRemoval(Guid libraryId)
    {
        lock (_removedLock)
        {
            _removedSeen.Add(libraryId);
        }
    }

    /// <summary>
    /// Marks a user the server is creating: until <see cref="EndCreation"/>, or
    /// <see cref="CreationTimeout"/> as a fallback, no run (the backstop, a manual sync, a store change, another user's
    /// sign-in follow-ups), no post-write check and no other request's sign-in correction writes them. On 10.11.5
    /// <c>CreateUserByName</c> saves the password after <c>UserCreatedEventArgs</c>, through <c>UpdateUserAsync</c> on the
    /// entity it created (<c>v10.11.5:Jellyfin.Api/Controllers/UserController.cs:543-549</c>); a write of ours in between
    /// moves the row version on and that save fails (<c>DbUpdateConcurrencyException</c>, 500): the user is left without a
    /// password, and an empty one signs in. The request that creates the user may correct them at its own sign-in (an LDAP
    /// or SSO plugin's first sign-in), its action being done by then.
    /// </summary>
    /// <param name="userId">The new user.</param>
    /// <param name="requestId">The creating request's <c>TraceIdentifier</c>, or <c>null</c> outside a request.</param>
    public void BeginCreation(Guid userId, string? requestId)
        => _creating[userId] = new CreationMark(requestId, Now() + CreationTimeout, Settling: false);

    /// <summary>
    /// Ends a user's creation (<see cref="BeginCreation"/>): the request is done with them. For <paramref name="settle"/>
    /// longer the runs still leave them alone while their own sign-in may be corrected: a sign-in right after the creation
    /// must not meet a run's write (the 10.11.5 sign-in race: 500, and the stale object enforced); the first
    /// run is asked for then.
    /// </summary>
    /// <param name="userId">The new user.</param>
    /// <param name="settle">How long the runs still leave them alone; zero for not at all.</param>
    public void EndCreation(Guid userId, TimeSpan settle)
    {
        if (settle <= TimeSpan.Zero)
        {
            _creating.TryRemove(userId, out _);
            return;
        }

        var until = Now() + settle;
        _creating.AddOrUpdate(userId, _ => new CreationMark(null, until, Settling: true), (_, mark) => mark with { UntilUtc = until, Settling = true });
    }

    /// <summary>
    /// Gets whether the runs leave a user alone because they are being created, or have just been (<see cref="EndCreation"/>).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns><c>true</c> while they are.</returns>
    public bool IsBeingCreated(Guid userId) => Creation(userId) is not null;

    /// <summary>
    /// Records a library rename the library structure filter saw, unless the sync is paused.
    /// </summary>
    /// <param name="oldId">The library's id before the rename.</param>
    /// <param name="oldName">Its name before.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    public async Task RecordRenameAsync(Guid oldId, string oldName, string newName, CancellationToken cancellationToken)
    {
        if (PauseReason() is not null)
        {
            return;
        }

        var now = Now();
        await _store.UpdateAsync(d => LibraryLifecycle.RecordRename(d, oldId, oldName, newName, now), cancellationToken, notify: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the state the rules resolve against now, as the next run would see it: the stored document with the library
    /// lifecycle applied (not saved) and its catalog, the listed libraries plus the ones a rename waits for and without the
    /// withheld ones (<see cref="LifecycleResult.Withheld"/>). The roles API's previews and the user policy
    /// hooks resolve against it, so they show and write no grant the sync wouldn't.
    /// </summary>
    /// <returns>The lifecycle's result; its <c>Document</c> and <c>Catalog</c> are what to resolve with.</returns>
    public LifecycleResult ResolutionView() => ResolutionView(_store.Current, _catalog.Read());

    /// <summary>
    /// Gets the state the rules resolve against for a document and the libraries listed now (<see cref="ResolutionView()"/>).
    /// </summary>
    /// <param name="document">The document (the stored one, or a proposed one).</param>
    /// <param name="listed">The libraries the server lists now.</param>
    /// <returns>The lifecycle's result.</returns>
    public LifecycleResult ResolutionView(AccessRolesDocument document, LibraryCatalog listed)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(listed);
        return LibraryLifecycle.Reconcile(document, listed, SeenRemoved(), Now());
    }

    /// <summary>
    /// Decides what an admin's (or an API key's) full-policy save of a user gets in its library fields (the user
    /// policy pre-merge): exactly what the next run would write for the user if their native access
    /// were the posted one, with the post's <c>IsAdministrator</c> and <c>IsDisabled</c>. So <c>Reapply</c> merges the
    /// rules' result (the admin's library edit is replaced: <c>ManualEditOverridden</c>); <c>ReportOnly</c> keeps the edit and
    /// merges only the safe-direction removals (<see cref="DriftDecision"/>, which the sync
    /// would apply a moment later); a user not managed, in sync, refused (no libraries, the lockout guard) or paused keeps the
    /// post as it is. Nothing is written or saved here. Call it under the user's policy lock.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="posted">The posted library fields.</param>
    /// <param name="postedAdministrator">The posted <c>IsAdministrator</c>.</param>
    /// <param name="postedDisabled">The posted <c>IsDisabled</c>.</param>
    /// <returns>The decision.</returns>
    public PreMergeDecision DecidePreMerge(Guid userId, LibraryFields posted, bool postedAdministrator, bool postedDisabled)
    {
        ArgumentNullException.ThrowIfNull(posted);
        var users = _users.GetAll();
        var user = users.FirstOrDefault(u => u.Id.Equals(userId));
        var before = user?.Native.Libraries ?? posted;
        if (PauseReason() is { } pause)
        {
            return PreMergeDecision.None(before, posted, pause);
        }

        if (user is null)
        {
            return PreMergeDecision.None(before, posted, SyncReasons.UserNotFound);
        }

        if (EffectiveRulesResolver.FindAssignment(_store.Current, userId) is null)
        {
            return PreMergeDecision.None(before, posted, AccessSources.NotManaged);
        }

        var listed = _catalog.Read();
        var view = ResolutionView(_store.Current, listed);
        var asPosted = user with
        {
            IsAdministrator = postedAdministrator,
            IsDisabled = postedDisabled,
            Native = user.Native with { EnableAllFolders = posted.EnableAllFolders, EnabledFolders = posted.EnabledFolders }
        };
        var reapply = !string.Equals(CurrentDriftPolicy(), ReportOnlyPolicy, StringComparison.Ordinal);
        var plan = Plan(view.Document, view.Catalog, listed, [asPosted], reapply, new HashSet<Guid>(), []).FirstOrDefault();
        if (plan is null)
        {
            return PreMergeDecision.None(before, posted, EffectiveRulesResolver.Resolve(view.Document, view.Catalog, asPosted).Libraries.Writes ? "InSync" : AccessSources.NotManaged);
        }

        var merge = plan.Kind switch
        {
            SyncActionKind.Write => plan.Target,
            SyncActionKind.Drift => plan.DriftDecision?.Write,
            _ => null
        };
        if (plan.Kind is not (SyncActionKind.Write or SyncActionKind.Drift))
        {
            return PreMergeDecision.None(before, posted, plan.Reason);
        }

        if (merge is not null)
        {
            // The lockout guard over the save as posted: an administrator's every-library access isn't narrowed unconfirmed.
            var others = users.Select(u => u.Id.Equals(userId) ? asPosted : u).ToList();
            var refused = LockoutGuard.Refused(listed, others, new Dictionary<Guid, LibraryFields> { [userId] = merge }, id => Confirmed(view.Document, id));
            if (refused.Contains(userId))
            {
                return PreMergeDecision.None(before, posted, SyncReasons.NoFullAdministrator);
            }
        }

        // The pause again, after the plan read the document, as on the sign-in path: a restore
        // that landed since the first look isn't merged into the admin's save.
        if (PauseReason() is { } late)
        {
            return PreMergeDecision.None(before, posted, late);
        }

        return new PreMergeDecision(plan, merge, before, posted, merge is null ? plan.Reason : null);
    }

    /// <summary>
    /// Records an admin's policy save the pre-merge decided on, once the core saved it (2xx): the ledger's original access
    /// if none was captured, the last write for a merged rules' result, the writer's post-write check and loop guard
    /// (<see cref="PolicyWriter.RecordAdminSave"/>), and the audit line, actor the admin: <c>ManualEditOverridden</c> when
    /// the save changed the library access and the rules' result replaced it, <c>PolicySynced</c> (trigger
    /// <c>PolicySave</c>) when the save only carried a pending write, <c>DriftDetected</c> under <c>ReportOnly</c> (once
    /// per situation, as the sync's). Never throws for a failed audit write (the audit log logs it).
    /// </summary>
    /// <param name="decision">The decision.</param>
    /// <param name="actor">The admin or API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    public async Task RecordPreMergeAsync(PreMergeDecision decision, AuditActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(actor);
        if (decision.Plan is not { } plan)
        {
            return;
        }

        var user = plan.User;
        var listed = _catalog.Read();
        if (decision.Merge is { } merged)
        {
            if (!_store.Current.Ledger.ContainsKey(user.Id))
            {
                var now = Now();
                var native = _users.Find(user.Id)?.Native ?? user.Native;
                await _store.UpdateAsync(
                    d => d.Ledger.ContainsKey(user.Id) ? d : d with { Ledger = new Dictionary<Guid, LedgerEntry>(d.Ledger) { [user.Id] = Capture(decision.Before, native.EnableUserPreferenceAccess, now) } },
                    cancellationToken,
                    notify: false).ConfigureAwait(false);
            }

            if (plan.Kind == SyncActionKind.Write)
            {
                await RecordLastWriteAsync(user.Id, merged, null, cancellationToken).ConfigureAwait(false);
            }

            _writer.RecordAdminSave(user.Id, merged, decision.Before);
        }

        var outcome = decision.Merge is { } written && !written.Same(decision.Before) ? PolicyWriteOutcome.Written : PolicyWriteOutcome.Unchanged;
        var done = plan with { Outcome = decision.Merge is null ? null : outcome };
        AuditRecord line;
        if (plan.Kind == SyncActionKind.Write && decision.Edited)
        {
            _reported.TryRemove(user.Id, out _);
            line = LibrarySyncAudit.Overridden(actor, done, decision.Before, listed);
        }
        else
        {
            if (plan.Kind == SyncActionKind.Drift)
            {
                // Once per situation, as the sync audits it: the next run finds the same and writes no second line.
                var signature = Signature(done);
                if (_reported.TryGetValue(user.Id, out var last) && string.Equals(last, signature, StringComparison.Ordinal))
                {
                    return;
                }

                _reported[user.Id] = signature;
            }
            else
            {
                _reported.TryRemove(user.Id, out _);
            }

            // Native is the posted access: what a ReportOnly decision keeps and applies is relative to it (and for a pending
            // write the save carried, it is the access enforced before).
            line = LibrarySyncAudit.User(actor, SyncTrigger.PolicySave, done, listed);
        }

        _status.Hook(LibrarySyncStatus.PreMerged);
        if (line.Action == AuditActions.ManualEditOverridden)
        {
            _status.Hook(LibrarySyncStatus.ManualEditsOverridden);
        }

        await _audit.AppendAsync([line], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Audits a pre-merge that failed (fail open: the admin's post went through unchanged, <c>DriftDetected</c>,
    /// the <i>Filter errors</i> count; the next run applies the rules' result).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="actor">The admin or API key.</param>
    /// <param name="posted">The posted library fields.</param>
    /// <param name="error">The error.</param>
    /// <returns>A task that completes when it is written.</returns>
    public async Task AuditHookFailedAsync(Guid userId, AuditActor actor, LibraryFields posted, string error)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(posted);
        _status.Hook(LibrarySyncStatus.FilterErrors);
        var listed = LibraryCatalog.Empty;
        try
        {
            listed = _catalog.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "No library names for the audit line of a failed pre-merge");
        }

        await _audit.AppendAsync([LibrarySyncAudit.HookFailed(actor, userId, _users.Find(userId)?.Name, posted, error, listed)], CancellationToken.None).ConfigureAwait(false);
        RetryWanted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs the library sync for one user now (the sign-in hook): the same plan,
    /// lockout guard, write, ledger and audit as a run, for a user whose libraries are managed. It takes the user's policy
    /// lock only (at most <paramref name="wait"/>), never the run lock: the write says what it was planned
    /// against, so a run writing the same user meanwhile either finds it written or writes nothing, and a long run (hundreds of
    /// users) or a busy database no longer turns a sign-in whose rules just narrowed into a refusal. When the user's access
    /// changed between the plan and the write (a run wrote them), it is planned and written once more. The library lifecycle
    /// is resolved, not saved (the runs save it); a pending release is left to the runs.
    /// </summary>
    /// <remarks>
    /// While the sync is paused nothing is written. An admin's pause (SAFE-MODE, enforcement or the library sync switched
    /// off) leaves the user alone ("library access untouched"); during the store's pauses (the restore
    /// hold, a read-only store, Degraded mode) the answer carries the ledger's last write, so the hook refuses a
    /// non-administrator whose access is more than that (fail closed; a user never written has none to
    /// compare, default roles given during a hold among them, and stays as they are until the admin confirms).
    /// </remarks>
    /// <param name="userId">The user.</param>
    /// <param name="trigger">Why (<see cref="SyncTrigger.SignIn"/>).</param>
    /// <param name="wait">How long to wait for the user's policy lock.</param>
    /// <param name="requestId">The sign-in request's <c>TraceIdentifier</c>: the request that creates a user (an LDAP or SSO
    /// plugin's first sign-in) may correct them, its action being done; another request's sign-in of a user being created
    /// writes nothing (<see cref="BeginCreation"/>).</param>
    /// <param name="cancellationToken">Cancels the wait and the write's waits.</param>
    /// <returns>What was done.</returns>
    public async Task<UserCorrection> CorrectUserAsync(Guid userId, SyncTrigger trigger, TimeSpan wait, string? requestId, CancellationToken cancellationToken)
    {
        if (PauseReason() is { } pause)
        {
            return Held(userId, pause);
        }

        // The fast path: a user without an assignment gets nothing.
        if (EffectiveRulesResolver.FindAssignment(_store.Current, userId) is null)
        {
            return UserCorrection.NotManaged;
        }

        for (var attempt = 0; ; attempt++)
        {
            var planned = PlanUser(userId);
            if (planned is null)
            {
                return UserCorrection.NotManaged;
            }

            if (planned.Plan is not { } plan)
            {
                foreach (var (synced, fields) in planned.InSync)
                {
                    await RecordLastWriteAsync(synced.Id, fields, synced, CancellationToken.None).ConfigureAwait(false);
                }

                return new UserCorrection(true, null);
            }

            // A user another request is still creating isn't written (BeginCreation): the sign-in is refused if their access
            // is more than the rules give (fail closed), and the runs after the creation write them.
            if (Creation(userId) is { Settling: false } creation && (creation.RequestId is null || !string.Equals(creation.RequestId, requestId, StringComparison.Ordinal)))
            {
                return new UserCorrection(true, plan) { BeingCreated = true };
            }

            // The pause again, after the plan read the document: a restore writes its hold
            // before it saves, so a hold not there now means the plan was made from the document before any restore, and a
            // hold there now means the plan may be the restored access, which isn't written.
            if (PauseReason() is { } late)
            {
                return Held(userId, late);
            }

            var result = await ExecuteAsync(plan, cancellationToken, wait).ConfigureAwait(false);
            if (attempt == 0 && result is { Kind: SyncActionKind.Skip, Reason: SyncReasons.NativeChanged })
            {
                // A run (or an admin's save) wrote the user since the plan: planned again from what is enforced now.
                continue;
            }

            await AuditAsync(new SyncRequest(trigger, AuditActor.System), [result], planned.Listed, CancellationToken.None).ConfigureAwait(false);
            _status.RecordUser(result, Now());
            if (result.Kind == SyncActionKind.Skip && result.Reason is SyncReasons.WriteAborted or SyncReasons.LockTimeout or SyncReasons.WriteFailed or SyncReasons.NativeChanged or SyncReasons.ConcurrentSave)
            {
                RetryWanted?.Invoke(this, EventArgs.Empty);
            }

            return new UserCorrection(true, result);
        }
    }

    // A sign-in while the sync is paused: nothing written. The store's pauses carry the ledger's last write for the hook's
    // refusal; an admin's pause, or a user Access Roles never wrote, is left alone.
    private UserCorrection Held(Guid userId, string pause)
    {
        if (!SyncReasons.IsStoreCaused(pause)
            || !_store.Current.Ledger.TryGetValue(userId, out var entry)
            || entry.LastWrite is not { } last)
        {
            return UserCorrection.NotManaged;
        }

        return new UserCorrection(true, null) { HeldBecause = pause, LastWrite = LibraryFields.Of(last.EnableAllFolders, last.EnabledFolders) };
    }

    // A run's plan for one user, with the lockout guard over every user; null when their libraries aren't written (not
    // managed, gone), a null Plan when they are in sync or only a release waits (left to the runs).
    private UserPlan? PlanUser(Guid userId)
    {
        if (_users.Find(userId) is not { } user)
        {
            return null;
        }

        var listed = _catalog.Read();
        var view = ResolutionView(_store.Current, listed);
        if (!EffectiveRulesResolver.Resolve(view.Document, view.Catalog, user).Libraries.Writes)
        {
            return null;
        }

        var reapply = !string.Equals(CurrentDriftPolicy(), ReportOnlyPolicy, StringComparison.Ordinal);
        var inSync = new List<(ServerUser User, LibraryFields Fields)>();
        var plan = Plan(view.Document, view.Catalog, listed, [user], reapply, _writer.FailedSaves.ToHashSet(), inSync).FirstOrDefault();
        if (plan is null || plan.Kind is SyncActionKind.Release or SyncActionKind.DropRelease)
        {
            return new UserPlan(null, listed, inSync);
        }

        var target = plan.Kind == SyncActionKind.Drift ? plan.DriftDecision?.Write : plan.Target;
        if (target is not null && plan.Kind is SyncActionKind.Write or SyncActionKind.Drift
            && LockoutGuard.Refused(listed, _users.GetAll(), new Dictionary<Guid, LibraryFields> { [userId] = target }, id => Confirmed(view.Document, id)).Contains(userId))
        {
            plan = plan with { Kind = SyncActionKind.Skip, Reason = SyncReasons.NoFullAdministrator };
        }

        return new UserPlan(plan, listed, inSync);
    }

    /// <summary>
    /// Gets the library fields the server enforces for a user now (on 10.11.5 the user manager's cached object).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The fields, or <c>null</c> when the user is gone or can't be read.</returns>
    public LibraryFields? EnforcedNow(Guid userId) => _writer.Read(userId);

    /// <summary>
    /// Gets whether the store holds an assignment for a user, without throwing: <c>null</c> when the store can't be read.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>Whether the user is assigned, or <c>null</c> when unknown.</returns>
    public bool? IsAssigned(Guid userId)
    {
        try
        {
            return EffectiveRulesResolver.FindAssignment(_store.Current, userId) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't read its data file to tell whether user {UserId} is managed", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets the library catalog the server lists now, or an empty one when it can't be read.
    /// </summary>
    /// <returns>The catalog.</returns>
    public LibraryCatalog ListedOrEmpty()
    {
        try
        {
            return _catalog.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't list the libraries");
            return LibraryCatalog.Empty;
        }
    }

    /// <summary>
    /// Records a sign-in the hook refused (fail closed): counted, and audited <c>SignInRefused</c> by the plugin.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="correction">What the hook found.</param>
    /// <param name="native">The access the server enforces for the user, if read.</param>
    /// <param name="error">The error, when the hook threw.</param>
    /// <param name="tokenRevoked">Whether the token the core issued was revoked.</param>
    /// <returns>A task that completes when it is written.</returns>
    public async Task AuditSignInRefusedAsync(Guid userId, string? userName, UserCorrection? correction, LibraryFields? native, string? error, bool tokenRevoked)
    {
        _status.Hook(LibrarySyncStatus.SignInsRefused);
        var listed = LibraryCatalog.Empty;
        try
        {
            listed = _catalog.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "No library names for the audit line of a refused sign-in");
        }

        await _audit.AppendAsync([LibrarySyncAudit.SignInRefused(userId, userName, correction, native, error, tokenRevoked, listed)], CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the sync once.
    /// </summary>
    /// <param name="request">What to do.</param>
    /// <param name="cancellationToken">Cancels the run between writes.</param>
    /// <returns>What it found and did.</returns>
    public async Task<SyncReport> RunAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = Now();
            var report = await RunLockedAsync(request, cancellationToken).ConfigureAwait(false);

            // The optional native mirror after the library pass (re-checked after every write, so a library write
            // from a stale row that put the flag back is put right in the same run). A dry run writes nothing.
            if (!request.DryRun && _mirror is not null)
            {
                var mirrored = await _mirror.RunAsync(request.Actor, request.UserId, cancellationToken).ConfigureAwait(false);
                if (mirrored.Any(m => m.Action == MirrorAction.Failed || m.Write is { Outcome: Policy.PolicyWriteOutcome.Written, Holds: false }))
                {
                    RetryWanted?.Invoke(this, EventArgs.Empty);
                }
            }

            _status.Record(report);
            if (!request.DryRun && request.UserId is null)
            {
                RanForEveryone?.Invoke(this, started);
            }

            return report;
        }
        finally
        {
            _run.Release();
        }
    }

    /// <summary>
    /// Runs the post-write checks that are due ("verify after writing"). A user whose access from before the write
    /// is enforced again is either the 10.11.5 sign-in race (measured on a 10.11.5 sandbox: the cache holds the old object,
    /// the database the
    /// write) or an edit back to the old access: the committed row tells them apart. For the race, the committed row is
    /// written back as it is (<see cref="PolicyWriter.ResyncAsync"/>), which can't overwrite anything saved, whether the user
    /// is still managed or not, and audited <c>PolicySynced</c> with the reason <c>RevertedAfterWrite</c>; an edit is left to
    /// the next run's rules. When the committed row can't be read, the write is made again only while the user's libraries are
    /// still written by Access Roles and the rules still give what was written (otherwise the check would write over a user
    /// who left management meanwhile). A revert after that, or other access found, is left to the next reconcile.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the run lock.</param>
    /// <returns><c>true</c> when a reconcile should follow soon.</returns>
    public async Task<bool> VerifyDueAsync(CancellationToken cancellationToken)
    {
        var due = _writer.TakeDue(Now());
        if (due.Count == 0)
        {
            return false;
        }

        var reconcile = false;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = _catalog.Read();
            foreach (var check in due)
            {
                if (IsBeingCreated(check.UserId))
                {
                    // Nothing writes a user being created (BeginCreation): the runs after the creation decide.
                    reconcile = true;
                    continue;
                }

                var result = _writer.Verify(check);
                switch (result.Outcome)
                {
                    case VerificationOutcome.Reverted when check.Attempt == 0 && PauseReason() is null:
                        var write = await RewriteRevertedAsync(check, result, catalog, cancellationToken).ConfigureAwait(false);
                        if (write is null)
                        {
                            // An edit back to the old access, or the rules changed since: the next run decides.
                            reconcile = true;
                            break;
                        }

                        // From here on the write happened (or not) whatever the token says: its line is written.
                        var rewrote = write.Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Resynced;
                        _status.Verified(result, rewritten: rewrote);
                        reconcile |= !rewrote;
                        await _audit.AppendAsync([LibrarySyncAudit.Reverted(result, write, _users.Find(check.UserId)?.Name, catalog)], CancellationToken.None).ConfigureAwait(false);
                        break;
                    case VerificationOutcome.Reverted:
                        _status.Verified(result, rewritten: false);
                        reconcile = true;
                        break;
                    case VerificationOutcome.Changed:
                        reconcile = true;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Access Roles' post-write check failed");
            _status.Failed(ex.Message, Now());
            reconcile = true;
        }
        finally
        {
            _run.Release();
        }

        return reconcile;
    }

    // A drifted user's applied removals count as their native access afterwards, so the next run's unchanged drift isn't
    // audited again.
    private static string Signature(SyncUserResult result)
    {
        var native = result is { Kind: SyncActionKind.Drift, Wrote: true, DriftDecision.Write: { } written } ? written : result.Native;
        return string.Join(
            "|",
            result.Kind.ToString(),
            result.Reason,
            native.EnableAllFolders ? "all" : string.Join(",", native.EnabledFolders.Order().Select(id => id.ToString("N", CultureInfo.InvariantCulture))),
            result.Target is null ? "-" : result.Target.EnableAllFolders ? "all" : string.Join(",", result.Target.EnabledFolders.Order().Select(id => id.ToString("N", CultureInfo.InvariantCulture))));
    }

    // The write again after a post-write check found the old access enforced; null when nothing is written.
    private async Task<PolicyWriteResult?> RewriteRevertedAsync(PendingVerification check, VerificationResult result, LibraryCatalog listed, CancellationToken cancellationToken)
    {
        var found = result.Found ?? check.Before;
        var heal = await _writer.ResyncAsync(check.UserId, found, attempt: 1, cancellationToken).ConfigureAwait(false);
        if (!heal.CommittedUnavailable)
        {
            if (heal.Outcome is PolicyWriteOutcome.Unchanged or PolicyWriteOutcome.Aborted)
            {
                // The database holds what the cache shows (an edit someone saved, not a stale cache), or the cache changed
                // since the check.
                _logger.LogInformation("Access Roles didn't write the library access of user {UserId} again: the access found after its write is saved, not a stale cache", check.UserId);
                return null;
            }

            return heal;
        }

        // Without the committed row: write again only while the user's libraries are still written by Access Roles and the
        // rules still give what was written.
        if (_users.Find(check.UserId) is not { } user)
        {
            return null;
        }

        var lifecycle = LibraryLifecycle.Reconcile(_store.Current, listed, SeenRemoved(), Now());
        var libraries = EffectiveRulesResolver.Resolve(lifecycle.Document, lifecycle.Catalog, user).Libraries;
        if (!libraries.Writes || !check.Target.Same(libraries.EnableAllFolders, libraries.EnabledFolders))
        {
            _logger.LogInformation("Access Roles didn't write the library access of user {UserId} again: their libraries are no longer managed, or the rules give other access now", check.UserId);
            return null;
        }

        var write = await _writer.WriteAsync(check.UserId, check.Target, cancellationToken, attempt: 1, expected: found).ConfigureAwait(false);
        if (write.Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Unchanged)
        {
            await RecordLastWriteAsync(check.UserId, check.Target, null, CancellationToken.None).ConfigureAwait(false);
        }

        return write;
    }

    private static LedgerEntry Capture(LibraryFields native, bool preferenceAccess, DateTime now) => new()
    {
        OriginalEnableAllFolders = native.EnableAllFolders,
        OriginalEnabledFolders = native.EnableAllFolders ? [] : native.EnabledFolders,
        OriginalEnableUserPreferenceAccess = preferenceAccess,
        CapturedUtc = now
    };

    private static List<SyncUserResult> Plan(
        AccessRolesDocument document,
        LibraryCatalog catalog,
        LibraryCatalog listed,
        IReadOnlyList<ServerUser> users,
        bool reapply,
        IReadOnlySet<Guid> failedSaves,
        List<(ServerUser User, LibraryFields Fields)> inSync)
    {
        var plan = new List<SyncUserResult>();
        foreach (var user in users)
        {
            var effective = EffectiveRulesResolver.Resolve(document, catalog, user);
            var native = user.Native.Libraries;
            var libraries = effective.Libraries;
            if (libraries.Writes)
            {
                var target = LibraryFields.Of(libraries.EnableAllFolders, libraries.EnabledFolders);
                if (target.Same(native) && failedSaves.Contains(user.Id))
                {
                    plan.Add(new SyncUserResult(user, SyncActionKind.Write, SyncReasons.RewriteAfterFailedSave, native, target) { Effective = effective, Force = true });
                    continue;
                }

                if (target.Same(native))
                {
                    if (effective.LastWrite is not { } last || !target.Same(last.EnableAllFolders, last.EnabledFolders))
                    {
                        inSync.Add((user, target));
                    }

                    continue;
                }

                if (libraries.NoLibraries && !libraries.NoLibrariesConfirmed)
                {
                    // Written only when the user has nothing left to lose: every library their native access opens is gone or
                    // was created again since (a new library), or the user was given the default roles at their creation and
                    // never written since (their access is the server's default for a new user, not an admin's; fail
                    // closed). Otherwise the admin decides.
                    var keeps = listed.Expand(native.EnableAllFolders, native.EnabledFolders).Where(id => !EffectiveRulesResolver.IsRecreated(document, listed, id)).ToList();
                    var newUser = document.Ledger.TryGetValue(user.Id, out var created) && created.DefaultRolesUtc is not null && created.LastWrite is null;
                    plan.Add(keeps.Count > 0 && !newUser
                        ? new SyncUserResult(user, SyncActionKind.Skip, SyncReasons.NoLibraries, native, target) { Effective = effective }
                        : new SyncUserResult(user, SyncActionKind.Write, keeps.Count > 0 ? SyncReasons.NewUserNoLibraries : SyncReasons.NoLibrariesLeft, native, target) { Effective = effective });
                    continue;
                }

                if (effective.Drift)
                {
                    // ReportOnly keeps the admin's edit and applies only the changes in the safe direction (DriftDecision).
                    plan.Add(reapply
                        ? new SyncUserResult(user, SyncActionKind.Write, SyncReasons.Drift, native, target) { Effective = effective }
                        : new SyncUserResult(user, SyncActionKind.Drift, SyncReasons.ReportOnly, native, target)
                        {
                            Effective = effective,
                            DriftDecision = DriftDecision.Decide(document, catalog, listed, native, target, effective.LastWrite!, libraries.NoLibrariesConfirmed)
                        });
                    continue;
                }

                plan.Add(new SyncUserResult(user, SyncActionKind.Write, effective.LastWrite is null ? SyncReasons.FirstWrite : SyncReasons.RulesChanged, native, target) { Effective = effective });
            }
            else if (effective.PendingRelease is { } release && document.Ledger.TryGetValue(user.Id, out var entry))
            {
                if (release.Choice.Value is not { } choice || choice == AccessRolesUnmanageChoice.Keep)
                {
                    plan.Add(new SyncUserResult(user, SyncActionKind.DropRelease, SyncReasons.UnknownChoice, native, null) { Effective = effective });
                    continue;
                }

                // A Restore is filtered again against the libraries now: deleted ones dropped, re-created ones left out.
                // A release whose save failed shows in the cache already, not in the database: written again, from the
                // committed row.
                var target = choice == AccessRolesUnmanageChoice.GrantAll || entry.OriginalEnableAllFolders
                    ? LibraryFields.All
                    : LibraryFields.Of(false, EffectiveRulesResolver.OriginalLibraries(document, catalog, entry, out _));
                plan.Add(target.Same(native) && failedSaves.Contains(user.Id)
                    ? new SyncUserResult(user, SyncActionKind.Release, choice.ToString(), native, target) { Effective = effective, Force = true }
                    : target.Same(native)
                    ? new SyncUserResult(user, SyncActionKind.DropRelease, SyncReasons.AlreadyApplied, native, null) { Effective = effective }
                    : effective.PendingReleaseBaselineMatches
                        ? new SyncUserResult(user, SyncActionKind.Release, choice.ToString(), native, target) { Effective = effective }
                        : new SyncUserResult(user, SyncActionKind.DropRelease, release.Baseline is null ? SyncReasons.NoBaseline : SyncReasons.BaselineChanged, native, target) { Effective = effective });
            }
        }

        return plan;
    }

    private static AccessRolesDocument DropRelease(AccessRolesDocument document, Guid userId, LedgerRelease? planned)
    {
        if (!document.Ledger.TryGetValue(userId, out var entry) || entry.Release is not { } release || (planned is not null && release.RequestedUtc != planned.RequestedUtc))
        {
            return document;
        }

        // The entry stays while the user has an assignment (a pause), without a last write: the access is the release's or
        // the admin's now, not Access Roles'.
        var ledger = document.Ledger.ToDictionary(p => p.Key, p => p.Value);
        if (document.Assignments.Any(a => a.UserId.Equals(userId)))
        {
            ledger[userId] = entry with { Release = null, LastWrite = null };
        }
        else
        {
            ledger.Remove(userId);
        }

        return document with { Ledger = ledger, KnownUsers = ledger.ContainsKey(userId) || document.Assignments.Any(a => a.UserId.Equals(userId)) ? document.KnownUsers : document.KnownUsers.Without(userId) };
    }

    private async Task<SyncReport> RunLockedAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        var started = Now();
        var configuration = _configuration.GetConfiguration();

        // A store restored from a snapshot at load holds the sync until an admin confirms it (RestoreHold): the hold file is
        // written at the first look, so a restart keeps it; a real run with confirmRestore=true lifts it, once a dry run was
        // made while held ("until the admin has seen the sync dry run"), and only once the run's
        // plan is the one the admin confirmed (ifPlan): until then the run is planned as it would be with
        // the hold lifted, and a refused confirmation leaves it held. The confirmation is of one hold (since 1.0.1):
        // the one there was when its dry run was found seen, read before and after, so a restore that
        // began meanwhile (whose Begin forgets the dry run before it writes its hold) is never confirmed by it.
        RestoreHold.Ensure(_paths, _store, started, _logger);
        RestoreHold.HoldIdentity? releasing = null;
        var scope = request.UserId;
        if (request.DryRun && scope is null)
        {
            // A dry run for one user shows the admin only them: it isn't the dry run the hold waits for.
            RestoreHold.RecordDryRun(_paths, _store, started, _logger);
        }
        else if (request.ConfirmRestore && RestoreHold.Current(_paths, _store) is { } hold)
        {
            if (!RestoreHold.DryRunSeen(_paths, _store))
            {
                _logger.LogWarning("Access Roles' library sync stays held: confirmRestore needs a dry run (POST /AccessRoles/Admin/Sync?dryRun=true) first");
            }
            else if (!hold.Equals(RestoreHold.Current(_paths, _store)))
            {
                throw AnotherHold();
            }
            else
            {
                releasing = hold;
            }
        }

        var pause = PauseReason(configuration, _paths, _store, releasing);
        if (releasing is not null && string.Equals(pause, SyncReasons.StoreRestored, StringComparison.Ordinal))
        {
            // Held still, with the confirmed hold lifted: another hold is there, or a restore is in progress.
            throw AnotherHold();
        }

        var listed = _catalog.Read();
        var removed = SeenRemoved();
        var users = _users.GetAll();
        var reapply = !string.Equals(DriftPolicy(configuration), ReportOnlyPolicy, StringComparison.Ordinal) || request.ReapplyDrift;
        var inSync = new List<(ServerUser User, LibraryFields Fields)>();
        var failedSaves = _writer.FailedSaves.ToHashSet();

        // A user being created is left alone until the request creating them is done (BeginCreation):
        // whatever started this run, a write of theirs now would fail the core's own save of their password.
        var creating = users.Where(u => IsBeingCreated(u.Id)).Select(u => u.Id).ToHashSet();
        var eligible = creating.Count == 0 ? users : [.. users.Where(u => !creating.Contains(u.Id))];

        // A run for one user (the per-user Revert) plans only them; LockoutGuard still looks at everyone.
        if (scope is { } only)
        {
            eligible = [.. eligible.Where(u => u.Id.Equals(only))];
        }

        IReadOnlySet<Guid> confirming = new HashSet<Guid>();

        // The run's plan for the library lifecycle's result, with LockoutGuard applied: never leave no enabled
        // administrator with every library unless it was confirmed.
        List<SyncUserResult> PlanFor(LifecycleResult result)
        {
            inSync.Clear();
            var resolved = result.Document;
            var made = Plan(resolved, result.Catalog, listed, eligible, reapply, failedSaves, inSync);
            var planned = made.Where(a => a.Kind is SyncActionKind.Write or SyncActionKind.Release && a.Target is not null).ToDictionary(a => a.User.Id, a => a.Target!);
            foreach (var drift in made.Where(a => a is { Kind: SyncActionKind.Drift, DriftDecision.Write: not null }))
            {
                planned[drift.User.Id] = drift.DriftDecision!.Write!;
            }

            var refused = LockoutGuard.Refused(listed, users, planned, id => request.ConfirmNoFullAdmin || Confirmed(resolved, id));
            confirming = request.ConfirmNoFullAdmin ? LockoutGuard.Refused(listed, users, planned, id => Confirmed(resolved, id)) : new HashSet<Guid>();
            return [.. made.Select(a => refused.Contains(a.User.Id) ? a with { Kind = SyncActionKind.Skip, Reason = SyncReasons.NoFullAdministrator } : a)];
        }

        // The library lifecycle first: the rules resolve against the libraries as it leaves them. A real run saves it in the
        // same change that checks the confirmed plan (ifPlan): a plan that is another now (a user to
        // write that the dry run didn't list, or other access for one it did) changes nothing at all, neither the lifecycle
        // nor the hold, the libraries seen removed, the audit or any user.
        LifecycleResult lifecycle;
        List<SyncUserResult> plan;
        string? pausedUnderWriter = null;
        if (pause is null && !request.DryRun)
        {
            LifecycleResult? applied = null;
            List<SyncUserResult>? checkedPlan = null;
            await _store.UpdateAsync(
                d =>
                {
                    // The pause again, under the store's writer: a restore writes its hold before
                    // its save takes this writer, so a restore that landed since the first look is seen here, and the run plans
                    // nothing from its document. Paused now, the run writes nothing at all.
                    pausedUnderWriter = PauseReason(_configuration.GetConfiguration(), _paths, _store, releasing);
                    if (pausedUnderWriter is not null)
                    {
                        return d;
                    }

                    applied = LibraryLifecycle.Reconcile(d, listed, removed, started);
                    checkedPlan = PlanFor(applied);
                    if (request.IfPlan is { } confirmed)
                    {
                        RequirePlan(checkedPlan, confirmed, users, scope);
                    }

                    return applied.Document;
                },
                cancellationToken,
                notify: false).ConfigureAwait(false);
            if (releasing is not null && string.Equals(pausedUnderWriter, SyncReasons.StoreRestored, StringComparison.Ordinal))
            {
                // A restore began since the confirmation looked at its hold: that hold is
                // the restore's now, whose own dry run is required. Nothing was written, and nothing is lifted.
                throw AnotherHold();
            }

            if (pausedUnderWriter is not null)
            {
                // Paused since the first look: as a paused run, with the hold kept (never released) and the plan only shown.
                pause = pausedUnderWriter;
                lifecycle = LibraryLifecycle.Reconcile(_store.Current, listed, removed, started);
                plan = PlanFor(lifecycle);
            }
            else
            {
                lifecycle = applied!;
                plan = checkedPlan!;
                ForgetRemoved(removed);
                if (Release(releasing) == RestoreHold.ReleaseOutcome.AnotherHold)
                {
                    // A restore began after the plan was checked under the writer: its hold stays (its save comes after
                    // this run's), and the run writes the plan that was confirmed, of the data stored before it.
                    _logger.LogWarning("A restore began while Access Roles' library sync was being confirmed: the confirmed plan is written, and the new restore's hold stays until its own dry run is confirmed");
                }

                if (lifecycle.Events.Count > 0)
                {
                    await _audit.AppendAsync([.. lifecycle.Events.Select(e => LibrarySyncAudit.Library(request.Actor, e))], cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            // Paused for another reason too (no plan to confirm), or a dry run: the hold goes as before, nothing else.
            if (Release(releasing) == RestoreHold.ReleaseOutcome.AnotherHold)
            {
                throw AnotherHold();
            }

            lifecycle = LibraryLifecycle.Reconcile(_store.Current, listed, removed, started);
            plan = PlanFor(lifecycle);
        }

        if (!request.DryRun && !string.Equals(pause, _lastPause, StringComparison.Ordinal))
        {
            var previous = _lastPause;
            _lastPause = pause;
            if (pause is not null || previous is not "(not run yet)")
            {
                await _audit.AppendAsync([LibrarySyncAudit.Pause(request.Actor, pause, previous is "(not run yet)" ? null : previous)], cancellationToken).ConfigureAwait(false);
                if (pause is null)
                {
                    _logger.LogInformation("Access Roles' library sync writes again");
                }
                else
                {
                    _logger.LogInformation("Access Roles' library sync is paused ({Reason}): nothing is written", pause);
                }
            }
        }

        var document = lifecycle.Document;

        var report = new SyncReport(request.Trigger, started)
        {
            DryRun = request.DryRun,
            Paused = pause,
            Libraries = lifecycle.Events,
            WaitingLibraries = lifecycle.Waiting,
            WithheldLibraries = lifecycle.Withheld,
            BeingCreated = creating,
            ManagedUsers = users.Count(u => EffectiveRulesResolver.Resolve(document, lifecycle.Catalog, u).Libraries.Writes),
            Scope = scope
        };
        if (pause is not null || request.DryRun)
        {
            return report with { Users = plan, Duration = Now() - started };
        }

        // A failed save is written again only for users whose libraries are still managed or who wait for a release; the
        // others' are forgotten, so they don't ask for a run every 30 s. A run for one user looks at theirs.
        foreach (var id in failedSaves.Where(id => (scope is null || id.Equals(scope.Value)) && !plan.Any(a => a.User.Id.Equals(id) && a.Force) && !StillWritten(document, lifecycle.Catalog, users, id)))
        {
            _writer.ForgetFailedSave(id);
        }

        if (confirming.Count > 0)
        {
            await _store.UpdateAsync(d => d with { Assignments = [.. d.Assignments.Select(a => confirming.Contains(a.UserId) && a.ConfirmedNoFullAdminUtc is null ? a with { ConfirmedNoFullAdminUtc = started } : a)] }, cancellationToken, notify: false).ConfigureAwait(false);
        }

        // Once the run writes, what it wrote is recorded and audited even when its request is cancelled (an aborted POST
        // Sync, a shutdown): it stops between users, and finishes its bookkeeping.
        var results = new List<SyncUserResult>();
        var cancelled = false;
        foreach (var action in plan)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            try
            {
                results.Add(await ExecuteAsync(action, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
        }

        // Users already in sync whose last write isn't recorded as their access (an admin set it by hand, or the access was
        // right before the first write): recorded, so a later edit is seen as drift.
        cancelled |= cancellationToken.IsCancellationRequested;
        if (!cancelled)
        {
            foreach (var (user, fields) in inSync)
            {
                _reported.TryRemove(user.Id, out _);
                await RecordLastWriteAsync(user.Id, fields, user, CancellationToken.None).ConfigureAwait(false);
            }
        }

        await AuditAsync(request, results, listed, CancellationToken.None).ConfigureAwait(false);
        if (cancelled)
        {
            _logger.LogInformation("Access Roles' library sync ({Trigger}) was cancelled after {Done} of {Planned} users; what it wrote is audited", request.Trigger, results.Count, plan.Count);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // A user out of a reported situation (skipped, drifted) is reported again if they get back into it. A run for one user
        // knows only about them: the others' reported situations stay.
        foreach (var id in _reported.Keys.Where(id => (scope is null || id.Equals(scope.Value)) && !results.Any(r => r.User.Id.Equals(id) && r.Kind is SyncActionKind.Skip or SyncActionKind.Drift)).ToList())
        {
            _reported.TryRemove(id, out _);
        }

        var finished = report with { Users = results, Duration = Now() - started };
        if (_writer.FailedSaves.Count > 0 || results.Any(r => r.Kind == SyncActionKind.Skip && r.Reason is SyncReasons.WriteAborted or SyncReasons.LockTimeout or SyncReasons.WriteFailed or SyncReasons.NativeChanged or SyncReasons.ConcurrentSave))
        {
            RetryWanted?.Invoke(this, EventArgs.Empty);
        }

        if (finished.Written > 0 || finished.Skipped > 0 || finished.Libraries.Count > 0)
        {
            _logger.LogInformation(
                "Access Roles' library sync ({Trigger}): {Written} writes, {Skipped} skipped, {Drifted} drifted, {Libraries} library changes, in {Ms} ms",
                request.Trigger,
                finished.Written,
                finished.Skipped,
                finished.Drifted,
                finished.Libraries.Count,
                (long)finished.Duration.TotalMilliseconds);
        }

        return finished;
    }

    // Whether the sync still writes a user's libraries: managed, or a release waiting for its write.
    private static bool StillWritten(AccessRolesDocument document, LibraryCatalog catalog, IReadOnlyList<ServerUser> users, Guid userId)
    {
        if (users.FirstOrDefault(u => u.Id.Equals(userId)) is not { } user)
        {
            return false;
        }

        var effective = EffectiveRulesResolver.Resolve(document, catalog, user);
        return effective.Libraries.Writes || effective.PendingRelease is not null;
    }

    private async Task<SyncUserResult> ExecuteAsync(SyncUserResult action, CancellationToken cancellationToken, TimeSpan? lockTimeout = null)
    {
        // Every write says what it was planned against (the native access the run read): an edit since is never written
        // over. Once the writer returns, its bookkeeping is finished whatever the token says.
        var user = action.User;
        switch (action.Kind)
        {
            case SyncActionKind.Write:
            {
                // The ledger keeps the user's original access before the plugin's first write.
                if (!_store.Current.Ledger.ContainsKey(user.Id))
                {
                    var now = Now();
                    await _store.UpdateAsync(
                        d => d.Ledger.ContainsKey(user.Id) ? d : d with { Ledger = new Dictionary<Guid, LedgerEntry>(d.Ledger) { [user.Id] = Capture(action.Native, user.Native.EnableUserPreferenceAccess, now) } },
                        cancellationToken,
                        notify: false).ConfigureAwait(false);
                }

                var write = await _writer.WriteAsync(user.Id, action.Target!, cancellationToken, force: action.Force, expected: action.Native, lockTimeout: lockTimeout).ConfigureAwait(false);
                if (write.Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Unchanged)
                {
                    await RecordLastWriteAsync(user.Id, action.Target!, null, CancellationToken.None).ConfigureAwait(false);
                    return action with { Outcome = write.Outcome, Differences = write.Differences, Write = write };
                }

                return Skipped(action, write);
            }

            case SyncActionKind.Drift when action.DriftDecision?.Write is { } safe:
            {
                // ReportOnly: only the removals in the safe direction; the last write stays, so the admin's edit is still drift.
                var write = await _writer.WriteAsync(user.Id, safe, cancellationToken, force: action.Force, expected: action.Native, lockTimeout: lockTimeout).ConfigureAwait(false);
                return write.Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Unchanged
                    ? action with { Outcome = write.Outcome, Differences = write.Differences, Write = write }
                    : Skipped(action, write);
            }

            case SyncActionKind.Release:
            {
                var write = await _writer.WriteAsync(user.Id, action.Target!, cancellationToken, force: action.Force, expected: action.Native).ConfigureAwait(false);
                if (write.Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Unchanged)
                {
                    await _store.UpdateAsync(d => DropRelease(d, user.Id, action.Effective?.PendingRelease), CancellationToken.None, notify: false).ConfigureAwait(false);
                    return action with { Outcome = write.Outcome, Differences = write.Differences, Write = write };
                }

                if (write.NativeChanged)
                {
                    // The baseline no longer holds under the user's lock: an admin edited the unmanaged user since the run read
                    // them, so the release is dropped without a write (Storage/LedgerRelease's contract).
                    await _store.UpdateAsync(d => DropRelease(d, user.Id, action.Effective?.PendingRelease), CancellationToken.None, notify: false).ConfigureAwait(false);
                    return action with { Kind = SyncActionKind.DropRelease, Reason = SyncReasons.BaselineChanged, Outcome = write.Outcome, Write = write };
                }

                return Skipped(action, write);
            }

            case SyncActionKind.DropRelease:
                await _store.UpdateAsync(d => DropRelease(d, user.Id, action.Effective?.PendingRelease), cancellationToken, notify: false).ConfigureAwait(false);
                return action;

            default:
                return action;
        }
    }

    private static SyncUserResult Skipped(SyncUserResult action, PolicyWriteResult write)
        => action with { Kind = SyncActionKind.Skip, Reason = SkipReason(write), Outcome = write.Outcome, Differences = write.Differences, Write = write };

    private async Task AuditAsync(SyncRequest request, IReadOnlyList<SyncUserResult> results, LibraryCatalog catalog, CancellationToken cancellationToken)
    {
        var lines = new List<AuditRecord>();
        var emptied = new List<SyncUserResult>();
        foreach (var result in results)
        {
            var once = result.Kind is SyncActionKind.Skip or SyncActionKind.Drift;
            if (once)
            {
                // Once per user and situation: a run every few minutes must not repeat the same line.
                var signature = Signature(result);
                if (_reported.TryGetValue(result.User.Id, out var last) && string.Equals(last, signature, StringComparison.Ordinal))
                {
                    continue;
                }

                _reported[result.User.Id] = signature;
            }
            else
            {
                _reported.TryRemove(result.User.Id, out _);
            }

            lines.Add(LibrarySyncAudit.User(request.Actor, request.Trigger, result, catalog));
            if (result.Wrote && result.Reason is SyncReasons.NoLibrariesLeft or SyncReasons.NewUserNoLibraries)
            {
                emptied.Add(result);
            }
        }

        if (emptied.Count > 0)
        {
            lines.Add(LibrarySyncAudit.WithoutLibraries(request.Actor, emptied));
        }

        if (lines.Count > 0)
        {
            await _audit.AppendAsync(lines, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecordLastWriteAsync(Guid userId, LibraryFields fields, ServerUser? capture, CancellationToken cancellationToken)
    {
        var now = Now();
        await _store.UpdateAsync(
            d =>
            {
                if (!d.Ledger.TryGetValue(userId, out var entry))
                {
                    if (capture is null)
                    {
                        return d;
                    }

                    entry = Capture(capture.Native.Libraries, capture.Native.EnableUserPreferenceAccess, now);
                }
                else if (entry.LastWrite is { } last && fields.Same(last.EnableAllFolders, last.EnabledFolders) && capture is not null)
                {
                    return d;
                }

                var written = entry with { LastWrite = new LedgerWrite { Utc = now, EnableAllFolders = fields.EnableAllFolders, EnabledFolders = fields.EnableAllFolders ? [] : fields.EnabledFolders } };
                return d with { Ledger = new Dictionary<Guid, LedgerEntry>(d.Ledger) { [userId] = written } };
            },
            cancellationToken,
            notify: false).ConfigureAwait(false);
    }

    private static string SkipReason(PolicyWriteResult write) => write.Outcome switch
    {
        _ when write.NativeChanged => SyncReasons.NativeChanged,
        PolicyWriteOutcome.Resynced => SyncReasons.ConcurrentSave,
        PolicyWriteOutcome.Aborted => SyncReasons.WriteAborted,
        PolicyWriteOutcome.LockTimeout => SyncReasons.LockTimeout,
        PolicyWriteOutcome.UserNotFound => SyncReasons.UserNotFound,
        _ => SyncReasons.WriteFailed
    };

    private static bool Confirmed(AccessRolesDocument document, Guid userId)
        => document.Assignments.FirstOrDefault(a => a.UserId.Equals(userId))?.ConfirmedNoFullAdminUtc is not null;

    // The confirmed plan (ifPlan): another plan writes nothing, and the 409 names the users written otherwise
    // than the dry run said, or everyone it writes now when the dry run's lines aren't remembered. A run
    // for one user names only them, whatever run the token was of.
    private static void RequirePlan(IReadOnlyList<SyncUserResult> plan, string confirmed, IReadOnlyList<ServerUser> users, Guid? scope)
    {
        if (AccessRolesTokens.Same(SyncPlanToken.Of(plan, scope), confirmed))
        {
            return;
        }

        var differing = SyncPlanToken.Differing(confirmed, plan, scope);
        var named = scope is { } only
            ? users.Where(u => u.Id.Equals(only))
            : differing is null
            ? plan.Where(a => SyncPlanToken.Written(a) is not null).Select(a => a.User)
            : users.Where(u => differing.Contains(u.Id));
        throw new AccessRolesException(
            PluginErrorKind.Conflict,
            AccessRolesException.Changed,
            "What the library sync would write changed since the dry run you confirmed; nothing was written. Look at the dry run again.",
            [.. named.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(AccessRolesMapper.UserRef)]);
    }

    // The restore hold lifted, once the run may go on (its confirmed plan checked): what was lifted.
    private RestoreHold.ReleaseOutcome Release(RestoreHold.HoldIdentity? releasing)
    {
        if (releasing is null)
        {
            return RestoreHold.ReleaseOutcome.NotHeld;
        }

        // Only the hold confirmed (since 1.0.1): another one a restore wrote since stays.
        var outcome = RestoreHold.Release(_paths, _store, releasing);
        if (outcome == RestoreHold.ReleaseOutcome.Released)
        {
            // The confirmation is audited (LibrarySyncResumed, the admin as actor) even when no run saw the hold before.
            _lastPause = SyncReasons.StoreRestored;
            _logger.LogInformation("An admin confirmed Access Roles' library sync after its data file was restored from a snapshot");
        }

        return outcome;
    }

    // The confirmation's answer when the hold it confirmed isn't the one there now (since 1.0.1).
    private static AccessRolesException AnotherHold() => new(
        PluginErrorKind.Conflict,
        AccessRolesException.DryRunRequired,
        "A restore began since the dry run you confirmed, so the library sync is held for that restore now: nothing was written, and nothing was lifted. Look at the sync's dry run again (POST /AccessRoles/Admin/Sync?dryRun=true), then confirm with its PlanToken.");

    // The libraries a run reconciled as removed, forgotten after its lifecycle was saved; one seen removed since stays.
    private void ForgetRemoved(IReadOnlySet<Guid> removed)
    {
        lock (_removedLock)
        {
            _removedSeen.ExceptWith(removed);
        }
    }

    // The libraries seen removed since a run last reconciled them (a copy; ForgetRemoved forgets them once saved).
    private HashSet<Guid> SeenRemoved()
    {
        lock (_removedLock)
        {
            return _removedSeen.ToHashSet();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _run.Dispose();

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    // A user's creation mark while it lasts; an expired one is forgotten.
    private CreationMark? Creation(Guid userId)
    {
        if (!_creating.TryGetValue(userId, out var mark))
        {
            return null;
        }

        if (mark.UntilUtc > Now())
        {
            return mark;
        }

        _creating.TryRemove(new KeyValuePair<Guid, CreationMark>(userId, mark));
        return null;
    }

    // One user's plan (PlanUser): the action or none, the libraries listed, the users in sync whose last write to record.
    private sealed record UserPlan(SyncUserResult? Plan, LibraryCatalog Listed, List<(ServerUser User, LibraryFields Fields)> InSync);

    // A user being created (BeginCreation): the creating request, until when, and whether the request is done with them
    // (settling: the runs still wait, the user's own sign-in may be corrected).
    private sealed record CreationMark(string? RequestId, DateTime UntilUtc, bool Settling);
}
