using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// What a corruption restore did: where the damaged database went and what took its place. Shown in Diagnostics as
/// <c>Recovery</c> until the plugin restarts. The same restore runs when the database file is missing, or empty, while
/// snapshots of its data exist: then <see cref="Damaged"/> is <c>false</c> and only what was there (an empty file,
/// sidecars without their database) is quarantined.
/// </summary>
/// <param name="Utc">When it ran.</param>
/// <param name="Reason">Why: the failed check's first line, the error, or that the file was missing.</param>
/// <param name="QuarantineFolder">The <c>quarantine-&lt;ts&gt;</c> folder's name the database, <c>-wal</c> and <c>-shm</c> were moved into; <c>null</c> when there was nothing to move.</param>
/// <param name="QuarantinedFiles">The names of the files moved there.</param>
/// <param name="RestoredFrom">The snapshot's file name, or <c>null</c> when an admin chose to start empty.</param>
/// <param name="SnapshotUtc">When that snapshot was taken.</param>
/// <param name="SnapshotsTried">The snapshots tried before it, newest first, that failed their check.</param>
/// <param name="Damaged"><c>true</c> when the database failed its check, <c>false</c> when it was missing or empty.</param>
/// <param name="PassedOver"><see cref="SnapshotsTried"/> as the banner names them: each name, followed by why when it was
/// passed over without being read ("(no permission to read it)", the JSON store's wording).</param>
/// <param name="DatedLater">The snapshots dated more than a day after the restore (the clock wrong then or when they
/// were taken), tried last and not restored, newest first; the banner names them, since they may hold newer
/// data.</param>
internal sealed record SqliteRecoveryResult(
    DateTime Utc,
    string Reason,
    string? QuarantineFolder,
    IReadOnlyList<string> QuarantinedFiles,
    string? RestoredFrom,
    DateTime? SnapshotUtc,
    IReadOnlyList<string> SnapshotsTried,
    bool Damaged = true,
    IReadOnlyList<string>? PassedOver = null,
    IReadOnlyList<string>? DatedLater = null)
{
    /// <summary>
    /// Gets how many hours of changes the restore lost at most: from the snapshot to the restore; <c>null</c> when the
    /// database started empty.
    /// </summary>
    public double? LostHours => SnapshotUtc is { } taken ? Math.Round((Utc - taken).TotalHours, 1) : null;

    /// <summary>
    /// Gets <see cref="LostHours"/> in words for the banner: "3 minutes", "1 hour", "2.5 hours"; <c>null</c> when the
    /// database started empty.
    /// </summary>
    public string? LostSpan
    {
        get
        {
            if (SnapshotUtc is not { } taken)
            {
                return null;
            }

            var span = Utc - taken;
            if (span < TimeSpan.FromHours(1))
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
                return minutes == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");
            }

            var hours = Math.Round(span.TotalHours, 1);
            return hours == 1 ? "1 hour" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours");
        }
    }

    /// <summary>
    /// Describes the restore with plain values, for Diagnostics and an admin endpoint.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Utc"] = Utc,
        ["Reason"] = Reason,
        ["Damaged"] = Damaged,
        ["QuarantineFolder"] = QuarantineFolder,
        ["QuarantinedFiles"] = QuarantinedFiles,
        ["RestoredFrom"] = RestoredFrom,
        ["SnapshotUtc"] = SnapshotUtc,
        ["LostHours"] = LostHours,
        ["LostSpan"] = LostSpan,
        ["SnapshotsTried"] = SnapshotsTried,
        ["PassedOver"] = PassedOver ?? SnapshotsTried,
        ["DatedLater"] = DatedLater ?? []
    };
}
