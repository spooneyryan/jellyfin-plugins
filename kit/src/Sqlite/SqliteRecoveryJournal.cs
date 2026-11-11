using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// The lasting record of a recovery under way: <c>&lt;db&gt;.recovery</c> next to the database, written and flushed to
/// the disk before a recovery moves anything, and again, with the identity of what it is about to put in the database's
/// place, before the rename; deleted once the recovery is done, or once what it moved is back. Nothing else is in it:
/// the quarantine folder's name, the files moved there, the step, why, and what took the database's place (its size and
/// SHA-256), so that the next open, in a process that knows nothing of the recovery, can put everything back
/// (<see cref="SqliteDatabase"/>'s open). Before it, a failed put back was remembered only in memory, and a restart
/// opened a new empty database, or restored a snapshot over the data, beside the quarantine folder that held it. No
/// SQLite type here.
/// </summary>
/// <remarks>
/// <para>Written as the JSON store's files are: to <c>&lt;db&gt;.recovery.tmp</c>, flushed to the disk, renamed over the
/// journal, so it is either the old one or the new one, never half of one.</para>
/// <para>A recovery that starts while a journal of an earlier one is still there (its put back couldn't finish, and an
/// admin restores a snapshot or starts empty) keeps that one as <see cref="SqliteRecoveryJournalEntry.Earlier"/>: should
/// this one be put back in turn, the earlier one is the journal again, and a restart puts both back, the later one
/// first.</para>
/// <para>What a journal may name is checked as it is read, so a journal changed by hand can never move or delete a file
/// outside the database's own set: its files only the database and its <c>-wal</c>, <c>-shm</c> and <c>-journal</c>, by
/// name; its folder only a quarantine folder's name (<c>quarantine-yyyyMMdd-HHmmss</c>, with <c>-n</c> after it for
/// another one in the same second), which is a folder in the database's own folder. A journal that names anything else
/// isn't a journal of ours (the open's unreadable journal); an earlier recovery's entry that does, or that is nested
/// deeper than eight, is left out and said, with the folder it names.</para>
/// </remarks>
internal static partial class SqliteRecoveryJournal
{
    /// <summary>The journal's suffix after the database file's name.</summary>
    public const string Suffix = ".recovery";

    /// <summary>The step while the files are being moved into the quarantine folder.</summary>
    public const string Quarantining = "quarantine";

    /// <summary>The step once what takes the database's place (<see cref="SqliteRecoveryJournalEntry.Placed"/>) is about to be renamed into it.</summary>
    public const string Installing = "install";

    /// <summary>The step of a recovery whose put back couldn't finish: what it moved is still in its folder.</summary>
    public const string Stuck = "stuck";

    /// <summary>The largest journal read; a larger one can't be read.</summary>
    public const int MaxBytes = 64 * 1024;

    private const string TempSuffix = ".tmp";

    // Nested entries deeper than this are left out when read (and said): an admin's recoveries over a put back that can't
    // finish.
    private const int MaxDepth = 8;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Reads the journal.
    /// </summary>
    /// <param name="path">The journal's path.</param>
    /// <param name="databaseName">The database file's name: a journal may name only it and its sidecars.</param>
    /// <param name="error">Why it can't be read; <c>null</c> when it was read, or when there is none.</param>
    /// <param name="dropped">The earlier recoveries' entries left out (malformed, naming what a journal may not, or nested
    /// deeper than eight), each with the folder it names when it names one; empty when none was.</param>
    /// <returns>The entry, or <c>null</c> when there is none or it can't be read (then <paramref name="error"/> says why).</returns>
    public static SqliteRecoveryJournalEntry? Read(string path, string databaseName, out string? error, out IReadOnlyList<string> dropped)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        error = null;
        var notes = new List<string>();
        dropped = notes;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxBytes)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"it is {bytes.Length} bytes, more than a journal ever is");
                return null;
            }

            if (JsonNode.Parse(bytes) is JsonObject root && FromJson(root, 0, databaseName, notes) is { } entry)
            {
                return entry;
            }

            notes.Clear();
            error = "it isn't a recovery journal";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            notes.Clear();
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Tells whether a name is one a journal may give its quarantine folder: <c>quarantine-yyyyMMdd-HHmmss</c>, with
    /// <c>-n</c> after it, the name of a folder <see cref="SqliteDatabase"/> makes next to the database; never a path.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it is a quarantine folder's name.</returns>
    public static bool IsQuarantineName(string? name) => name is not null && QuarantineName().IsMatch(name);

    /// <summary>
    /// Tells whether a name is one a journal may name as moved: the database's, or its <c>-wal</c>, <c>-shm</c> or
    /// <c>-journal</c>.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="databaseName">The database file's name.</param>
    /// <returns>Whether it is one of the database's own files.</returns>
    public static bool IsDatabaseFileName(string? name, string databaseName)
        => name is not null && (string.Equals(name, databaseName, StringComparison.Ordinal)
            || SqliteFiles.SidecarSuffixes.Any(s => string.Equals(name, databaseName + s, StringComparison.Ordinal)));

    /// <summary>
    /// Writes the journal: to a temporary file, flushed to the disk, renamed over it.
    /// </summary>
    /// <param name="path">The journal's path.</param>
    /// <param name="entry">The entry.</param>
    /// <exception cref="IOException">It couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The server may not write it.</exception>
    public static void Write(string path, SqliteRecoveryJournalEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(entry);
        var bytes = Encoding.UTF8.GetBytes(ToJson(entry).ToJsonString(WriteOptions));
        var temp = path + TempSuffix;
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next open deletes it.
            }

            throw;
        }
    }

    /// <summary>
    /// The identity of a file a recovery puts in the database's place: its size and SHA-256, read with every sharing.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="IOException">It couldn't be read.</exception>
    public static SqliteRecoveryJournalPlaced Identify(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = stream.Length;
        return new SqliteRecoveryJournalPlaced(size, Convert.ToHexString(SHA256.HashData(stream)));
    }

    /// <summary>
    /// Tells whether the file at a path is, byte for byte, the one a recovery put there: a regular file of the recorded size
    /// and SHA-256. <c>false</c> when it is missing, a folder, another file, or can't be read.
    /// </summary>
    /// <param name="path">The database's path.</param>
    /// <param name="placed">What the journal recorded.</param>
    /// <returns>Whether it is that file.</returns>
    public static bool Matches(string path, SqliteRecoveryJournalPlaced placed)
    {
        ArgumentNullException.ThrowIfNull(placed);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != placed.Size)
            {
                return false;
            }

            return string.Equals(Identify(path).Sha256, placed.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static JsonObject ToJson(SqliteRecoveryJournalEntry entry)
    {
        var json = new JsonObject
        {
            ["Folder"] = entry.Folder,
            ["Files"] = new JsonArray(entry.Files.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            ["Step"] = entry.Step,
            ["Utc"] = entry.Utc,
            ["Damaged"] = entry.Damaged,
            ["Reason"] = entry.Reason,
            ["Snapshot"] = entry.Snapshot,
            ["Placed"] = entry.Placed is { } placed ? new JsonObject { ["Size"] = placed.Size, ["Sha256"] = placed.Sha256 } : null,
            ["Failure"] = entry.Failure
        };
        if (entry.Earlier is { } earlier)
        {
            json["Earlier"] = ToJson(earlier);
        }

        return json;
    }

    private static SqliteRecoveryJournalEntry? FromJson(JsonObject json, int depth, string databaseName, List<string> notes)
    {
        if (json["Files"] is not JsonArray files || Text(json["Step"]) is not { } step || Text(json["Reason"]) is not { } reason)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var file in files)
        {
            // The database's own files, by name: a path, a name that goes up or any other file is no journal of ours,
            // so nothing outside the database's set is ever moved back or deleted because a journal names it.
            if (Text(file) is not { Length: > 0 } name || name.IndexOfAny(['/', '\\', ':']) >= 0 || !IsDatabaseFileName(name, databaseName))
            {
                return null;
            }

            names.Add(name);
        }

        // A quarantine folder's name, so a folder in the database's own folder; none when nothing was to be moved.
        var folder = Text(json["Folder"]);
        if (folder is not null && (folder.IndexOfAny(['/', '\\', ':']) >= 0 || !IsQuarantineName(folder)))
        {
            return null;
        }

        SqliteRecoveryJournalPlaced? placed = null;
        if (json["Placed"] is JsonObject identity)
        {
            if (identity["Size"] is not JsonValue size || !size.TryGetValue<long>(out var bytes) || Text(identity["Sha256"]) is not { Length: 64 } hash)
            {
                return null;
            }

            placed = new SqliteRecoveryJournalPlaced(bytes, hash);
        }

        var utc = json["Utc"] is JsonValue time && time.TryGetValue<DateTime>(out var parsed) ? parsed.ToUniversalTime() : DateTime.MinValue;
        var damaged = json["Damaged"] is JsonValue flag && flag.TryGetValue<bool>(out var isDamaged) && isDamaged;
        SqliteRecoveryJournalEntry? earlier = null;
        if (json["Earlier"] is { } before)
        {
            // An earlier recovery's entry that can't be used is left out, never followed, and said with the folder it names
            // (what that recovery moved may still be in it).
            var named = before is JsonObject beforeObject ? Text(beforeObject["Folder"]) : null;
            var where = named is null ? string.Empty : " (it names the folder \"" + named + "\")";
            if (depth + 1 > MaxDepth)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"an earlier recovery's entry nested deeper than {MaxDepth}{where}"));
            }
            else if (before is not JsonObject inner || (earlier = FromJson(inner, depth + 1, databaseName, notes)) is null)
            {
                notes.Add("an earlier recovery's entry that isn't a journal of ours" + where);
            }
        }

        return new SqliteRecoveryJournalEntry(folder, names, step, utc, damaged, reason, Text(json["Snapshot"]), placed, Text(json["Failure"]), earlier);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // QuarantinePrefix, the timestamp (yyyyMMdd-HHmmss), and -n for another folder in the same second (Quarantine's names).
    [GeneratedRegex(@"\A" + SqliteDatabase.QuarantinePrefix + @"[0-9]{8}-[0-9]{6}(?:-[1-9][0-9]{0,8})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuarantineName();
}
