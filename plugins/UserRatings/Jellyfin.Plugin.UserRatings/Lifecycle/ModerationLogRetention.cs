using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// The moderation log's retention (<c>ModerationLogDays</c>, 365 by default): the daily
/// maintenance's <see cref="StepName"/> step deletes the log's rows written more than that many days ago, then asks for a
/// checkpoint, so the rows' pages leave the database's write-ahead log too (the kit's background
/// <c>RequestCheckpoint</c>; the maintenance's own <c>Checkpoint</c> step at the end of the run waits for it). 0 keeps
/// every row.
/// </summary>
/// <remarks>
/// <para>The log holds ids, reason codes and counts, never a review's text, and every writable connection has
/// <c>secure_delete</c>, so a deleted row's bytes are overwritten.</para>
/// <para>Every row is aged alike, whatever its action, but for the rows a restore reads: they stay while a kept snapshot is
/// older than them. A moderator's <c>RemoveText</c> and <c>RemoveEntry</c> (since 0.3.21): a restore of that snapshot
/// removes the review again from its row; once it was gone, the restore brought the moderated text back if the member had
/// written again. And since 0.3.22 every
/// <c>ImportEnvelope</c> (a Replace of another server's export tells the restore the log before it is the file's, and an
/// import of this server's own export that its ledger rows are this server's) and <c>StartEmpty</c> (the restore says
/// deletions made before it may be back). When the snapshots can't be listed, every such row stays that run.
/// <c>KeepAsOrphan</c> applied again to a user whose
/// <c>UserOrphaned</c> row has gone writes a new one (the dedupe looks at the log), which changes
/// no rating.</para>
/// <para>Nothing is deleted while the database is read-only; the setting is read at each run, so a change on the
/// Settings tab applies at the next one without a restart.</para>
/// </remarks>
internal sealed class ModerationLogRetention
{
    /// <summary>The maintenance step's name, for the log and Diagnostics' <c>Maintenance</c>.</summary>
    public const string StepName = "ModerationLogRetention";

    private readonly IRatingsStore _store;
    private readonly Func<int> _days;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModerationLogRetention"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="days">The setting, read at each run (<see cref="RatingsSettings.ModerationLogDays"/>, already held to
    /// 0 to <see cref="RatingsSettings.MaxModerationLogDays"/>).</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ModerationLogRetention(IRatingsStore store, Func<int> days, TimeProvider clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _days = days;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Gets the time before which the log's rows go with a setting, or null when it keeps every row.
    /// </summary>
    /// <param name="days">The setting.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns>The cutoff.</returns>
    public static DateTime? CutoffFor(int days, DateTime nowUtc)
        => days <= 0 ? null : nowUtc.AddDays(-Math.Min(days, RatingsSettings.MaxModerationLogDays));

    /// <summary>
    /// Deletes the rows older than the setting (the daily step).
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The step's report: <c>Days</c>, <c>CutoffUtc</c> (none with 0) and <c>Deleted</c>; <c>ReadOnly</c> when the
    /// database couldn't be written.</returns>
    public async Task<IDictionary<string, object?>?> RunAsync(CancellationToken cancellationToken)
    {
        var days = Math.Clamp(_days(), 0, RatingsSettings.MaxModerationLogDays);
        var report = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Days"] = days, ["Deleted"] = 0 };
        if (CutoffFor(days, _clock.GetUtcNow().UtcDateTime) is not { } cutoff)
        {
            return report;
        }

        report["CutoffUtc"] = cutoff;
        if (_store.IsReadOnly)
        {
            report["ReadOnly"] = true;
            return report;
        }

        var removals = RemovalsCutoff(cutoff);
        if (removals < cutoff)
        {
            report["RemovalsCutoffUtc"] = removals;
        }

        var deleted = await _store.WriteAsync(w => w.DeleteModerationLogBefore(cutoff, removals), cancellationToken).ConfigureAwait(false);
        report["Deleted"] = deleted;
        if (deleted > 0)
        {
            _logger.LogInformation("The moderation log's {Count} rows older than {Days} days were deleted (ModerationLogDays)", deleted, days);
            RatingsCheckpoint.RequestAfter(_store, _logger, "the moderation log's retention (" + deleted.ToString(CultureInfo.InvariantCulture) + " rows older than " + days.ToString(CultureInfo.InvariantCulture) + " days)");
        }

        return report;
    }

    // The oldest kept snapshot's time, when it is before the cutoff: the rows a restore reads after it stay. Snapshots that
    // can't be listed keep every such row.
    private DateTime RemovalsCutoff(DateTime cutoff)
    {
        try
        {
            var oldest = _store.ListSnapshots().Select(s => (DateTime?)s.Utc).Min();
            return oldest is { } o && o < cutoff ? o : cutoff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginException)
        {
            _logger.LogWarning(ex, "The moderation log's retention keeps every row a restore reads this run (removals, envelope imports, Start empty): the snapshots couldn't be listed");
            return DateTime.MinValue;
        }
    }
}
