using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// Names, lists and rotates store snapshots: the daily class
/// <c>snapshots/&lt;store&gt;-&lt;yyyyMMdd-HHmmss&gt;&lt;ext&gt;</c> and the pre-operation class
/// <c>snapshots/pre-op/&lt;store&gt;-&lt;operation&gt;-&lt;yyyyMMdd-HHmmss&gt;&lt;ext&gt;</c>, both UTC. A second
/// snapshot within the same second gets a <c>-1</c>, <c>-2</c>… suffix. Only files named after the given store are ever
/// listed or deleted, so several stores can share one folder.
/// </summary>
internal static class SnapshotRotation
{
    /// <summary>
    /// How far past now a snapshot's name may be dated before it counts as dated in the future: taken while the clock
    /// was wrong, or the clock is wrong now, so its name says nothing about how old its data is. Rotation given
    /// <c>nowUtc</c> neither deletes nor counts such a snapshot, and both stores' restores try it last (the JSON
    /// store's at load; the SQLite database's at open and an admin's).
    /// </summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Returns an unused path for a new daily snapshot of a store.
    /// </summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <param name="storeName">The store's name, the file name without extension (for example <c>bookmarks</c>).</param>
    /// <param name="extension">The snapshot's extension, with the dot (for example <c>.json</c>).</param>
    /// <param name="utc">The snapshot time.</param>
    /// <returns>The path.</returns>
    public static string NewPath(string directory, string storeName, string extension, DateTime utc)
        => Unused(Path.Combine(directory, storeName + "-" + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture)), extension);

    /// <summary>
    /// Returns an unused path for a new pre-operation snapshot of a store, in <c>&lt;directory&gt;/pre-op</c>.
    /// </summary>
    /// <param name="directory">The snapshot folder (not its <c>pre-op</c> sub-folder).</param>
    /// <param name="storeName">The store's name.</param>
    /// <param name="operation">One of <see cref="SnapshotOperations.All"/>.</param>
    /// <param name="extension">The snapshot's extension, with the dot.</param>
    /// <param name="utc">The snapshot time.</param>
    /// <returns>The path.</returns>
    public static string NewPreOpPath(string directory, string storeName, string operation, string extension, DateTime utc)
        => Unused(
            Path.Combine(PreOpDirectory(directory), storeName + "-" + SnapshotOperations.Require(operation) + "-" + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture)),
            extension);

    /// <summary>
    /// Gets the folder of the pre-operation snapshots.
    /// </summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <returns><c>&lt;directory&gt;/pre-op</c>.</returns>
    public static string PreOpDirectory(string directory) => Path.Combine(directory, SnapshotOperations.Folder);

    /// <summary>
    /// Lists a store's daily snapshots, newest first.
    /// </summary>
    /// <param name="directory">The snapshot folder; a missing folder has none.</param>
    /// <param name="storeName">The store's name.</param>
    /// <param name="extension">The snapshot's extension, with the dot.</param>
    /// <returns>The snapshots.</returns>
    public static IReadOnlyList<StoreSnapshot> List(string directory, string storeName, string extension)
        => ListMatching(
            directory,
            new Regex(
                "^" + Regex.Escape(storeName) + @"-(?<ts>\d{8}-\d{6})(?:-(?<n>\d{1,6}))?" + Regex.Escape(extension) + "$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));

    /// <summary>
    /// Lists a store's pre-operation snapshots, newest first.
    /// </summary>
    /// <param name="directory">The snapshot folder (not its <c>pre-op</c> sub-folder).</param>
    /// <param name="storeName">The store's name.</param>
    /// <param name="extension">The snapshot's extension, with the dot.</param>
    /// <returns>The snapshots.</returns>
    public static IReadOnlyList<StoreSnapshot> ListPreOp(string directory, string storeName, string extension)
        => ListMatching(
            PreOpDirectory(directory),
            new Regex(
                "^" + Regex.Escape(storeName) + "-(?<op>" + string.Join('|', SnapshotOperations.All.Select(Regex.Escape)) + @")-(?<ts>\d{8}-\d{6})(?:-(?<n>\d{1,6}))?" + Regex.Escape(extension) + "$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));

    /// <summary>
    /// Deletes a store's oldest daily snapshots so that at most <paramref name="keep"/> remain. A file that can't be
    /// deleted is logged and left.
    /// </summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <param name="storeName">The store's name.</param>
    /// <param name="extension">The snapshot's extension, with the dot.</param>
    /// <param name="keep">How many to keep; at least 1.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="keepBeforeUtc">
    /// When set, every snapshot taken before this time is kept whatever <paramref name="keep"/> says, and only the later
    /// ones are rotated: the store started empty then, so the earlier ones are the last good copies of its data.
    /// </param>
    /// <param name="nowUtc"> When set, a snapshot dated more than <see cref="FutureTolerance"/> after it is neither
    /// deleted nor counted: rotation goes by the names' times, which say nothing about such a snapshot's age, so it may
    /// hold the newest data (the JSON store's case: the clock moved back). </param>
    /// <param name="companionSuffixes">
    /// Suffixes of files that belong to a snapshot and go with it: a SQLite snapshot's <c>-wal</c>, <c>-shm</c> and
    /// <c>-journal</c>. None by default. It comes after
    /// <paramref name="keepBeforeUtc"/> and <paramref name="nowUtc"/>, so the JSON store's positional calls keep their
    /// meaning. </param>
    /// <param name="keepNames"> When set, the snapshots of these file names (any case) are neither deleted nor counted:
    /// a store's kept set, the snapshots there were when its data was found missing or unreadable (the JSON store's and
    /// the SQLite database's <c>.keep-snapshots</c> marker), so that exactly those are kept and the ones taken since
    /// rotate as usual. It comes last, so earlier positional calls keep their meaning. </param>
    /// <returns>The deleted snapshots' paths.</returns>
    public static IReadOnlyList<string> Rotate(string directory, string storeName, string extension, int keep, ILogger logger, DateTime? keepBeforeUtc = null, DateTime? nowUtc = null, IReadOnlyList<string>? companionSuffixes = null, IReadOnlyCollection<string>? keepNames = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        ArgumentNullException.ThrowIfNull(logger);

        var names = NameSet(keepNames);
        var candidates = List(directory, storeName, extension)
            .Where(s => (keepBeforeUtc is null || !IsBefore(s, keepBeforeUtc.Value)) && (nowUtc is null || !IsFutureDated(s, nowUtc.Value)) && !names.Contains(s.Name));
        return Delete(candidates.Skip(keep), logger, companionSuffixes);
    }

    /// <summary>
    /// Deletes a store's oldest pre-operation snapshots so that at most <paramref name="keep"/> remain, apart from
    /// <paramref name="spare"/> and those taken before <paramref name="keepBeforeUtc"/>.
    /// </summary>
    /// <param name="directory">The snapshot folder (not its <c>pre-op</c> sub-folder).</param>
    /// <param name="storeName">The store's name.</param>
    /// <param name="extension">The snapshot's extension, with the dot.</param>
    /// <param name="keep">How many to keep; at least 1.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="spare"> A snapshot's file name that is never deleted here, even when it is among the oldest: the
    /// one being restored, which must survive a failed restore. One more than <paramref name="keep"/> may then remain
    /// until the next rotation. </param>
    /// <param name="keepBeforeUtc"> As for <see cref="Rotate"/>: every pre-operation snapshot taken before this time is
    /// kept, and only the later ones are rotated (the JSON store's restore at load: a snapshot passed over then may be
    /// the only copy of some data). Neither store passes it any more: both keep their incident's snapshots by name
    /// (<paramref name="keepNames"/>; the JSON store and the SQLite database), and it stays for a caller that keeps by
    /// time. </param>
    /// <param name="nowUtc">As for <see cref="Rotate"/>: a snapshot dated in the future is neither deleted nor counted.</param>
    /// <param name="companionSuffixes">Suffixes of files that go with a deleted snapshot, as for <see cref="Rotate"/>.</param>
    /// <param name="keepNames">As for <see cref="Rotate"/>: the snapshots of these file names are neither deleted nor counted.</param>
    /// <param name="keepWithin"> When set (opt-in, the SQLite database's <c>PreOpKeepWithin</c>), the snapshots are
    /// kept by age instead of by count: every one taken within this time before <paramref name="nowUtc"/> (or now), up
    /// to
    /// <paramref name="keepAtMost"/>, newest first, and none older, <paramref name="keep"/> notwithstanding (a
    /// pre-operation copy holds data its operation removed, a deleted user's say, so it expires whatever happens; the
    /// daily snapshots are the safety net). A burst of operations (a script deleting ten users) then never rotates out
    /// the snapshot that holds the first one's data, and none outlives the time. <paramref name="spare"/>, the
    /// snapshots of <paramref name="keepNames"/> and those dated in the future are still never deleted. </param>
    /// <param name="keepAtMost">The most snapshots <paramref name="keepWithin"/> keeps (<see cref="SnapshotOperations.MaxKeptByAge"/>);
    /// at least 1.</param>
    /// <returns>The deleted snapshots' paths.</returns>
    public static IReadOnlyList<string> RotatePreOp(string directory, string storeName, string extension, int keep, ILogger logger, string? spare = null, DateTime? keepBeforeUtc = null, DateTime? nowUtc = null, IReadOnlyList<string>? companionSuffixes = null, IReadOnlyCollection<string>? keepNames = null, TimeSpan? keepWithin = null, int keepAtMost = SnapshotOperations.MaxKeptByAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepAtMost, 1);
        var names = NameSet(keepNames);
        DateTime? since = keepWithin is { } within ? (nowUtc ?? DateTime.UtcNow) - within : null;
        return Delete(
            ListPreOp(directory, storeName, extension)
                .Where(s => (keepBeforeUtc is null || !IsBefore(s, keepBeforeUtc.Value)) && (nowUtc is null || !IsFutureDated(s, nowUtc.Value)) && !names.Contains(s.Name))
                .Where((s, index) => since is { } cutoff ? index >= keepAtMost || s.Utc < cutoff : index >= keep)
                .Where(s => !string.Equals(s.Name, spare, StringComparison.OrdinalIgnoreCase)),
            logger,
            companionSuffixes);
    }

    /// <summary>
    /// Tells whether a snapshot's name is dated more than <see cref="FutureTolerance"/> after a time.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns><c>true</c> when it is dated in the future.</returns>
    public static bool IsFutureDated(StoreSnapshot snapshot, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Utc > nowUtc + FutureTolerance;
    }

    /// <summary>
    /// Tells whether a snapshot was taken before a time. Snapshot names hold whole seconds, so the time is compared to the
    /// second: a snapshot of the same second as the time counts as later.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="utc">The time.</param>
    /// <returns><c>true</c> when it is earlier.</returns>
    public static bool IsBefore(StoreSnapshot snapshot, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var second = new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        return snapshot.Utc < second;
    }

    private static HashSet<string> NameSet(IReadOnlyCollection<string>? names)
        => names is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static string Unused(string stem, string extension)
    {
        var path = stem + extension;
        for (var n = 1; File.Exists(path); n++)
        {
            path = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{extension}");
        }

        return path;
    }

    private static List<StoreSnapshot> ListMatching(string directory, Regex pattern)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var snapshots = new List<StoreSnapshot>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            var match = pattern.Match(file.Name);
            if (match.Success
                && DateTime.TryParseExact(
                    match.Groups["ts"].Value,
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utc))
            {
                var sequence = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 0;
                var operation = match.Groups["op"].Success
                    ? SnapshotOperations.All.First(o => string.Equals(o, match.Groups["op"].Value, StringComparison.OrdinalIgnoreCase))
                    : null;
                snapshots.Add(new StoreSnapshot(file.Name, file.FullName, utc, sequence, file.Length, operation));
            }
        }

        return snapshots
            .OrderByDescending(s => s.Utc)
            .ThenByDescending(s => s.Sequence)
            .ToList();
    }

    private static List<string> Delete(IEnumerable<StoreSnapshot> snapshots, ILogger logger, IReadOnlyList<string>? companionSuffixes)
    {
        var deleted = new List<string>();
        foreach (var snapshot in snapshots)
        {
            try
            {
                File.Delete(snapshot.Path);
                deleted.Add(snapshot.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete the old snapshot {Path}", snapshot.Path);
                continue;
            }

            foreach (var suffix in companionSuffixes ?? [])
            {
                var companion = snapshot.Path + suffix;
                try
                {
                    File.Delete(companion);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete {Path}, which belonged to the old snapshot {Snapshot}", companion, snapshot.Name);
                }
            }
        }

        return deleted;
    }
}
