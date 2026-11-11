using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// The archive's earlier lines of a rating whose review is removed or that is deleted for good: a moderator's
/// <i>Remove text only</i> or <i>Remove rating and review</i>, and a member's own delete, review clear or edit of the
/// review with other words. A rating archived once and put back since (the reattach) leaves its archived line in the file as history, review and all; when such a
/// rating's review goes, its lines go too: immediately, keyed by the rating's id, and with no copy of the file (a copy
/// would keep what the removal takes away).
/// </summary>
/// <remarks>
/// <para><b>Recorded inside the unit of work, run after the commit</b>, as the deleted-user policies' rewrites are: the unit
/// adds the rating's id to <c>Meta.ArchiveIdPurgesPending</c> (<see cref="Key"/>) only when the archive holds a line of it
/// (<see cref="ArchiveContents.HistoryIds"/>, or a record), or might (lines it can't read, or an archive it can't read or
/// reach), so a rating never archived costs no rewrite; then <see cref="RunAsync"/> rewrites the file without every line of
/// the pending ids (<see cref="DetachedArchive.Purge"/> with <c>includeHistory</c> and no snapshot folder), clears them and
/// asks for a checkpoint of the database's write-ahead log (<see cref="RatingsCheckpoint"/>, in the background). One that fails stays pending: the daily maintenance's
/// <see cref="RatingsUserDataOwner.ArchivePurgesStep"/> runs it again and Diagnostics (<c>Lifecycle</c>,
/// <c>ArchiveIdPurgesPending</c>) counts it. Meanwhile a deleted rating's id is tombstoned, so its line never comes back, and
/// the kit's export and counts leave the pending ids out (<see cref="RatingsLibraryConsumer.IsPurgePending"/>).</para>
/// <para>Logs name ratings by id and count them; review text never reaches the log.</para>
/// </remarks>
internal sealed class ArchivedLinePurges
{
    /// <summary>The Meta key of the rating ids whose archive lines are still to go.</summary>
    public const string Key = "ArchiveIdPurgesPending";

    private readonly IRatingsStore _store;
    private readonly ILogger _logger;
    private readonly Func<DetachedArchive>? _archive;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchivedLinePurges"/> class.
    /// </summary>
    /// <param name="store">The ratings store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="archive">The ratings' archive (<see cref="RatingsLibraryConsumer.Archive"/>), resolved when first
    /// needed; null leaves the archive out (tests of the live table only).</param>
    public ArchivedLinePurges(IRatingsStore store, ILogger logger, Func<DetachedArchive>? archive = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
        _archive = archive;
    }

    /// <summary>
    /// Reads the rating ids pending in <c>Meta.ArchiveIdPurgesPending</c>; none for an empty or unreadable value.
    /// </summary>
    /// <param name="json">The stored value.</param>
    /// <returns>The ids.</returns>
    public static IReadOnlyList<Guid> PendingOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json)?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Records, inside a unit of work, the rating ids whose lines the archive holds or might hold.
    /// </summary>
    /// <param name="writer">The unit's writer.</param>
    /// <param name="view">The archive as <see cref="Look"/> read it before the unit.</param>
    /// <param name="ratingIds">The ratings whose review goes.</param>
    /// <returns>How many ids were recorded.</returns>
    public static int Record(IRatingsWriter writer, ArchiveView view, IEnumerable<Guid> ratingIds)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ratingIds);
        var ids = ratingIds.Where(id => id != Guid.Empty && view.Mentions(id)).ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var pending = PendingOf(writer.GetMeta(Key));
        writer.SetMeta(Key, JsonSerializer.Serialize(pending.Concat(ids).Distinct().Order().ToList()));
        return ids.Count;
    }

    /// <summary>
    /// Tells whether an archived record is one of the ids pending.
    /// </summary>
    /// <param name="ids">The pending ids.</param>
    /// <param name="record">The archived record.</param>
    /// <returns>Whether its line goes.</returns>
    public static bool Matches(IReadOnlySet<Guid> ids, ArchivedRecord record)
        => ids.Count > 0 && Guid.TryParse(record.Id, out var id) && ids.Contains(id);

    /// <summary>
    /// Reads the archive as it is now, before a unit of work that may remove a review (the archive caches its contents by the
    /// file's length and time, so this costs no read of the file in between).
    /// </summary>
    /// <returns>The view.</returns>
    public ArchiveView Look()
    {
        if (_archive is null)
        {
            return ArchiveView.None;
        }

        try
        {
            return new ArchiveView(_archive().Read(), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown: the ids are recorded, so their lines go once the archive can be reached.
            _logger.LogWarning(ex, "The ratings' archive can't be reached, so a removed review's archive lines are left for the daily maintenance");
            return ArchiveView.Unknown;
        }
    }

    /// <summary>
    /// Rewrites the archive without the lines of the pending ids, with no copy, then clears them and asks for a checkpoint of
    /// the database's write-ahead log (in the background; the maintenance's <c>Checkpoint</c> step, last, waits for it): right after each removal's commit, and as part of the daily maintenance's
    /// <see cref="RatingsUserDataOwner.ArchivePurgesStep"/>. Ids that fail stay pending, and the failure is logged.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The report: <c>Pending</c>, <c>Done</c>, <c>Failed</c>, <c>Purged</c> (archived records deleted).</returns>
    public async Task<IDictionary<string, object?>> RunAsync(CancellationToken cancellationToken)
    {
        var report = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_store.IsReadOnly)
        {
            report["Skipped"] = "ReadOnly";
            return report;
        }

        var pending = PendingOf(_store.GetMeta(Key));
        report["Pending"] = pending.Count;
        if (pending.Count == 0 || _archive is null)
        {
            return report;
        }

        var ids = pending.ToHashSet();
        int purged;
        try
        {
            (purged, _) = _archive().Purge(r => Matches(ids, r), snapshotDirectory: null, includeHistory: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The archive lines of {Count} ratings whose review was removed couldn't be taken out of the ratings' archive; the daily maintenance tries again", ids.Count);
            report["Done"] = 0;
            report["Failed"] = ids.Count;
            return report;
        }

        await _store.WriteAsync(
            w =>
            {
                var left = PendingOf(w.GetMeta(Key)).Where(id => !ids.Contains(id)).ToList();
                w.SetMeta(Key, JsonSerializer.Serialize(left));
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        RatingsCheckpoint.RequestAfter(_store, _logger, "the archive purge of " + ids.Count.ToString(CultureInfo.InvariantCulture) + " ratings");
        _logger.LogInformation("The archive lines of {Count} ratings whose review was removed left the ratings' archive ({Purged} archived records), with no copy", ids.Count, purged);
        report["Done"] = ids.Count;
        report["Failed"] = 0;
        report["Purged"] = purged;
        return report;
    }

    /// <summary>
    /// The archive as read before a unit of work.
    /// </summary>
    /// <param name="Contents">The contents; null for no archive (no lifecycle).</param>
    /// <param name="IsUnknown">Whether the archive couldn't be reached, so every id may be in it.</param>
    internal readonly record struct ArchiveView(ArchiveContents? Contents, bool IsUnknown)
    {
        /// <summary>Gets the view of no archive.</summary>
        public static ArchiveView None => new(null, false);

        /// <summary>Gets the view of an archive that couldn't be reached.</summary>
        public static ArchiveView Unknown => new(null, true);

        /// <summary>
        /// Tells whether the archive holds a line of the rating, or might: one it can't read could be the rating's.
        /// </summary>
        /// <param name="ratingId">The rating.</param>
        /// <returns>Whether its lines must be purged.</returns>
        public bool Mentions(Guid ratingId)
        {
            if (IsUnknown)
            {
                return true;
            }

            if (Contents is not { Exists: true } contents)
            {
                return false;
            }

            if (contents.Problem is not null || contents.InvalidLines > 0 || contents.TruncatedTail)
            {
                return true;
            }

            return contents.HistoryIds.Any(h => Guid.TryParse(h, out var id) && id == ratingId)
                || contents.Records.Any(r => Guid.TryParse(r.Id, out var id) && id == ratingId);
        }
    }
}
