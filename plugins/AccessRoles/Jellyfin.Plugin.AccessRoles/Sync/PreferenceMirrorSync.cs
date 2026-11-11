using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// The optional native mirror (<c>MirrorPreferenceLockToPolicy</c>, off by default): with the setting on,
/// a managed non-admin whose roles lock all five server-stored settings sections (Profile, Display (account), Home,
/// Playback, Subtitles) gets native <c>EnableUserPreferenceAccess=false</c>, which keeps them out of those settings without
/// the plugin. Off by default.
/// </summary>
/// <remarks>
/// <para>Each run of the library sync (every trigger: a start, a change of the store or the settings, the backstop interval,
/// a retry), after its library pass, runs it once: the flag is a user-row column on 10.11.5 that the sign-in race and a
/// library write from a stale row can put back (as seen on 10.11.5), so it is checked again after every write. For each
/// user:</para>
/// <list type="bullet">
/// <item>wanted and on: recorded in the document's <c>PreferenceMirror</c> (the mirror's ownership) first, then written off
/// (<see cref="PolicyWriter.WritePreferenceAccessAsync"/>) and audited <c>PreferenceAccessMirrored</c>. The record comes
/// first, so a flag the mirror turned off always has one: when the record can't be saved, the
/// flag isn't written (audited <c>WriteAborted</c>, tried again at the next run), and when the write doesn't happen the
/// record goes again; a record whose flag stays on is harmless (it is forgotten, or the flag written, at the next run);</item>
/// <item>wanted and off: nothing; a flag someone else turned off is never recorded, so never turned on;</item>
/// <item>not wanted (a lock removed, the user out of management or an administrator, the setting off) and recorded: turned
/// back on while it is still off, the record dropped, audited <c>PreferenceAccessRestored</c> with why; but while Degraded
/// mode locks the user (a restore or a store put back empty lost their assignment), left off until an admin ends
/// it, whose <c>Resolved</c> runs the sync.</item>
/// </list>
/// <para>It writes nothing while the sync's store and safety pauses hold (SAFE-MODE, <c>EnforcementEnabled</c> off, a
/// read-only, degraded or restored store; <c>LibrarySyncEnabled</c> off doesn't pause it, since it isn't library access),
/// nor after the plugin's uninstall began (<see cref="StopForUninstall"/>). <see cref="ReleaseAsync"/> puts the recorded
/// flags back on whatever the pauses say: <c>ReleaseLocks</c> and <c>OnUninstalling</c>; the records are the document's
/// and Degraded mode's evidence (<see cref="Enforcement.DegradedMode.MirrorEvidence"/>: a copy the store couldn't use).</para>
/// <para>A failure for one user (the data file can't be saved, the write throws) is logged and audited for that user, and
/// the others go on.</para>
/// </remarks>
internal sealed class PreferenceMirrorSync : IDisposable
{
    /// <summary>The pause after the plugin's uninstall began.</summary>
    public const string Uninstalling = "Uninstalling";

    private static readonly AccessRolesLock[] _mirrored = [AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.Home, AccessRolesLock.Playback, AccessRolesLock.Subtitles];

    private readonly AccessRolesStore _store;
    private readonly ILibraryCatalogSource _catalog;
    private readonly IServerUsers _users;
    private readonly PolicyWriter _writer;
    private readonly AccessRolesAuditLog _audit;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly AccessRolesPaths _paths;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _run = new(1, 1);
    private volatile bool _uninstalling;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreferenceMirrorSync"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="catalog">The library catalog.</param>
    /// <param name="users">The server's users.</param>
    /// <param name="writer">The only policy writer.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public PreferenceMirrorSync(AccessRolesStore store, ILibraryCatalogSource catalog, IServerUsers users, PolicyWriter writer, AccessRolesAuditLog audit, IPluginConfigurationProvider configuration, AccessRolesPaths paths, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _catalog = catalog;
        _users = users;
        _writer = writer;
        _audit = audit;
        _configuration = configuration;
        _paths = paths;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the five server-stored settings sections whose locks together turn the flag off.</summary>
    public static IReadOnlyList<AccessRolesLock> MirroredLocks => _mirrored;

    /// <summary>Gets a value indicating whether the plugin's uninstall began: the mirror turns no flag off any more.</summary>
    public bool IsStoppedForUninstall => _uninstalling;

    /// <summary>
    /// Gets why the mirror wants a user's flag off, or <c>null</c> when it doesn't: the setting on, a managed non-admin,
    /// and every one of <see cref="MirroredLocks"/> locked.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="effective">The user's effective rules.</param>
    /// <returns><c>true</c> when the flag is to be off.</returns>
    public static bool Wanted(PluginConfiguration configuration, EffectiveAccess effective)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(effective);
        return configuration.MirrorPreferenceLockToPolicy
            && !effective.User.IsAdministrator
            && effective.State == AccessRolesUserState.Managed
            && _mirrored.All(effective.Locked.Contains);
    }

    /// <summary>
    /// Stops the mirror turning flags off for the rest of the process (the plugin's uninstall began, <c>OnUninstalling</c>).
    /// </summary>
    public void StopForUninstall() => _uninstalling = true;

    /// <summary>
    /// Gets why the mirror writes nothing now, or <c>null</c> when it may.
    /// </summary>
    /// <returns>The pause reason.</returns>
    public string? PauseReason() => _uninstalling ? Uninstalling : LibrarySync.MirrorPauseReason(_configuration.GetConfiguration(), _paths, _store);

    /// <summary>
    /// Runs the mirror once (the class remarks).
    /// </summary>
    /// <param name="actor">Who started the run that runs it.</param>
    /// <param name="scope">One user only (a run for one user), or <c>null</c> for everyone.</param>
    /// <param name="cancellationToken">Cancels the run between users.</param>
    /// <returns>What it did for each user it wrote or dropped a record for.</returns>
    public async Task<IReadOnlyList<MirrorResult>> RunAsync(AuditActor actor, Guid? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (PauseReason() is not null)
            {
                return [];
            }

            var configuration = _configuration.GetConfiguration();
            var document = _store.Current;
            if (!configuration.MirrorPreferenceLockToPolicy && document.PreferenceMirror.Count == 0)
            {
                return [];
            }

            var catalog = _catalog.Read();
            var users = _users.GetAll();
            var results = new List<MirrorResult>();
            var failures = new List<AuditRecord>();
            foreach (var user in users.Where(u => scope is null || u.Id.Equals(scope.Value)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var effective = EffectiveRulesResolver.Resolve(document, catalog, user);
                var wanted = Wanted(configuration, effective);
                var owned = document.PreferenceMirror.ContainsKey(user.Id);
                var on = user.Native.EnableUserPreferenceAccess;
                try
                {
                    if (wanted && on)
                    {
                        results.Add(await TurnOffAsync(user, owned, cancellationToken).ConfigureAwait(false));
                    }
                    else if (!wanted && owned && _store.Degraded.Current?.Applies(user.Id) != true)
                    {
                        results.Add(await TurnOnAsync(user, NotWantedBecause(configuration, effective), cancellationToken).ConfigureAwait(false));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One user's failure never stops the others'.
                    _logger.LogError(ex, "Access Roles' mirror of the preference access of user {UserId} failed; the next run tries again", user.Id);
                    results.Add(new MirrorResult(user.Id, user.Name, MirrorAction.Failed, ex.GetType().Name, null));
                    failures.Add(Failure(actor, user, wanted && on ? "TurnOff" : "TurnOn", ex.Message));
                }
            }

            // Records of users the server no longer has (the user-data owner drops them at a deletion; a missed event too).
            var gone = document.PreferenceMirror.Keys.Where(id => (scope is null || id.Equals(scope.Value)) && !users.Any(u => u.Id.Equals(id))).ToList();
            if (gone.Count > 0)
            {
                await ForgetQuietlyAsync(gone).ConfigureAwait(false);
            }

            await AuditAsync(actor, results, null, failures).ConfigureAwait(false);
            return results;
        }
        finally
        {
            _run.Release();
        }
    }

    /// <summary>
    /// Puts the recorded flags back on (<c>ReleaseLocks</c>, <c>OnUninstalling</c>): each still off is written on, and its
    /// record dropped; one already on loses its record only. Runs whatever the pauses say.
    /// </summary>
    /// <param name="actor">Who asked.</param>
    /// <param name="only">The users to release, or <c>null</c> for every recorded one.</param>
    /// <param name="reason">Why, for the audit (<c>ReleaseLocks</c>, <c>Uninstalling</c>).</param>
    /// <param name="cancellationToken">Cancels waiting; a started write finishes.</param>
    /// <returns>What it did for each recorded user.</returns>
    public async Task<IReadOnlyList<MirrorResult>> ReleaseAsync(AuditActor actor, IReadOnlyCollection<Guid>? only, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<MirrorResult>();
            var failures = new List<AuditRecord>();
            var evidence = _store.Degraded.MirrorEvidence;
            foreach (var id in _store.Current.PreferenceMirror.Keys.Union(evidence).Where(id => only is null || only.Contains(id)).ToList())
            {
                if (_users.Find(id) is not { } user)
                {
                    await ForgetQuietlyAsync([id]).ConfigureAwait(false);
                    _store.Degraded.ForgetMirrorEvidence([id]);
                    continue;
                }

                try
                {
                    var result = await TurnOnAsync(user, reason, cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    if (result.Action != MirrorAction.Failed)
                    {
                        _store.Degraded.ForgetMirrorEvidence([id]);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Access Roles couldn't put back the preference access of user {UserId} ({Reason})", user.Id, reason);
                    results.Add(new MirrorResult(user.Id, user.Name, MirrorAction.Failed, ex.GetType().Name, null));
                    failures.Add(Failure(actor, user, "TurnOn", ex.Message));
                }
            }

            await AuditAsync(actor, results, reason, failures).ConfigureAwait(false);
            return results;
        }
        finally
        {
            _run.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _run.Dispose();

    private static string NotWantedBecause(PluginConfiguration configuration, EffectiveAccess effective)
    {
        if (!configuration.MirrorPreferenceLockToPolicy)
        {
            return "MirrorOff";
        }

        if (effective.User.IsAdministrator)
        {
            return "Administrator";
        }

        return effective.State == AccessRolesUserState.Managed ? "NotAllSectionsLocked" : "NotManaged";
    }

    // The audit line of a user the mirror couldn't do anything for (WriteAborted: nothing written, the next run tries again).
    private static AuditRecord Failure(AuditActor actor, ServerUser user, string what, string? error)
        => new(actor, AuditActions.WriteAborted, user.Id, null, user.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = "PreferenceMirrorFailed",
            ["Field"] = "EnableUserPreferenceAccess",
            ["Mirror"] = what,
            ["Error"] = error
        });

    private async Task<MirrorResult> TurnOffAsync(ServerUser user, bool owned, CancellationToken cancellationToken)
    {
        // The record before the flag: a store failure, or a crash, between the two then leaves at
        // most a record whose flag is still on, which the next run forgets or writes, never a flag off with no record.
        if (!owned)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (!await UpdateAsync(d => d.PreferenceMirror.ContainsKey(user.Id) ? d : d with { PreferenceMirror = new Dictionary<Guid, MirroredPreference>(d.PreferenceMirror) { [user.Id] = new MirroredPreference { WrittenUtc = now } } }, cancellationToken).ConfigureAwait(false))
            {
                return new MirrorResult(user.Id, user.Name, MirrorAction.Failed, "RecordNotSaved", null);
            }
        }

        var write = await _writer.WritePreferenceAccessAsync(user.Id, value: false, expected: true, cancellationToken).ConfigureAwait(false);
        if (write.Outcome == PolicyWriteOutcome.Written)
        {
            return new MirrorResult(user.Id, user.Name, MirrorAction.TurnedOff, owned ? "WrittenBackOn" : "AllSectionsLocked", write);
        }

        // Nothing written: the record made for this write goes again, so a flag someone else turned off meanwhile (Unchanged:
        // it went off between the users' read and the write) is never the mirror's. A save that failed may have been half
        // done (the server's cache can hold the write), so its record stays: the next run writes the flag, or forgets it.
        if (!owned && !write.SaveFailed)
        {
            await ForgetQuietlyAsync([user.Id]).ConfigureAwait(false);
        }

        return new MirrorResult(user.Id, user.Name, write.Outcome == PolicyWriteOutcome.Unchanged ? MirrorAction.None : MirrorAction.Failed, write.Outcome.ToString(), write);
    }

    private async Task<MirrorResult> TurnOnAsync(ServerUser user, string reason, CancellationToken cancellationToken)
    {
        PreferenceWriteResult? write = null;
        if (!user.Native.EnableUserPreferenceAccess)
        {
            write = await _writer.WritePreferenceAccessAsync(user.Id, value: true, expected: false, cancellationToken).ConfigureAwait(false);
            // Someone turned it on meanwhile (NativeChanged, found on): nothing to write, the record goes all the same.
            if (write.Outcome is not (PolicyWriteOutcome.Written or PolicyWriteOutcome.Unchanged or PolicyWriteOutcome.UserNotFound) && !(write.NativeChanged && write.Before == true))
            {
                return new MirrorResult(user.Id, user.Name, MirrorAction.Failed, write.Outcome.ToString(), write);
            }
        }

        // The flag is on: a record that can't be dropped now is harmless (the next run forgets it).
        await ForgetQuietlyAsync([user.Id]).ConfigureAwait(false);
        return new MirrorResult(user.Id, user.Name, write is { Outcome: PolicyWriteOutcome.Written } ? MirrorAction.TurnedOn : MirrorAction.Forgotten, reason, write);
    }

    private async Task ForgetQuietlyAsync(IReadOnlyCollection<Guid> ids)
    {
        if (_store.IsReadOnly)
        {
            // Nothing can be dropped from a read-only file; a record whose flag is on is harmless.
            return;
        }

        try
        {
            await UpdateAsync(d => ids.Any(d.PreferenceMirror.ContainsKey) ? d with { PreferenceMirror = d.PreferenceMirror.Where(p => !ids.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) } : d, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't drop {Count} of the mirror's records; the next run tries again", ids.Count);
        }
    }

    // False when nothing could be saved because the data file is read-only (a store write that fails throws).
    private async Task<bool> UpdateAsync(Func<AccessRolesDocument, AccessRolesDocument> change, CancellationToken cancellationToken)
    {
        if (_store.IsReadOnly)
        {
            _logger.LogWarning("Access Roles couldn't record the mirror's preference access writes: its data file is read-only");
            return false;
        }

        await _store.UpdateAsync(change, cancellationToken, notify: false).ConfigureAwait(false);
        return true;
    }

    private async Task AuditAsync(AuditActor actor, IReadOnlyList<MirrorResult> results, string? releasedFor, IReadOnlyList<AuditRecord> failures)
    {
        var records = results
            .Where(r => r.Action is MirrorAction.TurnedOff or MirrorAction.TurnedOn or MirrorAction.Forgotten)
            .Select(r => new AuditRecord(
                actor,
                r.Action == MirrorAction.TurnedOff ? AuditActions.PreferenceAccessMirrored : AuditActions.PreferenceAccessRestored,
                r.UserId,
                null,
                r.Name,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Reason"] = r.Reason,
                    ["Written"] = r.Action is MirrorAction.TurnedOff or MirrorAction.TurnedOn,
                    ["EnableUserPreferenceAccess"] = r.Action == MirrorAction.TurnedOff ? false : true,
                    ["Holds"] = r.Write?.Holds,
                    ["ReleasedFor"] = releasedFor
                }))
            .ToList();
        records.AddRange(failures);
        if (records.Count > 0)
        {
            await _audit.AppendAsync(records, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
