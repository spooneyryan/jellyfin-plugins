using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The clip list of the My Clips page: the records with a clip, live bookmarks and clip-only tombstones alike
/// (<see cref="IBookmarkStore.GetClipRecords"/>), filtered, ordered and paged in memory, and what each clip is right
/// now (<see cref="ClipFacts"/>).
/// </summary>
/// <remarks>
/// <para><b>Whose clips.</b> <see cref="Rows(Guid?)"/> takes one user's id (the caller's, never an id from the client) or
/// none, for the admins' view of every user's clips, behind its own gate.</para>
/// <para><b>What a clip is</b> is worked out on each request and never stored: being made (Queued, Running) is
/// <see cref="BookmarkClipState.InProgress"/>; otherwise the clip path is judged as <see cref="ClipFileGuard"/> judges it,
/// through <see cref="ClipStateCache"/> (the verdict of the last 30 s, else one worked out now): there is
/// <see cref="BookmarkClipState.Ready"/>, gone from a clip folder that is there <see cref="BookmarkClipState.Missing"/> (only
/// in a folder recognised as the clips' own by its marker file: in any other it is
/// <see cref="BookmarkClipState.FolderUnavailable"/> with <c>RootAvailable</c> true), and anything else (a clip folder
/// that isn't there or can't be read, a path outside the recorded clip folders, a reparse point on the way, no clip
/// file's name) is <see cref="BookmarkClipState.FolderUnavailable"/>: nothing can be done with that file from here, and
/// it is never taken for a file that is gone. A clip without a file is <see cref="BookmarkClipState.Failed"/>. A clip
/// whose file's rename isn't finished (<see cref="ClipStateCache.RenameUnfinished"/>, from the rename journal) is
/// <see cref="BookmarkClipState.RenameUnfinished"/>, whatever its old path shows.</para>
/// <para><b>Cost</b> (at most 200 <c>File.Exists</c> calls per request (page size), cached 30 s). The filters and the
/// orders read the records; the file system is asked, through the cache, only for the clips whose answer needs a file: the
/// page's (at most 200), those dated by their file for <see cref="BookmarkClipSortBy.Newest"/> and
/// <see cref="BookmarkClipSortBy.Oldest"/> (a kept clip whose job's time was lost, an earlier file next to a remake that
/// failed: every other finished clip is dated by its job's completion), and, for a <c>state</c> filter of
/// <see cref="BookmarkClipState.Ready"/>, <see cref="BookmarkClipState.Missing"/> or
/// <see cref="BookmarkClipState.FolderUnavailable"/>, every clip with a file it could match. The summary counts the missing and
/// offline clips, so it judges every clip with a file.</para>
/// </remarks>
internal sealed class ClipQueryService
{
    /// <summary>
    /// The page size of <see cref="Query"/> when the request gives none.
    /// </summary>
    public const int DefaultLimit = 50;

    // Titles sort as a person reads them: the invariant culture, ignoring case; the search also ignores accents.
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private static readonly StringComparer _textOrder = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
    private static readonly CompareInfo _search = CultureInfo.InvariantCulture.CompareInfo;

    // The states only a clip with a file can have: its file's.
    private static readonly IReadOnlySet<BookmarkClipState> _fileStates = new HashSet<BookmarkClipState>
    {
        BookmarkClipState.Ready,
        BookmarkClipState.Missing,
        BookmarkClipState.FolderUnavailable,
        BookmarkClipState.RenameUnfinished
    };

    private readonly IBookmarkStore _store;
    private readonly ClipStateCache _states;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipQueryService"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="states">The clip files' states, kept for 30 s.</param>
    public ClipQueryService(IBookmarkStore store, ClipStateCache states)
    {
        _store = store;
        _states = states;
    }

    /// <summary>
    /// Gets the clips of one user, or of every user, for one request.
    /// </summary>
    /// <param name="userId">The owner (the caller, for a user's request), or <c>null</c> for every user's.</param>
    /// <returns>The clips, in no particular order.</returns>
    public IReadOnlyList<ClipRow> Rows(Guid? userId) => Rows(_store.GetClipRecords(userId));

    /// <summary>
    /// Gets the clips of records that have one, for one request: they read the clip files together (one
    /// <see cref="ClipStateCache.Reading"/>).
    /// </summary>
    /// <param name="records">The records, each with its clip.</param>
    /// <returns>The clips, in the records' order.</returns>
    public IReadOnlyList<ClipRow> Rows(IEnumerable<Bookmark> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var reading = _states.Read();
        return records.Select(record => new ClipRow(record, this, reading)).ToList();
    }

    /// <summary>
    /// Filters, orders and pages clips (<c>GET /Bookmarks/Clips</c>): the clips being made first, whatever the
    /// order (the one running, then those waiting as they were asked for), then the others in the order asked for.
    /// </summary>
    /// <param name="rows">The clips to list, already the caller's.</param>
    /// <param name="filter">The filters and page.</param>
    /// <param name="sortBy">The order; <see cref="BookmarkClipSortBy.Newest"/> by default.</param>
    /// <param name="withNotes">Whether the bookmarks' notes take part: the owner's own list (the search matches the note, a
    /// clip without a name sorts by its note's first line); <c>false</c> for the admins' "All users" scope
    /// (admins never see a note, so no search or order may tell them anything of one), where a clip without a name sorts
    /// by its file's name.</param>
    /// <returns>The page and the number of clips that match.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for a search term over 100 characters, a negative start or a limit
    /// outside 1 to 200.</exception>
    public ClipPage Query(IReadOnlyList<ClipRow> rows, BookmarkClipQueryFilter? filter, BookmarkClipSortBy? sortBy, bool withNotes = true)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var criteria = Criteria.From(filter);
        var matches = rows.Where(row => Matches(row, criteria, withNotes)).ToList();
        var making = matches.Where(row => row.IsInProgress)
            .OrderBy(row => row.Clip.Status == BookmarkClipStatus.Running ? 0 : 1)
            .ThenBy(row => row.Clip.RequestedAt)
            .ThenBy(row => row.Record.Id);
        var ordered = making.Concat(Sort(matches.Where(row => !row.IsInProgress), sortBy ?? BookmarkClipSortBy.Newest, withNotes));
        var page = ordered.Skip(criteria.StartIndex).Take(criteria.Limit).ToList();
        return new ClipPage(page, matches.Count, criteria.StartIndex);
    }

    /// <summary>
    /// Counts clips by their states (<c>GET /Bookmarks/Clips/Summary</c>): every clip with a file is judged
    /// (through the cache), so the numbers and the list's rows agree.
    /// </summary>
    /// <param name="rows">The clips, already the caller's.</param>
    /// <returns>The numbers.</returns>
    public BookmarkClipSummaryDto Summarize(IReadOnlyList<ClipRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var summary = new BookmarkClipSummaryDto { Count = rows.Count };
        foreach (var row in rows)
        {
            var facts = row.Facts;
            switch (facts.State)
            {
                case BookmarkClipState.InProgress:
                    summary.InProgress++;
                    break;
                case BookmarkClipState.Failed:
                    summary.Failed++;
                    break;
                case BookmarkClipState.Missing:
                    summary.Missing++;
                    break;
                case BookmarkClipState.FolderUnavailable:
                    summary.FolderUnavailable++;
                    break;
                default:
                    break;
            }

            // The sizes the clips recorded, of every file not known to be gone (one in a folder that is offline is probably
            // still there, and so is a remake's previous file, and one whose rename isn't finished): no file system call of its
            // own.
            if ((facts.State == BookmarkClipState.RenameUnfinished || facts.Check is { Verdict: not ClipPathVerdict.Missing } || facts.Check is { RootUnrecognised: true })
                && row.Clip.FileSize is long size and > 0)
            {
                summary.TotalBytes += size;
            }
        }

        return summary;
    }

    /// <summary>
    /// Works out what a clip is right now, through a reading of its own (tests).
    /// </summary>
    /// <param name="record">The record, with its clip.</param>
    /// <returns>The clip's facts.</returns>
    internal ClipFacts Describe(Bookmark record) => Describe(record, _states.Read());

    /// <summary>
    /// Works out what a clip is right now, through the request's reading of the clip files.
    /// </summary>
    /// <param name="record">The record, with its clip.</param>
    /// <param name="reading">The request's reading.</param>
    /// <returns>The clip's facts.</returns>
    internal ClipFacts Describe(Bookmark record, ClipStateCache.Reading reading)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(reading);
        var clip = record.Clip ?? throw new ArgumentException("The record has no clip.", nameof(record));

        var check = string.IsNullOrEmpty(clip.FilePath) ? null : reading.Check(clip.FilePath);
        var fileExists = check?.Verdict == ClipPathVerdict.Exists;
        var state = clip.Status is BookmarkClipStatus.Queued or BookmarkClipStatus.Running ? BookmarkClipState.InProgress
            : check is null ? BookmarkClipState.Failed

            // A rename of its file that isn't finished, whatever its old path shows now: held, never missing.
            : _states.RenameUnfinished?.Invoke(record.Id) == true ? BookmarkClipState.RenameUnfinished
            : check.Verdict switch
            {
                ClipPathVerdict.Exists => BookmarkClipState.Ready,

                // Gone from a folder that is there but isn't the clips' own (no marker of its own): offline, not
                // missing, as the sweep and the worker treat it; RootAvailable stays true (the folder is there).
                ClipPathVerdict.Missing when check.RootUnrecognised => BookmarkClipState.FolderUnavailable,
                ClipPathVerdict.Missing => BookmarkClipState.Missing,
                _ => BookmarkClipState.FolderUnavailable
            };

        // A tombstone has no bookmark range to compare with ("as v1, false for tombstones").
        var outdated = check is not null && !BookmarkRecordRules.IsTombstone(record) && clip.FileStartTicks is long start
            && (start != record.StartTicks || clip.FileEndTicks != record.EndTicks);

        // Only a completed job's time is its file's; otherwise the file's own (a kept clip whose time was
        // dropped when a remake was asked for, the earlier file next to a remake that failed).
        var madeAt = CompletedAt(clip) ?? (fileExists ? reading.LastWriteTimeUtc(check!.FullPath!) : null);

        return new ClipFacts(
            state,
            fileExists,
            check is null ? null : check.IsInClipFolders,
            ContainerOf(clip.FilePath),
            DurationOf(clip),
            outdated,
            check is not null && IsNameOutdated(record, clip.FilePath!),
            clip.Status is BookmarkClipStatus.Failed or BookmarkClipStatus.Cancelled && fileExists,
            madeAt,
            check);
    }

    /// <summary>
    /// Gets a clip's length: its file's range when it is known (a 1.0 development build's clips had none), else the most
    /// recent job's; from the record alone.
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <returns>The length in ticks, never negative.</returns>
    internal static long DurationOf(ClipInfo clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Math.Max(
            0,
            !string.IsNullOrEmpty(clip.FilePath) && clip.FileStartTicks is long fileStart && clip.FileEndTicks is long fileEnd
                ? fileEnd - fileStart
                : clip.EndTicks - clip.StartTicks);
    }

    /// <summary>
    /// Gets a clip's title as the page shows it: the bookmark's name, else its note's first line, else the
    /// clip file's name without its extension; empty when it has none of them.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="withNote">Whether the note takes part (not in the admins' scope, where the page has no note).</param>
    /// <returns>The title.</returns>
    internal static string TitleOf(Bookmark record, bool withNote = true)
    {
        if (!string.IsNullOrWhiteSpace(record.Name))
        {
            return record.Name.Trim();
        }

        var line = withNote ? record.Note?.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null;
        return line ?? Path.GetFileNameWithoutExtension(record.Clip?.FilePath) ?? string.Empty;
    }

    // "Name changed" (IsNameOutdated): the file name's stem, the part before its range (and any " (2)" after it),
    // isn't what the bookmark's name gives now (BookmarkNames.ClipFileLabel: never its note since 1.2.0, so a file a bookmark
    // without a name got from its note before shows "Name changed", and Rename file gives it its video's name alone).
    // Compared as written (a change of case is a change); a name Bookmarks doesn't give a finished clip has nothing to
    // compare.
    private static bool IsNameOutdated(Bookmark record, string filePath)
        => ClipNamePattern.Stem(Path.GetFileName(filePath)) is { } stem
            && !string.Equals(stem, ClipFileNaming.BuildStem(record.ItemName, BookmarkNames.ClipFileLabel(record)), StringComparison.Ordinal);

    private static string? ContainerOf(string? path) => Path.GetExtension(path) switch
    {
        var extension when string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) => "mp4",
        var extension when string.Equals(extension, ".mkv", StringComparison.OrdinalIgnoreCase) => "mkv",
        _ => null
    };

    // The mode filter's view: a clip being made is the mode it is made in, any other clip with a file its file's.
    private static BookmarkClipMode ModeOf(ClipRow row)
        => !row.IsInProgress && row.Clip.FilePath is { Length: > 0 } path ? ClipFileNaming.GetMode(path) : row.Clip.Mode;

    private static bool Contains(string? text, string term) => text is not null && _search.IndexOf(text, term, SearchOptions) >= 0;

    // The cheap filters first; the state last. The record alone says a clip is being made or failed; only a clip with a file
    // whose state could match asks the file system (through the cache): none at all for a filter of InProgress or Failed. The
    // note is searched only in the owner's own list (never in the admins').
    private static bool Matches(ClipRow row, Criteria c, bool withNotes)
        => (c.IncludeDeleted || !row.IsTombstone)
            && (c.ItemId is not Guid item || row.Record.ItemId.Equals(item))
            && (c.Types is null || c.Types.Contains(BookmarkQueryService.TypeOf(row.Record)))
            && (c.Mode is not BookmarkClipMode mode || ModeOf(row) == mode)
            && (c.Term is not { } term
                || Contains(row.Record.Name, term) || (withNotes && Contains(row.Record.Note, term)) || Contains(row.Record.ItemName, term)
                || Contains(Path.GetFileName(row.Clip.FilePath), term))
            && (c.States is null
                || (row.KnownState is { } known ? c.States.Contains(known) : c.States.Overlaps(_fileStates) && c.States.Contains(row.Facts.State)));

    // A finished job's completion time, which is its file's; null for any other clip.
    private static DateTime? CompletedAt(ClipInfo clip)
        => clip.Status == BookmarkClipStatus.Completed && clip.CompletedAt is DateTime completed ? completed : null;

    // When the clip was made, for Newest and Oldest: its job's completion time, else its file's time (the only order that asks
    // the file system, through the cache, and only for these clips), else when it was last asked for.
    private static DateTime SortDate(ClipRow row)
        => CompletedAt(row.Clip) ?? (row.HasFile ? row.Facts.MadeAt : null) ?? row.Clip.RequestedAt;

    // Each sort in the direction its label names; every one ends on the id, so pages never overlap.
    private static IOrderedEnumerable<ClipRow> Sort(IEnumerable<ClipRow> rows, BookmarkClipSortBy by, bool withNotes) => by switch
    {
        BookmarkClipSortBy.Oldest => rows.OrderBy(SortDate).ThenBy(row => row.Record.Id),

        // Clips without a title (no name, note or file) after the others; in the admins' scope a note isn't a title.
        BookmarkClipSortBy.Name => rows.OrderBy(row => TitleOf(row.Record, withNotes).Length == 0 ? 1 : 0)
            .ThenBy(row => TitleOf(row.Record, withNotes), _textOrder)
            .ThenBy(row => row.Record.ItemName, _textOrder)
            .ThenBy(row => row.Clip.StartTicks)
            .ThenBy(row => row.Record.Id),
        BookmarkClipSortBy.ItemName => rows.OrderBy(row => row.Record.ItemName, _textOrder)
            .ThenBy(row => row.Record.ItemId)
            .ThenBy(row => row.Clip.StartTicks)
            .ThenBy(row => row.Record.Id),

        // Largest first; clips without a file (or a size) last.
        BookmarkClipSortBy.Size => rows.OrderBy(row => row.Clip.FilePath is { Length: > 0 } && row.Clip.FileSize is not null ? 0 : 1)
            .ThenByDescending(row => row.Clip.FileSize ?? 0)
            .ThenByDescending(SortDate)
            .ThenBy(row => row.Record.Id),
        BookmarkClipSortBy.Duration => rows.OrderByDescending(row => DurationOf(row.Clip))
            .ThenByDescending(SortDate)
            .ThenBy(row => row.Record.Id),
        _ => rows.OrderByDescending(SortDate).ThenBy(row => row.Record.Id),
    };

    /// <summary>
    /// A request's filters and page, checked (400 otherwise).
    /// </summary>
    private sealed record Criteria(
        string? Term,
        IReadOnlySet<BookmarkClipState>? States,
        BookmarkClipMode? Mode,
        IReadOnlySet<BookmarkItemType>? Types,
        bool IncludeDeleted,
        Guid? ItemId,
        int StartIndex,
        int Limit)
    {
        public static Criteria From(BookmarkClipQueryFilter? filter)
        {
            filter ??= new BookmarkClipQueryFilter();
            var term = filter.SearchTerm?.Trim();
            if (term?.Length > BookmarkQueryService.MaxSearchLength)
            {
                throw BookmarkException.Invalid(BookmarkQueryService.SearchTooLongMessage);
            }

            var startIndex = filter.StartIndex ?? 0;
            if (startIndex < 0)
            {
                throw BookmarkException.Invalid(BookmarkQueryService.StartIndexMessage);
            }

            var limit = filter.Limit ?? DefaultLimit;
            if (limit is < 1 or > BookmarkQueryService.MaxLimit)
            {
                throw BookmarkException.Invalid(BookmarkQueryService.LimitMessage);
            }

            return new Criteria(
                string.IsNullOrEmpty(term) ? null : term,
                filter.State is { Count: > 0 } states ? states.ToHashSet() : null,
                filter.Mode,
                filter.ItemTypes is { Count: > 0 } types ? types.ToHashSet() : null,
                filter.IncludeDeleted ?? true,
                filter.ItemId,
                startIndex,
                limit);
        }
    }
}
