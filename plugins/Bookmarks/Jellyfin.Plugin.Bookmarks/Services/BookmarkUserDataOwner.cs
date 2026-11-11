using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The bookmark store as the kit's <see cref="IUserDataOwner"/>: the deleted-user policy on
/// Jellyfin's <c>UserDeletedEventArgs</c> and the admin's <i>Apply deleted-user policy</i>, the reconciler's counts and names
/// (it never changes a record), and <i>Remap by name</i>.
/// </summary>
/// <remarks>
/// <para><b>Only an explicit deletion changes records</b> (the kit calls <see cref="HandleUserDeletedAsync"/> only then,
/// after a <c>user-deleted</c> or <c>apply-policy</c> pre-operation snapshot). Bookmarks' default policy,
/// <see cref="DeletedUserPolicy.KeepAsOrphan"/> (and <see cref="DeletedUserPolicy.Anonymize"/>, which behaves the same):
/// the user's bookmarks without a clip file are deleted; those with one become clip-only tombstones owned by the deleted
/// id, which only the admins' clip view (since 1.2.0) shows, as "Deleted user (id)".
/// <see cref="DeletedUserPolicy.Delete"/> removes every record of the user and deletes their clip files through
/// <see cref="ClipFileOps"/> after the write, an automatic deletion, so a file in use is listed in
/// <c>PendingFileDeletes</c> and retried. Either way a running clip job of the user is stopped (its worker records
/// nothing on a removed record or a tombstone and discards its output), and the user's naming preferences go; the
/// user's name stays in <c>KnownUsers</c> while records of the id remain.</para>
/// <para><b>No name or note is left</b> (since Bookmarks 1.2.1): every policy strips the name and the note (and the fields
/// of a later version) from the user's tombstones, those it makes and those there already; removes every record of the
/// user from the kit's archive (<c>archive/bookmarks-archived.jsonl</c>, whatever the policy: an archived bookmark has no
/// clip file to keep) with the archive's earlier lines (the file is rewritten, after a <c>user-deleted</c> copy in the
/// snapshot folder); forgets the renames of every clip file of the user; and writes the store once more, so that its
/// <c>.bak</c> holds no earlier version. Snapshots taken afterwards hold none of it; the pre-operation snapshots taken just
/// before are the way back, and rotate as usual. A policy or a remap that would change the archive while it can't be read
/// is refused (503) before anything is written, and the kit tries the policy again at the next reconciliation. The counts
/// (<see cref="CountRecordsByUser"/>) include the archived records, so a user whose only bookmarks are archived is handled
/// too, and throw while the archive can't be read (the kit then skips this owner); <i>Remap by name</i> re-appends the
/// archived ones under the new id.</para>
/// <para>While the store is read-only the kit leaves the owner alone (<see cref="IsReadOnly"/>), and every write asks
/// <see cref="BackgroundWriteGate"/> first.</para>
/// </remarks>
internal sealed class BookmarkUserDataOwner : IUserDataOwner
{
    /// <summary>The message of the 503 a policy or a remap gets while the archive can't be read.</summary>
    public const string ArchiveUnavailableMessage = "The archive of detached bookmarks can't be read now, so nothing was changed. The plugin's Diagnostics (Detached) say why; try again once it can be.";

    private readonly IBookmarkStore _store;
    private readonly BackgroundWriteGate _gate;
    private readonly ClipFileOps _files;
    private readonly Func<IClipExtractionService> _clips;
    private readonly string _snapshotDirectory;
    private readonly Func<DetachedRecords>? _detached;
    private readonly Func<IDetachedRecordOwner>? _archiveOwner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkUserDataOwner"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="gate">The read-only gate of automatic writers.</param>
    /// <param name="files">The clip file operations, for the <c>Delete</c> policy.</param>
    /// <param name="clips">The clip worker, to stop a deleted user's running job; resolved when a user is deleted.</param>
    /// <param name="snapshotDirectory">The snapshot folder (<c>BookmarkPaths.SnapshotsDirectory</c>).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="detached">The kit's detached records, whose archive holds the archived bookmarks; no archive without
    /// it.</param>
    /// <param name="archiveOwner">The owner of the archived bookmarks (<see cref="BookmarkIdentityService"/>).</param>
    public BookmarkUserDataOwner(
        IBookmarkStore store,
        BackgroundWriteGate gate,
        ClipFileOps files,
        Func<IClipExtractionService> clips,
        string snapshotDirectory,
        ILogger logger,
        TimeProvider? timeProvider = null,
        Func<DetachedRecords>? detached = null,
        Func<IDetachedRecordOwner>? archiveOwner = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _gate = gate;
        _files = files;
        _clips = clips;
        _snapshotDirectory = snapshotDirectory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _detached = detached;
        _archiveOwner = archiveOwner;
    }

    /// <inheritdoc />
    public string Name => BookmarkIdentityService.StoreName;

    /// <inheritdoc />
    public bool IsReadOnly => _store.IsReadOnly;

    /// <inheritdoc />
    public KnownUsers KnownUsers => _store.KnownUsers;

    /// <summary>
    /// The user an archived bookmark belongs to: its record's <c>UserId</c>, or the archive line's.
    /// </summary>
    /// <param name="record">The archived record.</param>
    /// <returns>The user, or null.</returns>
    internal static Guid? ArchivedUserIdOf(ArchivedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Record.TryGetPropertyValue(nameof(Bookmark.UserId), out var node) && node is JsonValue value
            && (value.TryGetValue<Guid>(out var id) || (value.TryGetValue<string>(out var text) && Guid.TryParse(text, out id))))
        {
            return id == Guid.Empty ? null : id;
        }

        return record.UserId is { } line && line != Guid.Empty ? line : null;
    }

    /// <summary>
    /// A deleted user's tombstone as it is kept: its clip file, without the name, the note or the fields of a later version.
    /// </summary>
    /// <param name="tombstone">The tombstone.</param>
    /// <returns>The tombstone without them; the same instance when it has none.</returns>
    internal static Bookmark WithoutText(Bookmark tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        return tombstone.Name is null && tombstone.Note is null && tombstone.Extra is null
            ? tombstone
            : tombstone with { Name = null, Note = null, Extra = null };
    }

    /// <inheritdoc />
    /// <remarks>The live records and the archived ones; throws (503) while the archive can't be read, never leaving them
    /// out.</remarks>
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser()
    {
        var counts = _store.GetAll().Where(b => !b.UserId.Equals(Guid.Empty)).GroupBy(b => b.UserId).ToDictionary(g => g.Key, g => g.Count());
        if (ArchiveToWrite() is { } archive)
        {
            foreach (var id in archive.Read().Records.Select(ArchivedUserIdOf).OfType<Guid>())
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    /// <inheritdoc />
    /// <remarks>Every policy changes the records of a user who has some, live or archived, but for tombstones without a name
    /// or a note.</remarks>
    public bool WouldChangeRecords(Guid userId, DeletedUserPolicy policy)
    {
        if (_store.GetAll().Any(b => b.UserId.Equals(userId) && (policy == DeletedUserPolicy.Delete || !BookmarkRecordRules.IsTombstone(b) || !ReferenceEquals(WithoutText(b), b))))
        {
            return true;
        }

        try
        {
            return ArchiveToWrite()?.Read().Records.Any(r => ArchivedUserIdOf(r) == userId) == true;
        }
        catch (PluginException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public async Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentNames);
        if (_gate.MayWrite(BackgroundWriter.Maintenance))
        {
            await _store.RefreshKnownUsersAsync(currentNames, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
        => await _store.Documents.PreOpSnapshotAsync(_snapshotDirectory, operation, cancellationToken).ConfigureAwait(false) is { } path
            ? System.IO.Path.GetFileName(path)
            : null;

    /// <inheritdoc />
    public async Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken)
    {
        if (userId.Equals(Guid.Empty) || !_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return 0;
        }

        // The archive is read first: a policy that can't reach it changes nothing, and is tried again (503).
        var archive = ArchiveToWrite();
        var archivedIds = archive?.Read().Records.Where(r => ArchivedUserIdOf(r) == userId).Select(r => r.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var removed = new List<Bookmark>();
        var tombstoned = new List<Guid>();
        var stripped = new List<Guid>();
        await _store.UpdateDocumentAsync(
            document =>
            {
                removed.Clear();
                tombstoned.Clear();
                stripped.Clear();
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var records = new List<Bookmark>(document.Bookmarks.Count);
                foreach (var bookmark in document.Bookmarks)
                {
                    if (!bookmark.UserId.Equals(userId))
                    {
                        records.Add(bookmark);
                    }
                    else if (policy == DeletedUserPolicy.Delete)
                    {
                        removed.Add(bookmark);
                    }
                    else if (BookmarkRecordRules.IsTombstone(bookmark))
                    {
                        // A tombstone there already keeps its clip file, and loses its name and note (since 1.2.1).
                        var kept = WithoutText(bookmark);
                        records.Add(kept);
                        if (!ReferenceEquals(kept, bookmark))
                        {
                            stripped.Add(bookmark.Id);
                        }
                    }
                    else if (BookmarkRecordRules.ToTombstone(bookmark, now) is { } tombstone)
                    {
                        // KeepAsOrphan: a bookmark with a clip file keeps it, as a tombstone of the deleted id.
                        records.Add(WithoutText(tombstone));
                        tombstoned.Add(bookmark.Id);
                    }
                    else
                    {
                        removed.Add(bookmark);
                    }
                }

                var preferences = document.UserPreferences.ContainsKey(userId)
                    ? document.UserPreferences.ToImmutableDictionary().Remove(userId)
                    : document.UserPreferences;
                if (removed.Count == 0 && tombstoned.Count == 0 && stripped.Count == 0 && ReferenceEquals(preferences, document.UserPreferences))
                {
                    return document;
                }

                return document with
                {
                    Bookmarks = records,
                    UserPreferences = preferences,
                    KnownUsers = records.Exists(b => b.UserId.Equals(userId)) ? document.KnownUsers : document.KnownUsers.Without(userId)
                };
            },
            cancellationToken).ConfigureAwait(false);

        // A job claimed before the write still runs: stop it. Its worker records nothing on a removed record or a tombstone,
        // and discards the file it may still finish.
        foreach (var id in removed.Select(b => b.Id).Concat(tombstoned))
        {
            _clips().StopRunning(id);
        }

        // Delete: the clip files, now that no record holds them; one in use is listed in PendingFileDeletes and retried. One
        // another record still names is kept for it (Bookmarks 1.1.3; ClipFileOps' Warning), and isn't counted.
        var files = 0;
        var queued = 0;
        foreach (var bookmark in removed.Where(b => !string.IsNullOrEmpty(b.Clip?.FilePath)))
        {
            var result = await _files.DeleteOrQueueAsync(bookmark.Clip!.FilePath, bookmark.Id, BackgroundWriter.IdentityService, cancellationToken).ConfigureAwait(false);
            files += result.Outcome is ClipFileOutcome.Done or ClipFileOutcome.Missing ? 1 : 0;
            queued += result.Queued ? 1 : 0;
        }

        // The archive: every record of the user and the earlier lines of them (rewritten after a user-deleted copy), so no
        // name or note is left there; one archived since the read above (a retention run racing this) goes too.
        var archived = 0;
        if (archive is not null)
        {
            var ids = removed.Select(b => b.Id).Concat(tombstoned).Concat(stripped).Select(Key).ToHashSet(StringComparer.Ordinal);
            ids.UnionWith(archivedIds);
            try
            {
                archived = archive.Purge(
                    r => ArchivedUserIdOf(r) == userId || ids.Contains(r.Id),
                    _detached!().SnapshotDirectory,
                    SnapshotOperations.UserDeleted,
                    includeHistory: true).Purged;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "The archived bookmarks of deleted user {UserId} couldn't be removed; the policy is tried again at the next reconciliation", userId);
                throw PluginException.Unavailable(ArchiveUnavailableMessage);
            }
        }

        // Their clip files' renames go with them, the tombstones' too (the history's paths can start with a
        // note's words), and the archived ones'.
        _files.ForgetRenames(removed.Select(b => b.Id).Concat(tombstoned).Concat(stripped).Concat(archivedIds.Select(ParseKey).OfType<Guid>()));

        // The deletion's write left the earlier document in the .bak: write once more, so no earlier version is left.
        if (removed.Count + tombstoned.Count + stripped.Count > 0 && !await _store.Documents.RefreshBackupAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("The backup of the bookmark store couldn't be refreshed after deleted user {UserId}; it may hold their bookmarks until the next write", userId);
        }

        _logger.LogInformation(
            "Deleted user {UserId} ({Policy}): {Removed} bookmarks removed, {Tombstoned} kept as clip-only tombstones, {Stripped} tombstones' names and notes removed, {Archived} archived ones removed, {Files} clip files deleted, {Queued} to be deleted later",
            userId,
            policy,
            removed.Count,
            tombstoned.Count,
            stripped.Count,
            archived,
            files,
            queued);
        return removed.Count + tombstoned.Count + stripped.Count + archived;
    }

    /// <inheritdoc />
    public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Count == 0 || !_gate.MayWrite(BackgroundWriter.Maintenance))
        {
            return 0;
        }

        var archive = ArchiveToWrite();
        var moved = 0;
        await _store.UpdateDocumentAsync(
            document =>
            {
                moved = 0;
                var records = document.Bookmarks.Select(b =>
                {
                    if (map.TryGetValue(b.UserId, out var newId) && !newId.Equals(b.UserId))
                    {
                        moved++;
                        return b with { UserId = newId };
                    }

                    return b;
                }).ToList();

                // Naming preferences follow, unless the new id has its own.
                var preferences = document.UserPreferences.ToImmutableDictionary();
                foreach (var (oldId, newId) in map)
                {
                    if (!oldId.Equals(newId) && preferences.TryGetValue(oldId, out var kept))
                    {
                        preferences = preferences.Remove(oldId);
                        if (!preferences.ContainsKey(newId))
                        {
                            preferences = preferences.Add(newId, kept);
                        }
                    }
                }

                return moved == 0 && preferences.Count == document.UserPreferences.Count && preferences.Keys.All(document.UserPreferences.ContainsKey)
                    ? document
                    : document with { Bookmarks = records, UserPreferences = preferences, KnownUsers = document.KnownUsers.Remap(map) };
            },
            cancellationToken).ConfigureAwait(false);

        // The archived ones are re-appended under the new id; the later line supersedes the earlier (since 1.2.1).
        if (archive is not null)
        {
            var rekeyed = new List<ArchivedRecord>();
            foreach (var record in archive.Read().Records)
            {
                if (ArchivedUserIdOf(record) is { } oldId && map.TryGetValue(oldId, out var newId) && !newId.Equals(oldId) && !newId.Equals(Guid.Empty))
                {
                    var json = record.Record.DeepClone().AsObject();
                    json[nameof(Bookmark.UserId)] = JsonValue.Create(newId);
                    rekeyed.Add(record with { UserId = newId, Record = json });
                }
            }

            if (rekeyed.Count > 0)
            {
                try
                {
                    archive.Append(rekeyed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "The archived bookmarks of remapped users couldn't be re-appended under their new ids");
                    throw PluginException.Unavailable(ArchiveUnavailableMessage);
                }

                moved += rekeyed.Count;
            }
        }

        return moved;
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static Guid? ParseKey(string id) => Guid.TryParse(id, out var guid) ? guid : null;

    // The archive of the archived bookmarks; null without one. Throws 503 while it can't be reached or read.
    private DetachedArchive? ArchiveToWrite()
    {
        if (_detached is null || _archiveOwner is null)
        {
            return null;
        }

        DetachedArchive archive;
        try
        {
            archive = _detached().ArchiveOf(_archiveOwner());
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PluginException)
        {
            _logger.LogError(ex, "The archive of detached bookmarks can't be reached");
            throw PluginException.Unavailable(ArchiveUnavailableMessage);
        }

        if (archive.Read().Problem is { } problem)
        {
            _logger.LogWarning("The archive of detached bookmarks can't be read ({Problem}); a deleted-user policy or a remap is refused", problem);
            throw PluginException.Unavailable(ArchiveUnavailableMessage);
        }

        return archive;
    }
}
