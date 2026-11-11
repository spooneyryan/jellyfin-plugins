using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The missing-clip sweep of the daily "Bookmarks: Maintenance": marks the clip files that are gone from their clip
/// folder (<see cref="ClipInfo.MissingSinceUtc"/>), clears the mark of a file that is back, and after
/// <c>MissingClipRetentionDays</c> (30; 0 never; at most <see cref="MaxRetentionDays"/>) moves a deleted bookmark's
/// kept clip into the archive (the kit's <see cref="DetachedRecords.ArchiveForReasonAsync"/>, reason
/// <see cref="ArchiveReason"/>) and clears a bookmark's own clip entry, the bookmark staying. It never deletes, moves
/// or renames a clip file; the one file it writes is a clip folder's marker (below).
/// </summary>
/// <remarks>
/// <para><b>Every answer is fresh.</b> Each clip path is judged by <see cref="ClipFileGuard"/> through
/// <see cref="ClipFileOps.Check"/>, which asks the file system every time; the My Clips list's <see cref="ClipStateCache"/>,
/// which keeps answers for 30 s for display, is never used here.</para>
/// <para><b>A clip is marked missing only in a folder proven to be its own</b>. A folder's being there isn't enough: an
/// empty mount point, a share whose disk isn't mounted, another drive that got the same letter, the folder made again
/// while the real one is away (by Bookmarks 1.1's clip worker, among others, which may have written a clip into it), or
/// an older drive or backup copy back under the same path is there and holds none of the newer clips. So once the run
/// has looked at the files, it looks at each clip folder again, and judges its clips only when the folder is there with
/// its marker file (<see cref="ClipRootMarker"/>) naming the token the store records for it; a folder there without one
/// gets it (the backfill) when every one of its clips the run judged is there, which proves it, and is left out
/// otherwise: <see cref="ClipFolderLeftOut.NoMarker"/> with none of them there,
/// <see cref="ClipFolderLeftOut.Unproven"/> with some there and some gone (found in testing: a stray folder holding a
/// clip 1.1 wrote there while the real one was away proved it before). A marked folder in which every clip is missing
/// at once, at least <see cref="AllMissingThreshold"/> of them there before (not marked missing), is left out too: a
/// folder emptied or put back from an old copy, not files deleted one by one. A folder left out
/// (<see cref="ClipFolderLeftOut"/>: not there for one of its clips or at the second look, not recognised, unproven,
/// its marker not written, every clip gone) is left out as a whole: none of its clips is marked or cleared, none
/// archived or forgotten, and Diagnostics lists the folder with its clips, the reason and its
/// <see cref="ClipRootPaths.RootId"/> (<see cref="MissingClipSweepResult.UnavailableFolders"/>), where an admin can
/// confirm it as the clip folder whose clips are gone (<see cref="ConfirmFolderAsync"/>), which marks them. So a file's
/// "missing" always has its own clip folder found before and after it, and a drive unplugged while a run looks reads as
/// offline, not as files gone. A path the guard refuses or can't inspect is left as it is too.</para>
/// <para><b>The default clip folder deleted</b>: <c>clips</c> in the plugin's data folder can't be an unplugged drive
/// while that data folder is there, so a run that finds it gone with clips recorded in it makes it again with its
/// marker file and marks those clips missing first (<see cref="ClipFileOps.DeletedDefaultFolder"/>; the clip worker
/// does the same before a clip goes there).</para>
/// <para><b>A clip being made</b> (a job waiting or running) is left alone: its job decides its file, and a remake of a
/// missing clip may take the same name again. Retention skips it too.</para>
/// <para><b>Nothing because of a stale answer.</b> The marks are written in one store write that applies each only to a clip
/// still exactly as it was when its file was looked at (a clip made again, deleted or changed meanwhile is left for the next
/// run). Retention decides again under the store's lock, on the record as it is then, after one more fresh look at the file
/// and at its clip folder with its marker file; a deleted bookmark's record leaves the store only once the archive holds it,
/// flushed (the kit's order).</para>
/// <para>While the bookmark data is read-only nothing runs (<see cref="BackgroundWriteGate"/>). The log has counts only; a
/// clip folder's path only at Debug (Diagnostics, for admins, shows it).</para>
/// </remarks>
internal sealed class MissingClipSweep
{
    /// <summary>
    /// The archive reason of a deleted bookmark whose clip file has been missing longer than the retention.
    /// </summary>
    public const string ArchiveReason = "ClipMissing";

    /// <summary>
    /// The most days <c>MissingClipRetentionDays</c> counts (100 years): a larger setting counts as this (found in testing: a
    /// value above about 739,000 days put the cutoff before the year 1, and the retention threw at every run).
    /// </summary>
    public const int MaxRetentionDays = 36_500;

    /// <summary>
    /// How many of a marked clip folder's clips that weren't marked missing must be missing at once, with none of its clips
    /// there, for the run to leave the folder out (<see cref="ClipFolderLeftOut.AllMissing"/>) rather than mark them: two, so
    /// a folder's one clip deleted by hand is still marked.
    /// </summary>
    public const int AllMissingThreshold = 2;

    /// <summary>
    /// The 404 message of an admin's confirmation that names no recorded clip folder.
    /// </summary>
    public const string UnknownFolderMessage = "No recorded clip folder has that id; Diagnostics' ClipSweep entry names each folder it left out with its RootId.";

    private readonly IBookmarkStore _store;
    private readonly BackgroundWriteGate _gate;
    private readonly ClipFileOps _files;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly Func<DetachedRecords> _detached;
    private readonly Func<IDetachedRecordOwner> _archiveOwner;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private MissingClipSweepResult? _lastRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingClipSweep"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="gate">Keeps the sweep idle while the data is read-only.</param>
    /// <param name="files">The clip file operations, for their guard's fresh checks.</param>
    /// <param name="configuration">The settings (<c>MissingClipRetentionDays</c>).</param>
    /// <param name="detached">The kit's detached records, for the archive.</param>
    /// <param name="archiveOwner">The bookmarks' record owner, whose archive takes the records (<c>bookmarks</c>).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The lifecycle's clock (the kit's <c>Clock</c>, which a Debug build moves on by
    /// <c>DeveloperClockOffsetDays</c>); the system clock by default.</param>
    public MissingClipSweep(
        IBookmarkStore store,
        BackgroundWriteGate gate,
        ClipFileOps files,
        IPluginConfigurationProvider configuration,
        Func<DetachedRecords> detached,
        Func<IDetachedRecordOwner> archiveOwner,
        ILogger logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(detached);
        ArgumentNullException.ThrowIfNull(archiveOwner);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _gate = gate;
        _files = files;
        _configuration = configuration;
        _detached = detached;
        _archiveOwner = archiveOwner;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the last run since the server started, or <c>null</c>.
    /// </summary>
    public MissingClipSweepResult? LastRun
    {
        get
        {
            lock (_lock)
            {
                return _lastRun;
            }
        }
    }

    /// <summary>
    /// Runs the sweep once: looks at every clip with a file, marks and clears, then the retention.
    /// </summary>
    /// <param name="cancellationToken">Cancels between clips and waiting for the store.</param>
    /// <returns>What it did.</returns>
    public async Task<MissingClipSweepResult> RunAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var now = _clock.GetUtcNow().UtcDateTime;
        var days = RetentionDaysOf(_configuration.GetConfiguration());
        if (!_gate.MayWrite(BackgroundWriter.Maintenance))
        {
            return Remember(new MissingClipSweepResult { StartedUtc = now, RetentionDays = days, Skipped = "ReadOnly" });
        }

        // 0. The default clip folder deleted: inside the plugin's data folder, which is there, it can't be an unplugged
        // drive. It is made again with its marker file and its clips are marked missing, as an admin's confirmation
        // does, so the run judges it below (its clips marked already, the AllMissing guard doesn't apply).
        var (madeAgain, markedOnConfirm, markersOnConfirm) = await RecreateDeletedDefaultFolderAsync(cancellationToken).ConfigureAwait(false);

        // 1. Every clip with a file, its path judged afresh; a clip being made is its job's.
        var looks = new List<Look>();
        var inProgress = 0;
        foreach (var record in _store.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Clip is not { FilePath: { Length: > 0 } path } clip)
            {
                continue;
            }

            if (BookmarkRecordRules.HasClipJob(record))
            {
                inProgress++;
                continue;
            }

            looks.Add(new Look(record, clip, path, _files.Check(path)));
        }

        // 2. The clip folders the run leaves out: not there for one of their clips; and, looked at again now that their
        // files have been, not there any more, or there but not proven to be their clips' own folder (no marker file
        // with the token recorded for it, and their clips not all there to write it on: the backfill), or with every
        // clip gone at once. A clip's "missing" needs its own folder there before and after it.
        var (leftOut, markersWritten) = await FoldersToLeaveOutAsync(looks, cancellationToken).ConfigureAwait(false);
        markersWritten += markersOnConfirm;

        // 3. What each clip is, and the marks to write.
        var unavailable = new Dictionary<string, ClipFolderLeftOut>(ClipRootPaths.Comparer);
        var marks = new Dictionary<Guid, (ClipInfo Seen, DateTime? MissingSince)>();
        var missing = new List<Look>();
        int present = 0, unreadable = 0, refused = 0, marked = 0, cleared = 0;
        foreach (var look in looks)
        {
            if (look.Check.Root is { } root && leftOut.TryGetValue(root, out var reason))
            {
                unavailable[root] = new ClipFolderLeftOut((unavailable.GetValueOrDefault(root)?.Clips ?? 0) + 1, reason);
                continue;
            }

            switch (look.Check.Verdict)
            {
                case ClipPathVerdict.Exists:
                    present++;
                    if (look.Clip.MissingSinceUtc is not null)
                    {
                        marks[look.Record.Id] = (look.Clip, null);
                    }

                    break;
                case ClipPathVerdict.Missing:
                    missing.Add(look);
                    if (look.Clip.MissingSinceUtc is null)
                    {
                        marks[look.Record.Id] = (look.Clip, now);
                    }

                    break;
                case ClipPathVerdict.Unreadable:
                    unreadable++;
                    break;
                default:
                    refused++;
                    break;
            }
        }

        var result = new MissingClipSweepResult
        {
            StartedUtc = now,
            RetentionDays = days,
            Checked = looks.Count,
            Present = present,
            Missing = missing.Count,
            InProgress = inProgress,
            FolderUnavailable = unavailable.Values.Sum(f => f.Clips),
            UnavailableFolders = unavailable,
            MarkersWritten = markersWritten,
            FoldersMadeAgain = madeAgain,
            Marked = markedOnConfirm,
            Unreadable = unreadable,
            Refused = refused
        };

        try
        {
            if (marks.Count > 0)
            {
                await _store.UpdateDocumentAsync(
                    document =>
                    {
                        marked = cleared = 0;
                        var records = document.Bookmarks.Select(b =>
                        {
                            // Only a clip still exactly as it was looked at: one made again, deleted or changed since (or a
                            // record gone) is the next run's.
                            if (!marks.TryGetValue(b.Id, out var mark) || !Equals(b.Clip, mark.Seen))
                            {
                                return b;
                            }

                            if (mark.MissingSince is null)
                            {
                                cleared++;
                            }
                            else
                            {
                                marked++;
                            }

                            return b with { Clip = b.Clip! with { MissingSinceUtc = mark.MissingSince } };
                        }).ToList();
                        return marked + cleared == 0 ? document : document with { Bookmarks = records };
                    },
                    cancellationToken).ConfigureAwait(false);
                result = result with { Marked = markedOnConfirm + marked, Cleared = cleared, Changed = marks.Count - marked - cleared };
            }

            // 4. Retention, for the clips missing that long (marked by an earlier run): at least the retention's days.
            if (days > 0)
            {
                var cutoff = now - TimeSpan.FromDays(days);
                var due = missing.Where(l => l.Clip.MissingSinceUtc is { } since && since <= cutoff).ToList();
                var (forgotten, keptLive) = await ForgetClipsAsync(due.Where(l => BookmarkRecordRules.IsLive(l.Record)).ToList(), cutoff, cancellationToken).ConfigureAwait(false);
                result = result with { ClipsForgotten = forgotten, Changed = result.Changed + keptLive };
                var (archived, keptDeleted) = await ArchiveTombstonesAsync(due.Where(l => BookmarkRecordRules.IsTombstone(l.Record)).ToList(), cutoff, cancellationToken).ConfigureAwait(false);
                result = result with { Archived = archived, Changed = result.Changed + keptDeleted };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // What was written stays written; the maintenance logs the step's failure, and the next run looks again.
            Remember(result with { Milliseconds = watch.ElapsedMilliseconds, Error = ex.Message });
            throw;
        }

        result = Remember(result with { Milliseconds = watch.ElapsedMilliseconds });
        Log(result);
        return result;
    }

    /// <summary>
    /// Describes the sweep for Diagnostics (<c>ClipSweep</c>): the retention, the clips marked missing now with the oldest
    /// mark, and the last run since the server started.
    /// </summary>
    /// <returns>A plain dictionary: counts, times and (admins only) the clip folders that weren't there.</returns>
    public IDictionary<string, object?> Describe()
    {
        var marked = _store.GetAll().Select(b => b.Clip?.MissingSinceUtc).OfType<DateTime>().ToList();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["RetentionDays"] = RetentionDaysOf(_configuration.GetConfiguration()),
            ["MarkedMissing"] = marked.Count,
            ["OldestMissingSinceUtc"] = marked.Count == 0 ? null : marked.Min(),
            ["LastRun"] = LastRun?.ToDictionary()
        };
    }

    /// <summary>
    /// "This is the clip folder; its clips are gone": an admin confirms that a recorded clip folder (by its
    /// <see cref="ClipRootPaths.RootId"/>), left out by the sweep, is the one its clips were made in, and that those of
    /// its clips whose file isn't there are gone: the folder is made when it isn't there, given its marker file, and
    /// those clips are marked missing now (<see cref="ClipFileOps.ConfirmFolderAsync"/>), so the retention takes them
    /// in <c>MissingClipRetentionDays</c> as any (the clips' owners can forget them on the My Clips page before that).
    /// It is the remedy for a folder whose clips were all deleted by hand, a folder deleted as a whole, or a folder
    /// unproven since some of its clips are gone. The folder leaves the last run's list of folders left out. A dry run
    /// only counts. The caller checks the admin and the read-only data; logged at Information with the admin's id and
    /// the folder's id (the folder at Debug).
    /// </summary>
    /// <param name="rootId">The folder's id.</param>
    /// <param name="dryRun">Whether to only count.</param>
    /// <param name="adminId">The admin's user id, for the log.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was (or would be) done, with plain values.</returns>
    /// <exception cref="PluginException">404 for an id no recorded clip folder has; 503 when the folder couldn't be made
    /// or its marker couldn't be written (nothing marked then).</exception>
    public async Task<IDictionary<string, object?>> ConfirmFolderAsync(string? rootId, bool dryRun, Guid adminId, CancellationToken cancellationToken)
    {
        var root = FolderOf(rootId) ?? throw PluginException.NotFound(UnknownFolderMessage);
        var confirmation = await _files.ConfirmFolderAsync(root, dryRun, cancellationToken).ConfigureAwait(false);
        if (confirmation.Failure is { } failure)
        {
            throw PluginException.Unavailable(failure);
        }

        if (!dryRun)
        {
            _logger.LogInformation(
                "An admin ({UserId}) confirmed the clip folder {RootId} as the one its clips were made in and its gone clips as gone: {Created}, marker {Marker}, {Marked} clips marked missing ({Clips} recorded in it, {Present} there)",
                adminId,
                ClipRootPaths.RootId(root),
                confirmation.Created ? "made again" : "there",
                confirmation.Marker,
                confirmation.Newly,
                confirmation.Counts.Clips,
                confirmation.Counts.Present);
            _logger.LogDebug("The clip folder {RootId} that an admin confirmed is {Folder}", ClipRootPaths.RootId(root), root);
            lock (_lock)
            {
                if (_lastRun is { } last && last.UnavailableFolders.TryGetValue(root, out var left))
                {
                    var folders = last.UnavailableFolders.Where(f => !ClipRootPaths.Comparer.Equals(f.Key, root)).ToDictionary(f => f.Key, f => f.Value, ClipRootPaths.Comparer);
                    _lastRun = last with { UnavailableFolders = folders, FolderUnavailable = Math.Max(0, last.FolderUnavailable - left.Clips) };
                }
            }
        }

        return confirmation.ToDictionary(dryRun);
    }

    /// <summary>
    /// Gets the retention the sweep counts: <c>MissingClipRetentionDays</c>, 0 (never) for a negative value, at most
    /// <see cref="MaxRetentionDays"/>.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The days.</returns>
    internal static int RetentionDaysOf(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Math.Clamp(configuration.MissingClipRetentionDays, 0, MaxRetentionDays);
    }

    // A clip still as the record has it under the store's lock: not being made, its file the one looked at, marked missing
    // at or before the cutoff.
    private static bool StillDue(Bookmark current, Look look, DateTime cutoff)
        => !BookmarkRecordRules.HasClipJob(current)
            && current.Clip is { FilePath: { } path, MissingSinceUtc: { } since }
            && ClipPlanner.PathsEqual(path, look.Path)
            && since <= cutoff;

    // The file is still gone from its own clip folder: a fresh look at the file, then at its folder and the folder's marker
    // file, which nothing writes here, under the store's lock.
    private bool StillMissing(string path)
        => _files.Check(path) is { Verdict: ClipPathVerdict.Missing, Root: { } root } && _files.CheckRoot(root) == ClipRootState.Marked;

    /// <summary>
    /// Gets the recorded clip folder (normalised) a <see cref="ClipRootPaths.RootId"/> names, or <c>null</c> (the admin's
    /// confirmation asks it before its snapshot, so an unknown id changes nothing).
    /// </summary>
    /// <param name="rootId">The id, in any case.</param>
    /// <returns>The folder, or <c>null</c>.</returns>
    internal string? FolderOf(string? rootId)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            return null;
        }

        foreach (var candidate in _store.ClipRoots)
        {
            if (ClipRootPaths.TryNormalize(candidate, out var root) && ClipRootPaths.RootId(root).Equals(rootId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
        }

        return null;
    }

    // Step 2 of a run: the clip folders whose clips the run leaves as they are, with the reason, and how many markers it
    // wrote. A folder is judged only from what this run found in it, and only when one of its clips has a verdict to act on
    // (there or missing); one with only clips the guard refuses or can't inspect needs no look.
    private async Task<(Dictionary<string, string> LeftOut, int MarkersWritten)> FoldersToLeaveOutAsync(List<Look> looks, CancellationToken cancellationToken)
    {
        var leftOut = new Dictionary<string, string>(ClipRootPaths.Comparer);
        foreach (var look in looks.Where(l => l.Check.Verdict == ClipPathVerdict.FolderUnavailable && l.Check.Root is not null))
        {
            leftOut.TryAdd(look.Check.Root!, ClipFolderLeftOut.NotThere);
        }

        var markersWritten = 0;
        foreach (var folder in looks.Where(l => l.Check.Root is not null && !leftOut.ContainsKey(l.Check.Root!))
            .GroupBy(l => l.Check.Root!, ClipRootPaths.Comparer)
            .ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var judged = folder.Where(l => l.Check.Verdict is ClipPathVerdict.Exists or ClipPathVerdict.Missing).ToList();
            if (judged.Count == 0)
            {
                continue;
            }

            var anyThere = judged.Any(l => l.Check.Verdict == ClipPathVerdict.Exists);
            var anyGone = judged.Any(l => l.Check.Verdict == ClipPathVerdict.Missing);
            switch (_files.CheckRoot(folder.Key))
            {
                case ClipRootState.Absent:
                    leftOut[folder.Key] = ClipFolderLeftOut.NotThere;
                    continue;
                case ClipRootState.Unmarked when !anyThere:
                    leftOut[folder.Key] = ClipFolderLeftOut.NoMarker;
                    continue;
                case ClipRootState.Unmarked when anyGone:
                    // A clip there doesn't prove the folder while others are gone. A folder in the real one's place can
                    // hold a clip Bookmarks 1.1's worker wrote there while the real one was away (it made the folder
                    // again), and an older copy of the folder holds only the older clips. Left out until an admin
                    // confirms it (ConfirmFolderAsync) or every clip of it is there.
                    leftOut[folder.Key] = ClipFolderLeftOut.Unproven;
                    continue;
                case ClipRootState.Unmarked:
                case ClipRootState.Adoptable:
                    // Every clip of it that the run judged is there, so it is their folder: the marker proves it from now on
                    // (the backfill). A marker of an earlier build, which had no token, gets one (adopted).
                    var marking = await _files.WriteRootMarkerAsync(folder.Key, cancellationToken).ConfigureAwait(false);
                    if (marking == ClipRootMarking.Failed)
                    {
                        leftOut[folder.Key] = ClipFolderLeftOut.MarkerNotWritten;
                        continue;
                    }

                    markersWritten += marking == ClipRootMarking.Written ? 1 : 0;
                    break;
            }

            if (!anyThere && judged.Count(l => l.Clip.MissingSinceUtc is null) >= AllMissingThreshold)
            {
                leftOut[folder.Key] = ClipFolderLeftOut.AllMissing;
            }
        }

        return (leftOut, markersWritten);
    }

    // Step 0 of a run: the default clip folder, recorded and deleted (ClipFileOps.DeletedDefaultFolder) while clips not
    // being made are recorded in it, made again with its marker file and its clips marked missing; how many folders
    // were made, clips marked and markers written.
    private async Task<(int Made, int Marked, int Markers)> RecreateDeletedDefaultFolderAsync(CancellationToken cancellationToken)
    {
        if (_files.DeletedDefaultFolder() is not { } root || _files.CountClips(root).Gone == 0)
        {
            return (0, 0, 0);
        }

        var confirmation = await _files.ConfirmFolderAsync(root, dryRun: false, cancellationToken).ConfigureAwait(false);
        if (confirmation.Failure is not null)
        {
            // The step below finds it not there, and leaves it out (NotThere); the next run tries again.
            _logger.LogWarning("The default clip folder is gone and couldn't be made again; its clips are left as they are");
            return (0, 0, 0);
        }

        _logger.LogInformation(
            "The default clip folder was gone (deleted with its {Clips} clips): made again with its marker file, {Marked} clips marked missing",
            confirmation.Counts.Clips,
            confirmation.Newly);
        return (confirmation.Created ? 1 : 0, confirmation.Newly, confirmation.Marker == ClipRootMarking.Written ? 1 : 0);
    }

    // A live bookmark keeps itself, its name and its note, and drops the clip entry of a file gone that long.
    private async Task<(int Forgotten, int Kept)> ForgetClipsAsync(IReadOnlyList<Look> due, DateTime cutoff, CancellationToken cancellationToken)
    {
        if (due.Count == 0)
        {
            return (0, 0);
        }

        var byId = due.ToDictionary(l => l.Record.Id);
        var forgotten = 0;
        await _store.UpdateDocumentAsync(
            document =>
            {
                forgotten = 0;
                var records = document.Bookmarks.Select(b =>
                {
                    if (!byId.TryGetValue(b.Id, out var look) || !BookmarkRecordRules.IsLive(b) || !StillDue(b, look, cutoff) || !StillMissing(look.Path))
                    {
                        return b;
                    }

                    forgotten++;
                    return b with { Clip = null };
                }).ToList();
                return forgotten == 0 ? document : document with { Bookmarks = records };
            },
            cancellationToken).ConfigureAwait(false);

        // The others changed meanwhile (asked for again, deleted), or their file is back: the next run's.
        return (forgotten, byId.Count - forgotten);
    }

    // A deleted bookmark's kept clip whose file is gone that long moves into the archive with its name and note
    // (never hard-deleted on a timer), through the kit, which writes and flushes the archive before the record leaves.
    private async Task<(int Archived, int Kept)> ArchiveTombstonesAsync(IReadOnlyList<Look> due, DateTime cutoff, CancellationToken cancellationToken)
    {
        if (due.Count == 0)
        {
            return (0, 0);
        }

        var byId = due.ToDictionary(l => l.Record.Id);
        var kept = 0;
        var run = await _detached().ArchiveForReasonAsync(
            _archiveOwner(),
            ArchiveReason,
            async (append, token) =>
            {
                IReadOnlyList<ArchivedRecord> archived = [];
                await _store.UpdateDocumentAsync(
                    document =>
                    {
                        archived = [];
                        var leaving = document.Bookmarks
                            .Where(b => byId.TryGetValue(b.Id, out var look) && BookmarkRecordRules.IsTombstone(b) && StillDue(b, look, cutoff) && StillMissing(look.Path))
                            .ToList();
                        kept = byId.Count - leaving.Count;
                        if (leaving.Count == 0)
                        {
                            return document;
                        }

                        // Written and flushed first: if that throws, nothing leaves the store.
                        archived = append(leaving.Select(b => BookmarkIdentityService.ToArchived(b, ArchiveReason, b.Clip!.MissingSinceUtc!.Value)).ToList());
                        var gone = leaving.Select(b => b.Id).ToHashSet();
                        return document with { Bookmarks = document.Bookmarks.Where(b => !gone.Contains(b.Id)).ToList() };
                    },
                    token).ConfigureAwait(false);
                return archived;
            },
            cancellationToken).ConfigureAwait(false);
        return (run.Archived, kept);
    }

    private MissingClipSweepResult Remember(MissingClipSweepResult result)
    {
        lock (_lock)
        {
            _lastRun = result;
        }

        return result;
    }

    private void Log(MissingClipSweepResult result)
    {
        _logger.LogInformation(
            "Missing clip files: {Checked} clips checked, {Present} with their file, {Missing} missing from their clip folder ({Marked} newly, {Cleared} back); {InProgress} being made, {FolderUnavailable} in clip folders left out, {Unreadable} unreadable and {Refused} outside the clip folders left as they are; {Archived} deleted bookmarks' clips missing for {Days} days or more moved to the archive, {Forgotten} bookmarks' clip entries cleared; {Markers} clip folders given their marker file",
            result.Checked,
            result.Present,
            result.Missing,
            result.Marked,
            result.Cleared,
            result.InProgress,
            result.FolderUnavailable,
            result.Unreadable,
            result.Refused,
            result.Archived,
            result.RetentionDays,
            result.ClipsForgotten,
            result.MarkersWritten);
        if (result.UnavailableFolders.Count > 0)
        {
            var reasons = result.UnavailableFolders.Values.GroupBy(f => f.Reason, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            _logger.LogInformation(
                "{Folders} clip folders were left out ({NotThere} not there, {NoMarker} there but not recognised by their marker file, {Unproven} without their marker file and with some of their clips gone, {MarkerNotWritten} whose marker file couldn't be written, {AllMissing} with every clip gone at once): their {Clips} clips were neither checked nor marked; Diagnostics names them, and an admin can confirm one there",
                result.UnavailableFolders.Count,
                reasons.GetValueOrDefault(ClipFolderLeftOut.NotThere),
                reasons.GetValueOrDefault(ClipFolderLeftOut.NoMarker),
                reasons.GetValueOrDefault(ClipFolderLeftOut.Unproven),
                reasons.GetValueOrDefault(ClipFolderLeftOut.MarkerNotWritten),
                reasons.GetValueOrDefault(ClipFolderLeftOut.AllMissing),
                result.FolderUnavailable);
            foreach (var (folder, left) in result.UnavailableFolders)
            {
                _logger.LogDebug("The clip folder {Folder} was left out ({Reason}); its {Clips} clips were left as they are", folder, left.Reason, left.Clips);
            }
        }
    }

    /// <summary>
    /// One clip as the run looked at it: the record, its clip, the path and the guard's fresh verdict.
    /// </summary>
    private sealed record Look(Bookmark Record, ClipInfo Clip, string Path, ClipPathCheck Check);
}
