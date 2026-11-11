using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// Bookmarks' part of the kit's import into <c>bookmarks.json</c>: records matched by id (<see cref="ImportMerge"/>), their
/// user ids mapped, and clip paths and folders from the export never trusted.
/// </summary>
/// <remarks>
/// <para><b>Clip files.</b> This server keeps its own <c>ClipRoots</c>: the export's are dropped, also the ones the 1→2
/// upgrade seeds from an older export's clip files. An imported record keeps its clip file path
/// only when <see cref="ClipFileGuard"/> accepts it against this server's clip folders, also while that clip folder isn't
/// there (an unplugged drive: the file is probably fine on it, and every later use checks the path again); otherwise its
/// file fields
/// (<c>FilePath</c>, <c>FileSize</c>, <c>FileStartTicks</c>, <c>FileEndTicks</c>, and <c>MissingSinceUtc</c>, which is about
/// the file) are dropped, and the report lists the record. A clip-only tombstone without a file left is not imported at all
/// (it is only its file). <c>PendingFileDeletes</c> are never imported: they are the exporting server's files. So an export
/// from a sandbox, or a crafted one, can't make this server delete or serve files outside its clip folders.</para>
/// <para>The other document fields stay this server's: the move-journal position, and the naming preferences and the Jellyfin
/// Enhanced import ledger, which this import doesn't bring; the report says when the export had any. <c>KnownUsers</c> is
/// refreshed for the records' owners with this server's names.</para>
/// <para><b>The archive</b> (since 1.1, with the admin import route): the export's archived bookmarks (whose video has
/// been gone longer than the retention) go into this server's archive through the kit (<c>DetachedRecords.ImportArchived</c>),
/// their users mapped like the live records', those neither live nor archived here already; one with a clip file (never
/// archived) or that isn't a detached bookmark on a library item is left out. Since 1.2.0 the archive also holds deleted
/// bookmarks whose clip file had been missing longer than <c>MissingClipRetentionDays</c> (reason <c>ClipMissing</c>): such
/// a record comes in with that reason and without its file's path, size and range (this server never had the file, and an
/// import never trusts an export's clip paths), its name, note and the time its file went missing kept. Since 1.2.0
/// the export carries the reason and its time next to the record (the kit's <c>DetachedRecords.ReadExported</c>); an export
/// made before carries neither, and a deleted bookmark with a "missing since" in it is then taken as one archived for its
/// missing clip. A reason this version doesn't know is left out. The archive is only ever added to, also by
/// <c>Replace</c>.</para>
/// <para><b>Clips being made</b> (since 1.1): an import that writes (not a dry run) is refused with 409 while a clip of
/// this server is queued or being made, since the import would change or remove the records those jobs report on. The admin
/// route checks it before the import's snapshot (so a refused import takes none) and this adapter again under
/// the store's lock in the write itself.</para>
/// <para><b>An export's clip jobs</b>: a record the export holds with its clip <c>Queued</c> or
/// <c>Running</c> (exported while that clip was being made) is imported with the job ended
/// (<see cref="BookmarkRecordRules.EndClipJobNoWorkerRuns"/>: <c>Failed</c> with <see cref="ImportedClipJobMessage"/>, at
/// the export's time, so importing the same export again changes nothing), since no clip worker would run it before the
/// server's next start; the report's note lists them.</para>
/// <para><b>Renamed clip files</b> (since 1.2.0): an export from before a clip's rename names its old path; an
/// imported record that names a path its file has left, Replace or Merge, names the file again (the record the data had
/// before, else the renames' history; never a file another record names), with a note counting them.</para>
/// </remarks>
internal sealed class BookmarkImportAdapter : JsonImportAdapter<BookmarkStoreDocument>
{
    /// <summary>
    /// The most record ids one note of the report lists.
    /// </summary>
    public const int MaxListedIds = 20;

    /// <summary>
    /// The 409 of an import or a restore while clips are being made.
    /// </summary>
    public const string ClipsBeingMadeMessage = "Clips are being made right now. Wait until they are done (or cancel them), then try again: this would change the bookmarks they belong to.";

    /// <summary>
    /// The error of an imported clip job, which its owner reads as "Clip failed: …".
    /// </summary>
    public const string ImportedClipJobMessage = "It was being made when these bookmarks were exported, and an import doesn't make clips. Make it again.";

    private static readonly IEqualityComparer<Bookmark> SameContent = ImportMerge.JsonContent<Bookmark>();

    private readonly IClipFileSystem _fileSystem;
    private readonly Func<IReadOnlyList<ArchivedRecord>, bool, IReadOnlyList<ArchivedRecord>>? _importArchived;
    private readonly bool _refuseWhileClipsAreMade;
    private readonly DateTime? _exportedUtc;
    private readonly Func<BookmarkStoreDocument, BookmarkStoreDocument, (BookmarkStoreDocument Document, int Relinked, int LetGo)>? _relink;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkImportAdapter"/> class over the real file system.
    /// </summary>
    public BookmarkImportAdapter()
        : this(PhysicalClipFileSystem.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkImportAdapter"/> class.
    /// </summary>
    /// <param name="fileSystem">The file system the clip path checks look at (reparse points).</param>
    /// <param name="importArchived">Adds archived records to this server's archive (<c>DetachedRecords.ImportArchived</c>
    /// for the bookmarks' owner: the records, and whether it is a dry run; returns those added), or <c>null</c> to import
    /// none, with a note saying so.</param>
    /// <param name="refuseWhileClipsAreMade">Whether <see cref="Apply"/> refuses (409) while a clip is queued or being made:
    /// for an import that writes.</param>
    /// <param name="exportedUtc">When the export was made (<c>ExportEnvelope.ExportedUtc</c>): when an imported clip job
    /// ended; now when <c>null</c>.</param>
    /// <param name="relink">Names the merged records' renamed clip files again (the document to write, and the document before
    /// the import; <c>ClipFileOps.RelinkRenamed</c>), and lets go of the files they would share with another record, for
    /// an import that writes; <c>null</c> relinks none.</param>
    public BookmarkImportAdapter(
        IClipFileSystem fileSystem,
        Func<IReadOnlyList<ArchivedRecord>, bool, IReadOnlyList<ArchivedRecord>>? importArchived = null,
        bool refuseWhileClipsAreMade = false,
        DateTime? exportedUtc = null,
        Func<BookmarkStoreDocument, BookmarkStoreDocument, (BookmarkStoreDocument Document, int Relinked, int LetGo)>? relink = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _importArchived = importArchived;
        _refuseWhileClipsAreMade = refuseWhileClipsAreMade;
        _exportedUtc = exportedUtc;
        _relink = relink;
    }

    /// <summary>
    /// Counts the live bookmarks whose clip is queued or being made: the jobs the clip worker runs (it never runs a
    /// tombstone's), so the ones an import or a restore waits for.
    /// </summary>
    /// <param name="bookmarks">The records.</param>
    /// <returns>How many.</returns>
    public static int CountClipsBeingMade(IEnumerable<Bookmark> bookmarks)
        => bookmarks.Count(b => b is not null && BookmarkRecordRules.IsLive(b) && BookmarkRecordRules.HasClipJob(b));

    /// <summary>
    /// Counts a document's records per user, tombstones included.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>User id to number of records.</returns>
    public override IReadOnlyDictionary<Guid, int> CountRecordsByUser(BookmarkStoreDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Bookmarks.Where(b => b is not null).GroupBy(b => b.UserId).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<Guid, int> CountArchivedRecordsByUser(IReadOnlyList<JsonObject> records, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .Select(r => r?["UserId"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty)
            .Where(id => !id.Equals(Guid.Empty))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc />
    public override ImportApplied<BookmarkStoreDocument> Apply(BookmarkStoreDocument current, BookmarkStoreDocument incoming, ImportMode mode, ImportUserMap users)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(users);
        if (_refuseWhileClipsAreMade && CountClipsBeingMade(current.Bookmarks) > 0)
        {
            throw PluginException.Conflict(ClipsBeingMadeMessage);
        }

        var records = new List<Bookmark>();
        var withoutFile = new List<Guid>();
        var tombstonesSkipped = new List<Guid>();
        var jobsEnded = new HashSet<Bookmark>(ReferenceEqualityComparer.Instance);
        var jobsEndedUtc = _exportedUtc ?? DateTime.UtcNow;
        foreach (var record in incoming.Bookmarks)
        {
            if (record is null || !users.TryMap(record.UserId, out var userId))
            {
                continue;
            }

            var mapped = record.UserId.Equals(userId) ? record : record with { UserId = userId };
            var fileDropped = false;
            if (mapped.Clip is { FilePath: { Length: > 0 } path } clip
                && ClipFileGuard.Check(path, current.ClipRoots, _fileSystem) is { IsInClipFolders: false, Verdict: not ClipPathVerdict.FolderUnavailable })
            {
                mapped = mapped with { Clip = WithoutFile(clip) };
                fileDropped = true;
            }

            if (BookmarkRecordRules.IsTombstone(mapped) && string.IsNullOrEmpty(mapped.Clip?.FilePath))
            {
                tombstonesSkipped.Add(mapped.Id);
                continue;
            }

            if (fileDropped)
            {
                withoutFile.Add(mapped.Id);
            }

            // A clip job of the exporting server's data: ended, since no worker here would run it.
            if (BookmarkRecordRules.HasClipJob(mapped))
            {
                var live = BookmarkRecordRules.IsLive(mapped);
                mapped = BookmarkRecordRules.EndClipJobNoWorkerRuns(mapped, ImportedClipJobMessage, jobsEndedUtc);
                if (live)
                {
                    jobsEnded.Add(mapped);
                }
            }

            records.Add(mapped);
        }

        var merged = mode == ImportMode.Replace
            ? ImportMerge.Replace(current.Bookmarks, records, b => b.Id, SameContent)
            : ImportMerge.Merge(current.Bookmarks, records, b => b.Id, b => b.UpdatedAt, SameContent);
        var counts = merged.Counts with
        {
            Incoming = merged.Counts.Incoming + tombstonesSkipped.Count,
            Skipped = tombstonesSkipped.Count
        };

        // The ended jobs this import writes: an incoming copy that was kept as stored (unchanged, or older) isn't one.
        var endedWritten = merged.Records.Where(jobsEnded.Contains).Select(b => b.Id).ToList();
        var notes = Notes(current, incoming, withoutFile, tombstonesSkipped, endedWritten);
        var known = current.KnownUsers.Refresh(
            merged.Records.Select(b => b.UserId),
            users.Mapped.GroupBy(m => m.NewId).ToDictionary(g => g.Key, g => g.First().Name));
        var changed = merged.Counts.Added + merged.Counts.Updated + merged.Counts.Removed > 0;
        if (!changed && known.Equals(current.KnownUsers))
        {
            return new ImportApplied<BookmarkStoreDocument>(current, counts, notes);
        }

        var written = BookmarkStore.WithBookmarks(current, merged.Records) with { KnownUsers = known };
        if (_relink is not null && changed)
        {
            // A record from before its clip file's rename names the renamed file again.
            var (relinked, count, letGo) = _relink(written, current);
            if (count + letGo > 0)
            {
                written = relinked;
            }

            if (count > 0)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture, "{0} clip(s) named a file that was renamed since the export; they name the renamed file again.", count));
            }

            if (letGo > 0)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture, "{0} clip(s) named a file another clip of this server has now (its name used again since the export); they keep no file, so two clips never share one, and can be made again.", letGo));
            }
        }

        return new ImportApplied<BookmarkStoreDocument>(written, counts, notes);
    }

    /// <inheritdoc />
    public override Task<ArchiveImportResult> ImportArchiveAsync(
        IReadOnlyList<JsonObject> records,
        int schemaVersion,
        ImportUserMap users,
        ImportMode mode,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(users);
        if (records.Count == 0 || _importArchived is null)
        {
            return base.ImportArchiveAsync(records, schemaVersion, users, mode, dryRun, cancellationToken);
        }

        // Archived records are in the store's shape at the export's schema version; only schema 2 has an archive (1.0 kept
        // none), and a later version's is refused with the envelope before this.
        if (schemaVersion != BookmarkStoreDocument.CurrentVersion)
        {
            return Task.FromResult(new ArchiveImportResult(
                records.Count,
                0,
                string.Format(CultureInfo.InvariantCulture, "The export's {0} archived bookmark(s) are at schema {1}, which this version doesn't read in an archive, so they were not imported.", records.Count, schemaVersion)));
        }

        var archived = new List<ArchivedRecord>();
        int unknownUser = 0, unusable = 0;
        foreach (var json in records)
        {
            // The kit's export carries an owner's reason next to the record (since 1.2.0), and when it began.
            var entry = json is null ? null : DetachedRecords.ReadExported(json);
            Bookmark? record;
            try
            {
                record = entry?.Record.Deserialize<Bookmark>(JsonStoreFormat.Options);
            }
            catch (JsonException)
            {
                record = null;
            }

            // An archive holds detached bookmarks on a library item without a clip file and, since 1.2.0, deleted bookmarks
            // whose clip file went missing (ClipMissing, the missing-clip sweep's retention); anything else is left out, a
            // reason this version doesn't know among it. An export made before 1.2.0 carries no reason: a
            // deleted bookmark with a "missing since" in it is one archived for its missing clip.
            if (record is null || record.Id.Equals(Guid.Empty))
            {
                unusable++;
                continue;
            }

            var reason = entry!.Reason
                ?? (BookmarkRecordRules.IsTombstone(record) && record.Clip?.MissingSinceUtc is not null ? MissingClipSweep.ArchiveReason : null);
            var detachedBookmark = reason is null && record.DetachedUtc is not null && BookmarkRecordRules.HasLibraryItem(record)
                && BookmarkRecordRules.IsLive(record) && string.IsNullOrEmpty(record.Clip?.FilePath);
            var missingSince = entry.ReasonSinceUtc ?? record.Clip?.MissingSinceUtc;
            var missingClip = reason == MissingClipSweep.ArchiveReason && BookmarkRecordRules.IsTombstone(record) && record.Clip is not null && missingSince is not null;
            if (!detachedBookmark && !missingClip)
            {
                unusable++;
                continue;
            }

            if (!users.TryMap(record.UserId, out var userId))
            {
                unknownUser++;
                continue;
            }

            var mapped = record.UserId.Equals(userId) ? record : record with { UserId = userId };
            archived.Add(detachedBookmark
                ? BookmarkIdentityService.ToArchived(mapped)
                : BookmarkIdentityService.ToArchived(
                    mapped with { Clip = mapped.Clip! with { FilePath = null, FileSize = null, FileStartTicks = null, FileEndTicks = null } },
                    MissingClipSweep.ArchiveReason,
                    missingSince!.Value));
        }

        var added = archived.Count == 0 ? [] : _importArchived(archived, dryRun);
        var notes = new List<string>();
        if (archived.Count > added.Count)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "{0} are here already, live or archived.", archived.Count - added.Count));
        }

        if (unknownUser > 0)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "{0} belong to users not found on this server.", unknownUser));
        }

        if (unusable > 0)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "{0} aren't archived bookmarks this version can take.", unusable));
        }

        string? note = null;
        if (notes.Count > 0)
        {
            note = string.Format(CultureInfo.InvariantCulture, "Of the export's {0} archived bookmark(s), {1} not imported: ", records.Count, records.Count - added.Count) + string.Join(" ", notes);
        }

        if (mode == ImportMode.Replace && records.Count > 0)
        {
            note = (note is null ? string.Empty : note + " ") + "The archive is only ever added to, also by Replace.";
        }

        return Task.FromResult(new ArchiveImportResult(records.Count, added.Count, note));
    }

    // The clip without its file: the job's state stays (only the file fields are dropped).
    private static ClipInfo WithoutFile(ClipInfo clip)
        => clip with { FilePath = null, FileSize = null, FileStartTicks = null, FileEndTicks = null, MissingSinceUtc = null };

    private static List<string> Notes(BookmarkStoreDocument current, BookmarkStoreDocument incoming, List<Guid> withoutFile, List<Guid> tombstonesSkipped, List<Guid> jobsEnded)
    {
        var notes = new List<string>();
        if (jobsEnded.Count > 0)
        {
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} clip(s) were being made when the export was made; an import doesn't make clips, so they are marked failed, and their owners can make them again ({1}).",
                jobsEnded.Count,
                ListIds(jobsEnded)));
        }

        if (withoutFile.Count > 0)
        {
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} record(s) kept without their clip file: the clip file path is outside this server's clip folders ({1}).",
                withoutFile.Count,
                ListIds(withoutFile)));
        }

        if (tombstonesSkipped.Count > 0)
        {
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} kept clip(s) of deleted bookmarks not imported: the clip file path is outside this server's clip folders ({1}).",
                tombstonesSkipped.Count,
                ListIds(tombstonesSkipped)));
        }

        var foreignRoots = incoming.ClipRoots.Count(r => !current.ClipRoots.Contains(r, ClipRootPaths.Comparer));
        if (foreignRoots > 0)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "The export's {0} other clip folder(s) were not imported; this server keeps its own.", foreignRoots));
        }

        if (incoming.PendingFileDeletes.Count > 0)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "The export's {0} clip file(s) waiting to be deleted were not imported: they are the exporting server's files.", incoming.PendingFileDeletes.Count));
        }

        if (incoming.UserPreferences.Count > 0 || incoming.ImportLedger.Count > 0)
        {
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "The export's naming preferences ({0} user(s)) and Jellyfin Enhanced import ledger ({1} entries) were not imported; this server keeps its own.",
                incoming.UserPreferences.Count,
                incoming.ImportLedger.Count));
        }

        return notes;
    }

    private static string ListIds(List<Guid> ids)
    {
        var listed = string.Join(", ", ids.Take(MaxListedIds).Select(id => id.ToString("D", CultureInfo.InvariantCulture)));
        return ids.Count > MaxListedIds ? listed + string.Format(CultureInfo.InvariantCulture, " and {0} more", ids.Count - MaxListedIds) : listed;
    }
}
