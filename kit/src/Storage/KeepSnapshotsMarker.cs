using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// Records, writes and reads a store's <c>&lt;file&gt;.keep-snapshots</c> marker (<see cref="KeptSnapshots{TStatus}"/>),
/// the same for the JSON store and the SQLite database: <c>{KeepSnapshotsBeforeUtc, SinceUtc, Reason, Incident,
/// Snapshots}</c>, and <c>ReleaseBeforeUtc</c> for names recorded from an earlier version's marker.
/// </summary>
internal static class KeepSnapshotsMarker
{
    /// <summary>The marker's suffix after the store file's name.</summary>
    public const string Suffix = ".keep-snapshots";

    private const string BeforeProperty = "KeepSnapshotsBeforeUtc";

    /// <summary>
    /// How far ahead of now a marker's <c>KeepSnapshotsBeforeUtc</c> may be (since Bookmarks 1.2.1): a week, well past the
    /// snapshots of a clock that was a day or two ahead, which an earlier version must keep by that time.
    /// </summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(7);

    /// <summary>
    /// Records every snapshot there is now, daily and pre-operation, by name (an incident: the data started empty, or was
    /// restored from a snapshot or a stale backup). The time is the later of now, the newest snapshot's time plus a second
    /// and the existing marker's, so it is never moved back and is later than every kept snapshot even after the clock moved
    /// back (what an earlier version reads as "every snapshot dated before it"); but never later than
    /// <see cref="CapOf"/>, a week ahead (since Bookmarks 1.2.1): a snapshot dated further ahead (taken while the clock was
    /// wrong) would otherwise make an earlier version, after a downgrade, keep every snapshot it takes for years. Such a snapshot is still
    /// kept by name here, and an earlier version keeps it as the newest by date.
    /// </summary>
    /// <typeparam name="TStatus">The store's status.</typeparam>
    /// <param name="snapshots">The snapshots there are now, daily and pre-operation.</param>
    /// <param name="now">Now.</param>
    /// <param name="incident">How the data was found.</param>
    /// <param name="existing">The kept snapshots so far, or <c>null</c>.</param>
    /// <returns>The kept snapshots; <c>null</c> when there is no snapshot at all, so nothing to keep.</returns>
    public static KeptSnapshots<TStatus>? Record<TStatus>(IReadOnlyCollection<StoreSnapshot> snapshots, DateTime now, TStatus incident, KeptSnapshots<TStatus>? existing)
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return null;
        }

        var keepBefore = now;
        foreach (var snapshot in snapshots)
        {
            var after = snapshot.Utc.AddSeconds(1);
            if (after > keepBefore)
            {
                keepBefore = after;
            }
        }

        if (existing is not null && existing.BeforeUtc > keepBefore)
        {
            keepBefore = existing.BeforeUtc;
        }

        var cap = CapOf(now);
        if (keepBefore > cap)
        {
            keepBefore = cap;
        }

        return new KeptSnapshots<TStatus>(keepBefore, now, incident, snapshots.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), null);
    }

    /// <summary>
    /// Writes the marker; <c>false</c>, with a Warning, when it can't be: the protection then holds in this process only.
    /// </summary>
    /// <typeparam name="TStatus">The store's status.</typeparam>
    /// <param name="path">The marker's path.</param>
    /// <param name="kept">The kept snapshots.</param>
    /// <param name="reason">The banner of the incident, or <c>null</c>.</param>
    /// <param name="options">The serializer options of the store's files.</param>
    /// <param name="logger">The store's logger.</param>
    /// <param name="file">The store's file, for the log.</param>
    /// <returns>Whether it was written.</returns>
    public static bool Write<TStatus>(string path, KeptSnapshots<TStatus> kept, string? reason, JsonSerializerOptions options, ILogger logger, string file)
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var marker = new JsonObject
            {
                [BeforeProperty] = kept.BeforeUtc,
                ["SinceUtc"] = kept.SinceUtc,
                ["Reason"] = reason
            };
            if (kept.Incident is { } incident)
            {
                marker["Incident"] = incident.ToString();
            }

            marker["Snapshots"] = new JsonArray(kept.Names.Order(StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
            if (kept.ReleaseBeforeUtc is { } releaseBefore)
            {
                marker["ReleaseBeforeUtc"] = releaseBefore;
            }

            File.WriteAllText(path, marker.ToJsonString(options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Marker}; the kept snapshots of {File} are protected only until the plugin restarts", path, file);
            return false;
        }
    }

    /// <summary>
    /// Reads the marker, or <c>null</c> when there is none. Formats: the names (<c>Snapshots</c>); and, written by
    /// earlier versions, a time that kept every snapshot dated before it: Bookmarks 1.1's <c>{KeepSnapshotsBeforeUtc,
    /// Reason}</c>, 1.2's first <c>{KeepSnapshotsBeforeUtc, SinceUtc, Reason}</c>, and (with
    /// <paramref name="readsPlainTime"/>) a development build's SQLite database's, the time alone as text. Those are
    /// recorded by name once (the marker rewritten, its fields kept): the snapshots there are now dated before the
    /// time, and only one of them dated before the incident (<c>SinceUtc</c>, else the time) releases them, since one
    /// dated after it may have been taken since, while the clock was behind. A marker that can't be read is left as it
    /// is and still protects: its own write time stands in, the same way, in this process.
    /// </summary>
    /// <typeparam name="TStatus">The store's status.</typeparam>
    /// <param name="path">The marker's path.</param>
    /// <param name="file">The store's file, for the log.</param>
    /// <param name="namesDatedBefore">The names of the store's snapshots, daily and pre-operation, dated before a time.</param>
    /// <param name="options">The serializer options of the store's files.</param>
    /// <param name="logger">The store's logger.</param>
    /// <param name="timeProvider">The store's clock, when the marker's own time can't be read either.</param>
    /// <param name="readsPlainTime">Whether a marker of the time alone, as text, is the store's own earlier format.</param>
    /// <returns>The kept snapshots, or <c>null</c>.</returns>
    public static KeptSnapshots<TStatus>? Read<TStatus>(
        string path,
        string file,
        Func<DateTime, HashSet<string>> namesDatedBefore,
        JsonSerializerOptions options,
        ILogger logger,
        TimeProvider timeProvider,
        bool readsPlainTime = false)
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(namesDatedBefore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (readsPlainTime && TryReadPlainTime(bytes, out var plain))
            {
                return Upgrade(path, file, new KeptSnapshots<TStatus>(plain, plain, null, namesDatedBefore(plain), plain), null, options, logger);
            }

            if (JsonNode.Parse(bytes) is JsonObject root && TryReadUtc(root[BeforeProperty], out var before))
            {
                var since = TryReadUtc(root["SinceUtc"], out var sinceUtc) ? sinceUtc : before;
                TStatus? incident = root["Incident"] is JsonValue incidentValue && incidentValue.TryGetValue<string>(out var incidentText)
                    && Enum.TryParse<TStatus>(incidentText, ignoreCase: false, out var parsed) ? parsed : null;
                var reason = root["Reason"] is JsonValue reasonValue && reasonValue.TryGetValue<string>(out var reasonText) ? reasonText : null;
                var cap = CapOf(timeProvider.GetUtcNow().UtcDateTime);
                if (root["Snapshots"] is JsonArray names)
                {
                    var recorded = names.OfType<JsonValue>()
                        .Select(n => n.TryGetValue<string>(out var name) ? name : null)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var kept = new KeptSnapshots<TStatus>(before, since, incident, recorded, TryReadUtc(root["ReleaseBeforeUtc"], out var releaseBefore) ? releaseBefore : null);
                    return before > cap ? Capped(path, file, kept, cap, reason, options, logger) : kept;
                }

                // An earlier version's marker keeps the snapshots dated before its time, all of them, the far-future case too
                // (those are the ones it meant); the time it is rewritten with is capped as a new one's is.
                var converted = Upgrade(path, file, new KeptSnapshots<TStatus>(before, since, incident, namesDatedBefore(before), since), reason, options, logger);
                return converted.BeforeUtc > cap ? Capped(path, file, converted, cap, reason, options, logger) : converted;
            }

            logger.LogWarning("Could not read {Marker}: it has no {Property}; keeping the snapshots taken before it was last written", path, BeforeProperty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not read {Marker}; keeping the snapshots taken before it was last written", path);
        }

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read the time of {Marker}", path);
            stamp = timeProvider.GetUtcNow().UtcDateTime;
        }

        return new KeptSnapshots<TStatus>(stamp, stamp, null, namesDatedBefore(stamp), stamp);
    }

    /// <summary>
    /// The latest <c>KeepSnapshotsBeforeUtc</c> a marker holds (since Bookmarks 1.2.1): <see cref="MaxAhead"/> after now.
    /// Only an earlier version reads the time (this one keeps by name), and it keeps every snapshot dated before it until
    /// the marker goes: a time far ahead, from a snapshot taken while the clock was wrong or a marker written then, would
    /// keep every snapshot it takes for years. A snapshot dated later than this is never rotated out by name here, and an
    /// earlier version keeps it as the newest by date.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <returns>The latest time.</returns>
    public static DateTime CapOf(DateTime now) => now + MaxAhead;

    // A marker whose time is later than CapOf(now) (one 1.2.0 wrote while a snapshot was dated far ahead): rewritten with the
    // capped time, its names and other fields kept; in memory only when it can't be written.
    private static KeptSnapshots<TStatus> Capped<TStatus>(string path, string file, KeptSnapshots<TStatus> kept, DateTime cap, string? reason, JsonSerializerOptions options, ILogger logger)
        where TStatus : struct, Enum
    {
        var capped = kept with { BeforeUtc = cap };
        if (Write(path, capped, reason, options, logger, file))
        {
            logger.LogInformation(
                "{Marker} of {File} named {Utc} for an earlier version, more than a week ahead; it names {Cap} now, so an earlier version doesn't keep every snapshot it takes. The {Count} snapshots it keeps are kept as before",
                path,
                file,
                kept.BeforeUtc,
                cap,
                kept.Names.Count);
        }

        return capped;
    }

    // An earlier version's marker read by its dates, recorded by name once (the file rewritten, its fields kept), so that
    // from now on only those are kept.
    private static KeptSnapshots<TStatus> Upgrade<TStatus>(string path, string file, KeptSnapshots<TStatus> converted, string? reason, JsonSerializerOptions options, ILogger logger)
        where TStatus : struct, Enum
    {
        if (Write(path, converted, reason, options, logger, file))
        {
            logger.LogInformation(
                "Recorded by name in {Marker}, written by an earlier version, the {Count} snapshots of {File} dated before {Utc} it keeps; the snapshots taken since rotate as usual",
                path,
                converted.Names.Count,
                file,
                converted.BeforeUtc);
        }

        return converted;
    }

    // A development build's SQLite database marker before the names: the round-trip time alone ("O"), as text.
    private static bool TryReadPlainTime(byte[] bytes, out DateTime utc)
    {
        utc = default;
        var text = JsonStoreFormat.SkipByteOrderMark(bytes);
        if (text.Length == 0 || text.Length > 64 || text[0] == (byte)'{')
        {
            return false;
        }

        return DateTime.TryParse(
            System.Text.Encoding.UTF8.GetString(text).Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out utc);
    }

    private static bool TryReadUtc(JsonNode? node, out DateTime utc)
    {
        utc = default;
        if (node is not JsonValue value || !value.TryGetValue<DateTime>(out var parsed))
        {
            return false;
        }

        utc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        return true;
    }
}
