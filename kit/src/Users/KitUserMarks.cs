using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// The orphan marks of <see cref="UserReconciler"/> in <c>&lt;data&gt;/&lt;folder&gt;/kit-users.json</c>. A failure to
/// read or write the file never stops the reconciler: it is logged once, as a Warning, and the marks of this process
/// are used. While the file can't be read (<see cref="IsReadOnly"/>), the users deleted on this server aren't known, so
/// whatever gives records to users by name refuses (<see cref="ThrowIfDeletedUsersUnknown"/>), and an admin can start
/// the list empty (<see cref="StartEmptyAsync"/>).
/// </summary>
internal sealed class KitUserMarks : IDisposable
{
    private readonly JsonDocumentStore<KitUsersDocument> _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _recoveryPage;
    private volatile IReadOnlyDictionary<Guid, OrphanMark>? _fallback;

    // 1 once the marks' trouble has been logged, until an admin starts them empty.
    private int _troubleLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitUserMarks"/> class.
    /// </summary>
    /// <param name="file">The file, normally <c>&lt;KitPaths.Root&gt;/kit-users.json</c>.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="recoveryPage">Whether the plugin's settings page offers <see cref="StartEmptyAsync"/>
    /// (<c>KitOptions.UsersRecoveryPage</c>), which words the banner and the log.</param>
    public KitUserMarks(string file, ILogger logger, TimeProvider? timeProvider = null, bool recoveryPage = false)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _recoveryPage = recoveryPage;
        _store = CreateStore(file, logger, _timeProvider, recoveryPage);
    }

    /// <summary>
    /// Gets the store underneath (the conformance suite and Diagnostics use it).
    /// </summary>
    public JsonDocumentStore<KitUsersDocument> Store => _store;

    /// <summary>
    /// Gets a value indicating whether the marks can't be read from or saved to the file: it couldn't be read at load (the
    /// store read-only, <c>ReadOnlyDataUnreadable</c>), is a newer version's, or can't be opened now. The list of users
    /// deleted on this server is then incomplete: only the marks made in this process, kept in memory until a restart.
    /// </summary>
    public bool IsReadOnly
    {
        get
        {
            try
            {
                return _store.IsReadOnly;
            }
            catch (PluginException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the marks the file holds aren't known: it couldn't be read (unreadable, its upgrade
    /// failed, missing beside snapshots that can't be used) or can't be opened now, so <see cref="DeletedIds"/> has only the
    /// marks of this process. A newer version's file is read as far as this version can, its marks included.
    /// </summary>
    public bool DeletedUsersUnknown
    {
        get
        {
            try
            {
                return _store.Status is StoreStatus.ReadOnlyDataUnreadable or StoreStatus.ReadOnlyDataMissing or StoreStatus.ReadOnlyMigrationFailed;
            }
            catch (PluginException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Gets the marks by user id.
    /// </summary>
    public IReadOnlyDictionary<Guid, OrphanMark> Current
    {
        get
        {
            if (_fallback is { } fallback)
            {
                return fallback;
            }

            try
            {
                return ToDictionary(_store.Current.Orphans);
            }
            catch (PluginException ex)
            {
                LogTrouble(ex, "can't be read");
                return new Dictionary<Guid, OrphanMark>();
            }
        }
    }

    /// <summary>
    /// Gets the ids of users Jellyfin reported deleted on this server while they held records, or that an admin applied the
    /// deleted-user policy to (<see cref="OrphanMarkSource.Deleted"/>, <see cref="OrphanMarkSource.PolicyApplied"/>): their
    /// records are never given to a user of the same name, by <i>Remap by name</i> or by an import. Incomplete while
    /// <see cref="IsReadOnly"/>.
    /// </summary>
    public IReadOnlySet<Guid> DeletedIds => Current.Values
        .Where(m => m.Source is OrphanMarkSource.Deleted or OrphanMarkSource.PolicyApplied)
        .Select(m => m.UserId)
        .ToHashSet();

    /// <summary>
    /// Creates the store as the kit uses it.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="recoveryPage">Whether the plugin's settings page offers the recovery (the store's banner says so).</param>
    /// <returns>The store.</returns>
    public static JsonDocumentStore<KitUsersDocument> CreateStore(string file, ILogger logger, TimeProvider? timeProvider = null, bool recoveryPage = false)
        => new(file, KitUsersDocument.CurrentVersion, KitUsersDocument.CreateEmpty, [], logger, JsonStoreFormat.Options, timeProvider, recoveryPage: recoveryPage);

    /// <summary>
    /// Refuses (409) what would give records to users by name while the list of users deleted on this server can't be
    /// read (<see cref="DeletedUsersUnknown"/>): a deleted user's records could go to a new user of the same name.
    /// Fails closed.
    /// </summary>
    /// <param name="action">What is refused, to start the sentence ("Remap by name").</param>
    /// <exception cref="PluginException">409 while the marks can't be read.</exception>
    public void ThrowIfDeletedUsersUnknown(string action)
    {
        if (DeletedUsersUnknown)
        {
            throw PluginException.Conflict(
                action + " is refused: the list of users deleted on this server (" + Path.GetFileName(_store.FilePath) + ") can't be read, so a deleted user's records could go to a new user of the same name. Nothing was changed. "
                + (_recoveryPage
                    ? "Start that list empty on the plugin's settings page (Data tab) first, or put a readable copy of the file back with the server stopped."
                    : "Put a readable copy of the file back, or move it aside, with the server stopped first."));
        }
    }

    /// <summary>
    /// Describes the marks' store for the <c>Users</c> entry of Diagnostics: <c>{File, Status, ReadOnly, Recoverable,
    /// Message, MarksInMemory}</c>; <c>Recoverable</c> while an admin can start them empty on the plugin's settings page,
    /// <c>MarksInMemory</c> the marks of this process that couldn't be saved (null when they are saved).
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["File"] = _store.FilePath,
            ["MarksInMemory"] = _fallback?.Count
        };
        try
        {
            result["Status"] = _store.Status.ToString();
            result["ReadOnly"] = _store.IsReadOnly;
            result["Recoverable"] = _recoveryPage && _store.CanRecover;
            result["Message"] = _store.StatusMessage;
        }
        catch (PluginException ex)
        {
            result["Status"] = null;
            result["ReadOnly"] = true;
            result["Recoverable"] = false;
            result["Message"] = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// An admin's <i>Start empty</i> for the marks while <c>kit-users.json</c> can't be read (the store's
    /// <c>RecoverAsync</c>): what couldn't be read is kept as <c>kit-users.json.corrupt-&lt;time&gt;</c>, the marks made in
    /// this process (kept in memory) are saved into the new file, and the store is writable at once. The marks of users
    /// deleted while the file couldn't be read in an earlier process are lost; <see cref="UserReconciler.StartMarksEmptyAsync"/>
    /// rebuilds the reconciler's marks from the server's users afterwards.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The recovery, and how many marks of this process were saved.</returns>
    /// <exception cref="PluginException">409 the file isn't unreadable, or a readable copy is in place now; 503 the write
    /// failed.</exception>
    public async Task<(StoreRecoveryResult Recovery, int MarksKept)> StartEmptyAsync(CancellationToken cancellationToken)
    {
        var recovery = await _store.RecoverAsync(_store.SnapshotDirectory, null, startEmpty: true, cancellationToken).ConfigureAwait(false);
        var fallback = Interlocked.Exchange(ref _fallback, null);
        Interlocked.Exchange(ref _troubleLogged, 0);
        var kept = 0;
        if (fallback is { Count: > 0 })
        {
            await _store.UpdateAsync(
                document =>
                {
                    var current = ToDictionary(document.Orphans);
                    var added = fallback.Values.Where(m => !current.ContainsKey(m.UserId)).ToList();
                    kept = added.Count;
                    return added.Count == 0 ? document : document with { Orphans = Ordered(current.Values.Concat(added)) };
                },
                cancellationToken).ConfigureAwait(false);
        }

        return (recovery, kept);
    }

    /// <summary>
    /// The reconciler's marks: every id in <paramref name="ids"/> is marked (a new one as
    /// <see cref="OrphanMarkSource.Reconciler"/>, a marked one keeps its mark), and <see cref="OrphanMarkSource.Reconciler"/>
    /// marks of other ids are removed (the id is known again, or has no records left) where <paramref name="canUnmark"/>
    /// allows it. Marks of users Jellyfin reported deleted (<see cref="OrphanMarkSource.Deleted"/>,
    /// <see cref="OrphanMarkSource.PolicyApplied"/>) are kept while the server doesn't know their id: their records may come
    /// back with a restored snapshot or an import, and must never be remapped to a new user of the same name. Since User
    /// Ratings 1.0.1 every mark of an id <paramref name="isServerUser"/> knows goes, whatever made it, a pending policy
    /// among it: the user is back (Jellyfin's database restored with them in it), so the mark is done.
    /// </summary>
    /// <param name="ids">The unknown ids that own records now.</param>
    /// <param name="canUnmark">
    /// Whether the reconciler's mark of an id not in <paramref name="ids"/> may go; every one by default. When an owner was
    /// left out of the run (read-only, unreadable), its ids aren't in <paramref name="ids"/> although they may still own
    /// records there, so only ids the server knows again may go.
    /// </param>
    /// <param name="isServerUser">Whether the server knows an id now; none by default.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    public Task SetReconciledAsync(IReadOnlyCollection<Guid> ids, Func<Guid, bool>? canUnmark, Func<Guid, bool>? isServerUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ChangeAsync(
            current =>
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var wanted = ids.ToHashSet();
                return current.Values
                    .Where(m => isServerUser is null || !isServerUser(m.UserId))
                    .Where(m => m.Source != OrphanMarkSource.Reconciler || wanted.Contains(m.UserId) || (canUnmark is not null && !canUnmark(m.UserId)))
                    .Concat(wanted.Where(id => !current.ContainsKey(id)).Select(id => new OrphanMark { UserId = id, MarkedUtc = now, Source = OrphanMarkSource.Reconciler }));
            },
            cancellationToken);
    }

    /// <summary>
    /// Marks ids as users deleted on this server: a new id, or one the reconciler marked, gets a mark from
    /// <paramref name="source"/>; an id already marked as deleted keeps its mark.
    /// </summary>
    /// <param name="ids">The ids.</param>
    /// <param name="source"><see cref="OrphanMarkSource.Deleted"/> or <see cref="OrphanMarkSource.PolicyApplied"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    public Task MarkDeletedAsync(IReadOnlyCollection<Guid> ids, OrphanMarkSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ChangeAsync(
            current =>
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var marking = ids.Where(id => !current.TryGetValue(id, out var mark) || mark.Source == OrphanMarkSource.Reconciler).ToHashSet();
                return current.Values
                    .Where(m => !marking.Contains(m.UserId))
                    .Concat(marking.Select(id => new OrphanMark { UserId = id, MarkedUtc = now, Source = source }));
            },
            cancellationToken);
    }

    /// <summary>
    /// Records owners whose deleted-user policy failed for ids marked deleted (<see cref="OrphanMark.PolicyPending"/>):
    /// added to what is pending already; an id not marked yet is marked with <paramref name="source"/>.
    /// </summary>
    /// <param name="pending">Id to the owners whose policy failed.</param>
    /// <param name="source"><see cref="OrphanMarkSource.Deleted"/> or <see cref="OrphanMarkSource.PolicyApplied"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    public Task AddPolicyPendingAsync(IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> pending, OrphanMarkSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return ChangeAsync(
            current =>
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var changed = pending
                    .Where(p => p.Value.Count > 0)
                    .Select(p =>
                    {
                        var mark = current.TryGetValue(p.Key, out var found) && found.Source != OrphanMarkSource.Reconciler
                            ? found
                            : new OrphanMark { UserId = p.Key, MarkedUtc = now, Source = source };
                        var owners = (mark.PolicyPending ?? []).Concat(p.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                        return mark with { PolicyPending = owners };
                    })
                    .ToDictionary(m => m.UserId);
                return current.Values.Where(m => !changed.ContainsKey(m.UserId)).Concat(changed.Values);
            },
            cancellationToken);
    }

    /// <summary>
    /// Removes an owner from an id's pending policies, once the policy succeeded there.
    /// </summary>
    /// <param name="userId">The id.</param>
    /// <param name="owner">The owner's name.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    public Task ClearPolicyPendingAsync(Guid userId, string owner, CancellationToken cancellationToken)
        => ChangeAsync(
            current => current.Values.Select(m =>
            {
                if (!m.UserId.Equals(userId) || m.PolicyPending is null || !m.PolicyPending.Contains(owner, StringComparer.Ordinal))
                {
                    return m;
                }

                var left = m.PolicyPending.Where(o => !string.Equals(o, owner, StringComparison.Ordinal)).ToList();
                return m with { PolicyPending = left.Count == 0 ? null : left };
            }),
            cancellationToken);

    /// <summary>
    /// Removes the marks of ids (records remapped, or none left).
    /// </summary>
    /// <param name="ids">The ids.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    public Task UnmarkAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ChangeAsync(current => current.Values.Where(m => !ids.Contains(m.UserId)), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();

    private static Dictionary<Guid, OrphanMark> ToDictionary(IEnumerable<OrphanMark>? marks)
    {
        var result = new Dictionary<Guid, OrphanMark>();
        foreach (var mark in marks ?? [])
        {
            if (mark is not null && !mark.UserId.Equals(Guid.Empty))
            {
                result[mark.UserId] = mark;
            }
        }

        return result;
    }

    private static List<OrphanMark> Ordered(IEnumerable<OrphanMark> marks)
        => marks.OrderBy(m => m.MarkedUtc).ThenBy(m => KnownUsers.FormatId(m.UserId), StringComparer.Ordinal).ToList();

    // One Warning, without the stack trace (the store has logged its own Error with it), while the marks can't be read
    // or saved, worded by whether the plugin's settings page can start them empty.
    private void LogTrouble(PluginException ex, string what)
    {
        if (Interlocked.Exchange(ref _troubleLogged, 1) != 0)
        {
            _logger.LogDebug(ex, "The orphan marks in {File} {What}", _store.FilePath, what);
            return;
        }

        _logger.LogWarning(
            "The orphan marks in {File} {What} ({Reason}); the marks made from now on are kept in memory until the server restarts, and Remap by name and an import by name are refused, since the users deleted on this server aren't known. {WayOut}",
            _store.FilePath,
            what,
            ex.Message,
            _recoveryPage
                ? "An admin can start the list empty on the plugin's settings page (Data tab), or put a readable copy of the file back with the server stopped."
                : "Put a readable copy of the file back, or move it aside, with the server stopped.");
    }

    private async Task ChangeAsync(Func<IReadOnlyDictionary<Guid, OrphanMark>, IEnumerable<OrphanMark>> change, CancellationToken cancellationToken)
    {
        if (_fallback is { } fallback)
        {
            _fallback = ToDictionary(change(fallback));
            return;
        }

        try
        {
            await _store.UpdateAsync(
                document =>
                {
                    var current = ToDictionary(document.Orphans);
                    var next = Ordered(change(current));
                    return next.Count == current.Count && next.All(m => current.TryGetValue(m.UserId, out var mark) && ReferenceEquals(mark, m))
                        ? document
                        : document with { Orphans = next };
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PluginException ex)
        {
            // Read-only or unwritable: keep the marks of this process, so the reconciler still works until a restart.
            LogTrouble(ex, "can't be saved");
            IReadOnlyDictionary<Guid, OrphanMark> current;
            try
            {
                current = ToDictionary(_store.Current.Orphans);
            }
            catch (PluginException)
            {
                current = new Dictionary<Guid, OrphanMark>();
            }

            _fallback = ToDictionary(change(current));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save the orphan marks in {File}", _store.FilePath);
        }
    }
}
