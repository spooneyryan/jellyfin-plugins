using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Interop;

namespace Jellyfin.Plugin.Bookmarks.Import;

/// <summary>
/// The decisions of the Jellyfin Enhanced import over a bookmark document, the files read and the videos found: what each
/// JE bookmark becomes, the ledger entries and the applied-import entries. It asks nothing of the library or the disk, so
/// a dry run plans over the current document and an applied import plans again under the store's lock, over the document
/// it then writes.
/// </summary>
/// <remarks>
/// <para><b>The ledger</b> (<c>je:&lt;userIdN&gt;:&lt;JE key&gt;</c>) makes a second run a no-op: an entry it lists is
/// "already imported" unless JE's copy changed since (its <c>UpdatedAt</c>, else its <c>CreatedAt</c>, is later than the
/// ledger's) and ours didn't (the bookmark still has the <c>UpdatedAt</c> the import gave it): then it is rewritten from
/// JE's entry. A bookmark changed or deleted here since its import is never touched or brought back: the local edit wins.
/// A duplicate is recorded too, so it stays one.</para>
/// <para><b>Duplicates</b>: the same owner, the same video, within a second and the same name (no name equals
/// no name) as one of the owner's bookmarks, or as a JE bookmark this run imports before it.</para>
/// <para><b>The record</b>: the owner's id, the matched video (or, detached, JE's item id with what JE knew of it), the
/// position <c>round(Timestamp × 10,000,000)</c> (past a known runtime: a second before the end, reported), the label as
/// the name (<see cref="BookmarkNames.FromLabel"/>), no end, media source or audio track, JE's <c>CreatedAt</c> and
/// <c>UpdatedAt</c>, and <c>Origin = JellyfinEnhanced</c>, which gives it the identity service's import relink policy
/// (one Strong candidate without a stored runtime).</para>
/// </remarks>
internal static class JeImportPlanner
{
    /// <summary>The most sample rows a report holds.</summary>
    public const int MaxSamples = 50;

    /// <summary>The ledger outcome of an imported entry.</summary>
    public const string ImportedOutcome = "Imported";

    /// <summary>The ledger outcome of an entry left out as a duplicate.</summary>
    public const string DuplicateOutcome = "Duplicate";

    /// <summary>The largest position read, in seconds (about 31 years): a larger one is malformed.</summary>
    public const double MaxSeconds = 1e9;

    // The longest label a sample row shows.
    private const int MaxSampleLabel = 200;

    private enum Step
    {
        Skip,
        Already,
        Update,
        New
    }

    /// <summary>
    /// Gets the entries whose video must be found: the ones the plan would import, or rewrite from a changed JE entry.
    /// </summary>
    /// <param name="document">The document the plan will be made over.</param>
    /// <param name="sources">The files read.</param>
    /// <returns>The entries' ledger keys.</returns>
    public static IReadOnlySet<string> ToResolve(BookmarkStoreDocument document, IReadOnlyList<JeImportSource> sources)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sources);
        var records = Index(document);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Once(sources).Where(s => s.Owner is not null && s.File.Format == JeDataFormat.Bookmarks1))
        {
            foreach (var entry in source.File.Entries)
            {
                if (Decide(document, records, source, entry).Step is Step.New or Step.Update)
                {
                    keys.Add(source.LedgerKey(entry.Key));
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Plans the import of the given files over a document.
    /// </summary>
    /// <param name="document">The bookmark document.</param>
    /// <param name="sources">The files read, in report order.</param>
    /// <param name="resolve">The video found for an entry, by its ledger key; <c>null</c> when it wasn't looked up.</param>
    /// <param name="now">The time of the import (UTC).</param>
    /// <param name="appliedBy">The admin applying it, for the applied-import entries; <c>null</c> for an API key (and a dry
    /// run, which writes none).</param>
    /// <returns>The plan.</returns>
    public static JeImportPlan Plan(BookmarkStoreDocument document, IReadOnlyList<JeImportSource> sources, Func<string, JeEntryResolution?> resolve, DateTime now, Guid? appliedBy)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(resolve);

        var records = Index(document);
        var users = new List<BookmarkJeImportUserDto>();
        var rows = new List<(int Rank, int Order, BookmarkJeImportSampleDto Sample)>();
        var added = new List<Bookmark>();
        var replaced = new List<Bookmark>();
        var ledger = new Dictionary<string, BookmarkImportLedgerEntry>(StringComparer.Ordinal);
        var runs = new Dictionary<string, BookmarkImportRun>(StringComparer.Ordinal);
        var owners = new Dictionary<Guid, string>();
        var order = 0;

        void Sample(JeImportSource source, int rank, string key, string? label, BookmarkJeImportOutcome outcome, string? itemName, long? start, string reason)
            => rows.Add((rank, order++, new BookmarkJeImportSampleDto
            {
                JeUserId = source.JeUserKey,
                UserName = source.Owner?.Username,
                JeKey = key,
                Label = label is null ? null : Cut(label, MaxSampleLabel),
                ItemName = itemName,
                StartTicks = start,
                Outcome = outcome,
                Reason = reason
            }));

        foreach (var source in Once(sources))
        {
            var owner = source.Owner;
            var who = owner?.Username is { Length: > 0 } username ? username : source.JeUserKey;
            var row = new BookmarkJeImportUserDto { JeUserId = source.JeUserKey, UserId = owner?.Id, UserName = owner?.Username };
            var errors = new List<string>();
            users.Add(row);
            var file = source.File;
            if (file.Format != JeDataFormat.Bookmarks1)
            {
                errors.Add(file.Format switch
                {
                    JeDataFormat.Unreadable => $"Couldn't read the Jellyfin Enhanced bookmarks of {who} ({file.Problem}). Try again; nothing was imported for them.",
                    JeDataFormat.Missing => $"The Jellyfin Enhanced bookmarks of {who} are gone (the file was deleted or moved meanwhile); nothing was imported for them.",
                    _ => $"{file.Problem ?? "Unsupported Jellyfin Enhanced format."} Nothing was imported for {who}; a later Bookmarks may read it."
                });
                row.Errors = errors;
                continue;
            }

            row.Found = file.Entries.Count + file.Invalid.Count;
            if (owner is null)
            {
                errors.Add($"No user on this server has the id {source.JeUserKey}; its {row.Found} Jellyfin Enhanced bookmark(s) are skipped.");
            }

            foreach (var problem in file.Invalid)
            {
                row.Skipped++;
                Sample(source, 0, problem.Key, null, BookmarkJeImportOutcome.Skipped, null, null, "Malformed: " + problem.Reason);
            }

            // The owner's bookmarks as this run leaves them, for the duplicate check.
            var mine = owner is null
                ? []
                : records.Values.Where(b => BookmarkRecordRules.IsLiveBookmarkOf(b, owner.Id)).ToDictionary(b => b.Id);
            var changes = 0;
            foreach (var entry in file.Entries)
            {
                var key = source.LedgerKey(entry.Key);
                var decision = Decide(document, records, source, entry);
                if (decision.Step == Step.Skip)
                {
                    row.Skipped++;
                    Sample(source, 0, entry.Key, entry.Label, BookmarkJeImportOutcome.Skipped, entry.Name, null, decision.Reason);
                    continue;
                }

                if (decision.Step == Step.Already)
                {
                    row.AlreadyImported++;
                    Sample(source, decision.LocalChange ? 7 : 9, entry.Key, entry.Label, BookmarkJeImportOutcome.AlreadyImported, decision.Existing?.ItemName ?? entry.Name, decision.Existing?.StartTicks, decision.Reason);
                    continue;
                }

                var resolution = resolve(key);
                if (resolution is null || resolution.Outcome == BookmarkJeImportOutcome.Skipped || entry.Timestamp > MaxSeconds)
                {
                    row.Skipped++;
                    Sample(
                        source,
                        0,
                        entry.Key,
                        entry.Label,
                        BookmarkJeImportOutcome.Skipped,
                        entry.Name,
                        null,
                        resolution is null ? "The bookmark data changed while the import ran; run it again."
                            : entry.Timestamp > MaxSeconds ? "Malformed: the position is out of range."
                            : resolution.Reason);
                    continue;
                }

                var start = (long)Math.Round(entry.Timestamp * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
                var clamped = resolution.RunTimeTicks is > 0 && start > resolution.RunTimeTicks.Value;
                if (clamped)
                {
                    start = Math.Max(0, resolution.RunTimeTicks!.Value - TimeSpan.TicksPerSecond);
                }

                var (name, note) = BookmarkNames.FromLabel(entry.Label, BookmarkManager.MaxNoteLength);
                var createdAt = Utc(entry.CreatedAt) ?? Utc(entry.UpdatedAt) ?? now;
                var updatedAt = Utc(entry.UpdatedAt) ?? createdAt;
                var clampNote = clamped
                    ? $" It was past the end of the video ({Position(entry.Timestamp)}), so it was moved to a second before the end."
                    : string.Empty;

                if (decision.Step == Step.New && FindDuplicate(mine.Values, resolution.ItemId, start, name) is { } twin)
                {
                    row.Duplicates++;
                    changes++;
                    ledger[key] = new BookmarkImportLedgerEntry
                    {
                        BookmarkId = Guid.Empty,
                        SourceUpdatedAt = SourceTime(entry),
                        ImportedAt = now,
                        Outcome = DuplicateOutcome
                    };
                    Sample(
                        source,
                        1,
                        entry.Key,
                        entry.Label,
                        BookmarkJeImportOutcome.Duplicate,
                        resolution.ItemName,
                        start,
                        twin.Origin == BookmarkOrigin.JellyfinEnhanced && !records.ContainsKey(twin.Id)
                            ? "The same name and moment (within a second) as another Jellyfin Enhanced bookmark of this user on this video, imported in this run."
                            : "The same name and moment (within a second) as a bookmark this user already has on this video.");
                    continue;
                }

                var existing = decision.Existing;
                var record = new Bookmark
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    UserId = owner!.Id,
                    ItemId = resolution.ItemId,
                    StartTicks = start,
                    Name = name,
                    Note = note,
                    ItemName = resolution.ItemName,
                    Identity = resolution.Identity,
                    Origin = BookmarkOrigin.JellyfinEnhanced,
                    AttachedUtc = resolution.Attached ? now : null,
                    DetachedUtc = resolution.Attached ? null : now,
                    CreatedAt = existing?.CreatedAt ?? createdAt,
                    UpdatedAt = updatedAt,
                    Extra = existing?.Extra
                };
                mine[record.Id] = record;
                changes++;
                ledger[key] = new BookmarkImportLedgerEntry
                {
                    BookmarkId = record.Id,
                    SourceUpdatedAt = SourceTime(entry),
                    ImportedAt = now,
                    Outcome = ImportedOutcome,
                    RecordUpdatedAt = record.UpdatedAt,
                    Extra = decision.Ledger?.Extra
                };
                if (clamped)
                {
                    row.Clamped++;
                }

                if (existing is not null)
                {
                    replaced.Add(record);
                    row.Updated++;
                    Sample(source, 4, entry.Key, entry.Label, BookmarkJeImportOutcome.Updated, record.ItemName, start, decision.Reason + clampNote);
                    continue;
                }

                added.Add(record);
                row.Import++;
                switch (resolution.Outcome)
                {
                    case BookmarkJeImportOutcome.MatchedById:
                        row.MatchedById++;
                        break;
                    case BookmarkJeImportOutcome.MatchedByProviderId:
                        row.MatchedByProviderId++;
                        break;
                    case BookmarkJeImportOutcome.Ambiguous:
                        row.Ambiguous++;
                        break;
                    default:
                        row.Detached++;
                        break;
                }

                var rank = resolution.Outcome switch
                {
                    BookmarkJeImportOutcome.Detached => 2,
                    BookmarkJeImportOutcome.Ambiguous => 3,
                    _ when clamped => 5,
                    BookmarkJeImportOutcome.MatchedByProviderId => 6,
                    _ => 8
                };
                Sample(source, rank, entry.Key, entry.Label, resolution.Outcome, record.ItemName, start, resolution.Reason + clampNote);
            }

            row.Errors = errors;
            if (owner is not null)
            {
                owners[owner.Id] = owner.Username;
            }

            // The applied-import entry: when the source is first covered, and whenever a run changes something for it, so a
            // second run with nothing new writes nothing at all.
            if (changes > 0 || !document.ImportRuns.ContainsKey(source.RunKey))
            {
                runs[source.RunKey] = new BookmarkImportRun
                {
                    AppliedUtc = now,
                    AppliedBy = appliedBy,
                    Found = row.Found,
                    Imported = row.Import,
                    Updated = row.Updated,
                    Detached = row.Detached,
                    Duplicates = row.Duplicates,
                    AlreadyImported = row.AlreadyImported,
                    Skipped = row.Skipped,
                    Extra = document.ImportRuns.GetValueOrDefault(source.RunKey)?.Extra
                };
            }
        }

        var ordered = rows.OrderBy(r => r.Rank).ThenBy(r => r.Order).ToList();
        return new JeImportPlan(
            users,
            ordered.Take(MaxSamples).Select(r => r.Sample).ToList(),
            Math.Max(0, ordered.Count - MaxSamples),
            added,
            replaced,
            ledger,
            runs,
            owners);
    }

    /// <summary>
    /// Gets the time a JE entry was last changed: its <c>UpdatedAt</c>, else its <c>CreatedAt</c> (UTC).
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The time, or <c>null</c> when it has neither.</returns>
    public static DateTime? SourceTime(JeBookmark entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Utc(entry.UpdatedAt) ?? Utc(entry.CreatedAt);
    }

    /// <summary>
    /// Reads one of JE's ISO 8601 times (<c>2026-05-22T21:23:40.598Z</c>) as UTC; one without a zone is taken as UTC.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The time, or <c>null</c> when it isn't one.</returns>
    public static DateTime? Utc(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;

    // Each JE user's file once, the first read: a user read twice would be imported twice, since
    // neither the ledger nor the duplicate check sees what this run plans for the same user before it.
    private static IEnumerable<JeImportSource> Once(IReadOnlyList<JeImportSource> sources)
    {
        var seen = new HashSet<Guid>();
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (seen.Add(source.JeUserId))
            {
                yield return source;
            }
        }
    }

    private static Dictionary<Guid, Bookmark> Index(BookmarkStoreDocument document)
    {
        // The last record of an id wins, as the store reads a hand-edited file.
        var byId = new Dictionary<Guid, Bookmark>();
        foreach (var bookmark in document.Bookmarks)
        {
            if (bookmark is not null)
            {
                byId[bookmark.Id] = bookmark;
            }
        }

        return byId;
    }

    private static Decision Decide(BookmarkStoreDocument document, Dictionary<Guid, Bookmark> records, JeImportSource source, JeBookmark entry)
    {
        if (source.Owner is null)
        {
            return new Decision(Step.Skip, "No user on this server has this id.");
        }

        if (!string.IsNullOrWhiteSpace(entry.SyncedFrom))
        {
            return new Decision(Step.Skip, "A copy Jellyfin Enhanced made of a bookmark on another version of the video (SyncedFrom); the original is imported on its own.");
        }

        if (!document.ImportLedger.TryGetValue(source.LedgerKey(entry.Key), out var ledger) || ledger is null)
        {
            return new Decision(Step.New, string.Empty);
        }

        var sourceTime = SourceTime(entry);
        var newer = sourceTime is DateTime changed && (ledger.SourceUpdatedAt is not DateTime stored || changed > stored);
        var when = Date(ledger.ImportedAt);
        if (!newer)
        {
            return new Decision(Step.Already, ledger.Outcome == DuplicateOutcome ? $"Left out as a duplicate on {when}." : $"Imported on {when}.") { Ledger = ledger };
        }

        // Left out as a duplicate before and changed in JE since: it is looked at again.
        if (ledger.BookmarkId == Guid.Empty)
        {
            return new Decision(Step.New, string.Empty) { Ledger = ledger };
        }

        if (!records.TryGetValue(ledger.BookmarkId, out var existing) || !BookmarkRecordRules.IsLiveBookmarkOf(existing, source.Owner.Id))
        {
            return new Decision(Step.Already, $"Changed in Jellyfin Enhanced since the import on {when}, but deleted here since: not brought back.") { Ledger = ledger, LocalChange = true };
        }

        if (ledger.RecordUpdatedAt is not DateTime written || existing.UpdatedAt.Ticks != written.Ticks)
        {
            return new Decision(Step.Already, $"Changed in Jellyfin Enhanced since the import on {when}, and here too: the change made here wins.") { Ledger = ledger, Existing = existing, LocalChange = true };
        }

        return new Decision(Step.Update, $"Changed in Jellyfin Enhanced since the import on {when}, and not here: rewritten from it.") { Ledger = ledger, Existing = existing };
    }

    private static Bookmark? FindDuplicate(IEnumerable<Bookmark> bookmarks, Guid itemId, long startTicks, string? name)
        => bookmarks.FirstOrDefault(b => b.ItemId.Equals(itemId)
            && Math.Abs(b.StartTicks - startTicks) <= TimeSpan.TicksPerSecond
            && string.Equals(b.Name, name, StringComparison.Ordinal));

    private static string Date(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Position(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Min(seconds, MaxSeconds)));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    private static string Cut(string text, int length)
        => text.Length <= length ? text : text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)] + "…";

    private sealed record Decision(Step Step, string Reason)
    {
        public BookmarkImportLedgerEntry? Ledger { get; init; }

        public Bookmark? Existing { get; init; }

        public bool LocalChange { get; init; }
    }
}
