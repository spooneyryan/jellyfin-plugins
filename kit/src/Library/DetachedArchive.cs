using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The archive of one store's detached records, <c>&lt;data&gt;/&lt;folder&gt;/archive/&lt;store&gt;-archived.jsonl</c>:
/// append-only JSON Lines, never rotated. Records move here from the live store after
/// the owner's retention (<see cref="DetachedRecords"/>), are exported, still reattach when their item returns, and leave
/// only through the admin's <i>Purge</i>, the one rewrite of the file.
/// </summary>
/// <remarks>
/// <para><b>Lines</b> (UTF-8, one JSON object each, <c>V</c> 1, a <c>Seq</c> that grows by one per line):
/// <c>{"V":1,"Op":"Archived","Seq":…,"Utc":…,"Id":…,"UserId":…,"Identity":{…},"AttachedUtc":…,"DetachedUtc":…,"Record":{…}}</c>,
/// with <c>"Reason":…</c> for a record its owner archived for a reason of its own (<see cref="ArchivedRecord.Reason"/>;
/// absent for a detached record, so those lines are as before);
/// <c>{"V":1,"Op":"Reattached","Seq":…,"Utc":…,"Ids":[…]}</c> (put back into the live store);
/// <c>{"V":1,"Op":"Superseded","Seq":…,"Utc":…,"Ids":[…]}</c> (the live store kept the record: its archived copy
/// doesn't count, so a record later deleted by its user never comes back from here);
/// <c>{"V":1,"Op":"Checked","Seq":…,"Utc":…,"Through":…}</c> (where the daily reattach left off). Lines of another
/// <c>V</c> or with an unknown <c>Op</c> are ignored (forward compatibility); a last line without its line feed that
/// isn't complete JSON is a write cut short and is ignored.</para>
/// <para><b>Order of writes.</b> Archiving appends and flushes the lines inside the store's writer lock, just before the
/// store's write that removes the records, and a failed store write is answered with a <c>Superseded</c> line; a record in
/// both places (a crash between the two writes) counts as live, and <see cref="DetachedRecords.ReconcileAsync"/> marks its
/// archived copy superseded at the next start. Reattaching writes the live store first, then <c>Reattached</c>.</para>
/// </remarks>
internal sealed class DetachedArchive
{
    /// <summary>The archive folder under the plugin's data folder.</summary>
    public const string Folder = "archive";

    /// <summary>The file name's suffix after the store's name.</summary>
    public const string FileSuffix = "-archived.jsonl";

    /// <summary>The format version of the lines.</summary>
    public const int FormatVersion = 1;

    /// <summary>A larger file isn't read.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    private const string OpArchived = "Archived";
    private const string OpReattached = "Reattached";
    private const string OpSuperseded = "Superseded";
    private const string OpChecked = "Checked";

    private static readonly JsonSerializerOptions LineOptions = CreateLineOptions();

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private (long Length, long Ticks, ArchiveContents Contents)? _cached;
    private bool _problemLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetachedArchive"/> class.
    /// </summary>
    /// <param name="directory">The archive folder (<c>&lt;data&gt;/&lt;folder&gt;/archive</c>).</param>
    /// <param name="storeName">The store's name.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    public DetachedArchive(string directory, string storeName, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(storeName);
        ArgumentNullException.ThrowIfNull(logger);
        if (storeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The store name must be usable as a file name.", nameof(storeName));
        }

        StoreName = storeName;
        FilePath = Path.GetFullPath(Path.Combine(directory, storeName + FileSuffix));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the store's name.
    /// </summary>
    public string StoreName { get; }

    /// <summary>
    /// Gets the archive file's full path.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the name the pre-purge copies carry in the snapshot folder's <c>pre-op</c> class
    /// (<c>&lt;store&gt;-archived-purge-&lt;ts&gt;.jsonl</c>).
    /// </summary>
    public string SnapshotName => StoreName + "-archived";

    /// <summary>
    /// Reads the archive, or returns what was read before when the file hasn't changed.
    /// </summary>
    /// <returns>The archived records and the reattach position; <see cref="ArchiveContents.Empty"/> without a file.</returns>
    public ArchiveContents Read()
    {
        lock (_lock)
        {
            return ReadLocked();
        }
    }

    /// <summary>
    /// Appends records and flushes them to disk. Called inside the owner's store lock, before the store's write that
    /// removes them from the live store.
    /// </summary>
    /// <param name="records">The records (their <see cref="ArchivedRecord.Seq"/> and <see cref="ArchivedRecord.ArchivedUtc"/>
    /// are set here).</param>
    /// <returns>The records as written.</returns>
    /// <exception cref="IOException">The archive couldn't be written; the records must stay live.</exception>
    public IReadOnlyList<ArchivedRecord> Append(IReadOnlyList<ArchivedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        lock (_lock)
        {
            var contents = ReadLocked();
            if (contents.Problem is not null)
            {
                throw new IOException(contents.Problem);
            }

            var utc = _timeProvider.GetUtcNow().UtcDateTime;
            var seq = contents.NextSeq;
            var written = records.Select(r => r with { Seq = seq++, ArchivedUtc = utc }).ToList();
            WriteLines(written.Select(r => new ArchiveLine
            {
                Op = OpArchived,
                Seq = r.Seq,
                Utc = utc,
                Id = r.Id,
                UserId = r.UserId,
                Identity = r.Identity,
                AttachedUtc = r.AttachedUtc,
                DetachedUtc = r.DetachedUtc,
                Record = r.Record,
                Reason = r.Reason
            }));
            return written;
        }
    }

    /// <summary>
    /// Records that the live store has these records again: put back by the reattach.
    /// </summary>
    /// <param name="ids">The record ids.</param>
    public void MarkReattached(IReadOnlyCollection<string> ids) => MarkIds(OpReattached, ids);

    /// <summary>
    /// Records that the live store kept these records, so their archived copies don't count (a store write that failed
    /// after the archive was written, or a crash in between).
    /// </summary>
    /// <param name="ids">The record ids.</param>
    public void MarkSuperseded(IReadOnlyCollection<string> ids) => MarkIds(OpSuperseded, ids);

    /// <summary>
    /// Records where the daily reattach left off; nothing when it left off there already (so an archive whose records
    /// never come back doesn't grow by a line a day).
    /// </summary>
    /// <param name="throughSeq">The <see cref="ArchivedRecord.Seq"/> of the last record checked.</param>
    public void MarkChecked(long throughSeq)
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var contents = ReadLocked();
            if (contents.Problem is not null)
            {
                throw new IOException(contents.Problem);
            }

            if (contents.LastCheckedSeq == throughSeq)
            {
                return;
            }

            WriteLines([new ArchiveLine { Op = OpChecked, Seq = contents.NextSeq, Utc = _timeProvider.GetUtcNow().UtcDateTime, Through = throughSeq }]);
        }
    }

    /// <summary>
    /// The next records for the daily reattach, least recently checked first: those after the last checked one in archive
    /// order, then from the start again.
    /// </summary>
    /// <param name="max">At most this many.</param>
    /// <param name="which">Only the records this accepts count (the reattach takes those without a
    /// <see cref="ArchivedRecord.Reason"/>); every record when null.</param>
    /// <returns>The records.</returns>
    public IReadOnlyList<ArchivedRecord> NextToCheck(int max, Func<ArchivedRecord, bool>? which = null)
    {
        var contents = Read();
        var records = which is null ? contents.Records : contents.Records.Where(which);
        var after = records.Where(r => r.Seq > contents.LastCheckedSeq);
        var wrapped = records.Where(r => r.Seq <= contents.LastCheckedSeq);
        return after.Concat(wrapped).Take(Math.Max(0, max)).ToList();
    }

    /// <summary>
    /// Deletes archived records for good (the admin's <i>Purge</i>): the file is rewritten without them, as a flushed
    /// <c>.tmp</c> moved into place. A copy of the file as it was goes to the snapshot folder's pre-operation class
    /// first (<c>pre-op/&lt;store&gt;-archived-purge-&lt;ts&gt;.jsonl</c>, the newest 5 kept, and always the one just
    /// made; none dated more than a day ahead is deleted or counted). The rewrite keeps only the records as they are
    /// now, so the earlier lines of every record (archived, then reattached or superseded) go with it, and so do the
    /// lines that can't be read (torn by a crash mid-write, or broken: they can't be parsed or recovered, and may hold
    /// a purged record's text).
    /// </summary>
    /// <param name="which">Which records go.</param>
    /// <param name="snapshotDirectory">The snapshot folder, or null for no copy.</param>
    /// <param name="operation">The copy's operation (<see cref="SnapshotOperations.All"/>): <c>purge</c> by default; a
    /// plugin that purges a deleted user's records names it <c>user-deleted</c>.</param>
    /// <param name="includeHistory">Whether the earlier lines count too (opt-in): an archived line that is no longer a
    /// record (it was reattached, superseded, or archived again since) and that <paramref name="which"/> picks makes
    /// the file rewritten even when no record is purged, so none of its text is left; it isn't counted as purged. So
    /// does a line that can't be read: whose text it holds can't be told.</param>
    /// <param name="keepWithin">Keeps the copies by age instead (<see cref="SnapshotRotation.RotatePreOp"/>'s
    /// <c>keepWithin</c>: those of this time, none older, the one just made always); null keeps the newest five.</param>
    /// <returns>How many records were deleted, and the copy's file name.</returns>
    public (int Purged, string? Snapshot) Purge(Func<ArchivedRecord, bool> which, string? snapshotDirectory, string operation = SnapshotOperations.Purge, bool includeHistory = false, TimeSpan? keepWithin = null)
    {
        ArgumentNullException.ThrowIfNull(which);
        SnapshotOperations.Require(operation);
        lock (_lock)
        {
            var contents = ReadLocked();
            if (contents.Problem is not null)
            {
                throw new IOException(contents.Problem);
            }

            var purged = contents.Records.Where(which).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var unreadable = contents.InvalidLines + (contents.TruncatedTail ? 1 : 0);
            if (purged.Count == 0 && !(includeHistory && (unreadable > 0 || ReadHistoryLocked().Any(which))))
            {
                return (0, null);
            }

            string? snapshot = null;
            if (snapshotDirectory is not null)
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var copy = SnapshotRotation.NewPreOpPath(snapshotDirectory, SnapshotName, operation, ".jsonl", now);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(FilePath, copy, overwrite: false);
                snapshot = Path.GetFileName(copy);

                // The copy is the only one of the records about to be purged: its rotation never deletes it, even when
                // the clock is behind the other copies' names, and never deletes or counts one dated in the future (as
                // the JSON store's rotation does).
                SnapshotRotation.RotatePreOp(snapshotDirectory, SnapshotName, ".jsonl", SnapshotOperations.ToKeep, _logger, spare: snapshot, nowUtc: now, keepWithin: keepWithin);
            }

            var utc = _timeProvider.GetUtcNow().UtcDateTime;
            var kept = contents.Records.Where(r => !purged.Contains(r.Id)).ToList();
            var lines = kept.Select(r => new ArchiveLine
            {
                Op = OpArchived,
                Seq = r.Seq,
                Utc = r.ArchivedUtc,
                Id = r.Id,
                UserId = r.UserId,
                Identity = r.Identity,
                AttachedUtc = r.AttachedUtc,
                DetachedUtc = r.DetachedUtc,
                Record = r.Record,
                Reason = r.Reason
            }).ToList();
            lines.Add(new ArchiveLine { Op = OpChecked, Seq = contents.NextSeq, Utc = utc, Through = contents.LastCheckedSeq });

            var temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var line in lines)
                {
                    stream.Write(Serialize(line));
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, FilePath, overwrite: true);
            _cached = null;
            _logger.LogInformation("Purged {Count} archived records of {Store}; its earlier lines went with the rewrite", purged.Count, StoreName);
            if (unreadable > 0)
            {
                _logger.LogWarning("The rewrite of the archive {File} dropped {Count} lines that couldn't be read", FilePath, unreadable);
            }

            return (purged.Count, snapshot);
        }
    }

    /// <summary>
    /// Takes a pre-operation copy of the archive as it is now,
    /// <c>&lt;store&gt;-archived-&lt;operation&gt;-&lt;ts&gt;.jsonl</c> in the snapshot folder, under the archive's
    /// lock (an import's copy, taken with its database snapshot, so restoring that snapshot puts back the archive the
    /// import found). Without a file the copy is empty: the archive had no records then. The copies are rotated as
    /// <see cref="Purge"/>'s are, the one just made always kept.
    /// </summary>
    /// <param name="snapshotDirectory">The snapshot folder.</param>
    /// <param name="operation">The copy's operation (<see cref="SnapshotOperations.All"/>).</param>
    /// <param name="keepWithin">Keeps the copies by age (<see cref="SnapshotRotation.RotatePreOp"/>); null keeps the newest
    /// five.</param>
    /// <returns>The copy's file name.</returns>
    /// <exception cref="IOException">The copy couldn't be written.</exception>
    public string CopyToSnapshots(string snapshotDirectory, string operation, TimeSpan? keepWithin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        SnapshotOperations.Require(operation);
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var copy = SnapshotRotation.NewPreOpPath(snapshotDirectory, SnapshotName, operation, ".jsonl", now);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, copy, overwrite: false);
            }
            else
            {
                using var empty = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                empty.Flush(flushToDisk: true);
            }

            var name = Path.GetFileName(copy);
            SnapshotRotation.RotatePreOp(snapshotDirectory, SnapshotName, ".jsonl", SnapshotOperations.ToKeep, _logger, spare: name, nowUtc: now, keepWithin: keepWithin);
            return name;
        }
    }

    /// <summary>
    /// Puts a pre-operation copy back as the archive, under the archive's lock (a restore's archive part, which used to
    /// swap the file without it). The archive as it is now is first kept as a <c>restore</c> copy, unless there is
    /// none; the copy is written beside the archive and moved over it, so the archive is the one or the other.
    /// </summary>
    /// <param name="copyPath">The copy to put back.</param>
    /// <param name="snapshotDirectory">The snapshot folder, for the archive's own copy.</param>
    /// <returns>The file name of the archive's own copy, or null when there was no archive.</returns>
    /// <exception cref="IOException">The copy couldn't be read, or the archive written; the archive is as it was.</exception>
    public string? RestoreFrom(string copyPath, string snapshotDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(copyPath);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        lock (_lock)
        {
            string? saved = null;
            if (File.Exists(FilePath))
            {
                var own = SnapshotRotation.NewPreOpPath(snapshotDirectory, SnapshotName, SnapshotOperations.Restore, ".jsonl", _timeProvider.GetUtcNow().UtcDateTime);
                Directory.CreateDirectory(Path.GetDirectoryName(own)!);
                File.Copy(FilePath, own, overwrite: false);
                saved = Path.GetFileName(own);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".restore-tmp";
            try
            {
                File.Copy(copyPath, temp, overwrite: true);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            _cached = null;
            _logger.LogInformation("The archive of {Store} was put back from {Copy}", StoreName, Path.GetFileName(copyPath));
            return saved;
        }
    }

    /// <summary>
    /// Makes the archive these records and only these, under the archive's lock (an envelope Replace makes the archive
    /// the file's, as it makes the ratings the file's). The file is rewritten beside the archive and moved over it;
    /// each record gets a new <see cref="ArchivedRecord.Seq"/> from 1 and <see cref="ArchivedRecord.ArchivedUtc"/> now,
    /// as <see cref="Append"/> gives them, and the reattach starts from the first. Take a pre-operation copy first
    /// (<see cref="CopyToSnapshots"/>): the earlier records are gone.
    /// </summary>
    /// <param name="records">The records.</param>
    /// <returns>The records as written.</returns>
    /// <exception cref="IOException">The archive couldn't be written; it is as it was.</exception>
    public IReadOnlyList<ArchivedRecord> ReplaceRecords(IReadOnlyList<ArchivedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_lock)
        {
            var utc = _timeProvider.GetUtcNow().UtcDateTime;
            long seq = 1;
            var written = records.Where(r => r is not null)
                .GroupBy(r => r.Id, StringComparer.Ordinal)
                .Select(g => g.First() with { Seq = seq++, ArchivedUtc = utc })
                .ToList();
            var lines = written.Select(r => new ArchiveLine
            {
                Op = OpArchived,
                Seq = r.Seq,
                Utc = r.ArchivedUtc,
                Id = r.Id,
                UserId = r.UserId,
                Identity = r.Identity,
                AttachedUtc = r.AttachedUtc,
                DetachedUtc = r.DetachedUtc,
                Record = r.Record,
                Reason = r.Reason
            }).ToList();
            lines.Add(new ArchiveLine { Op = OpChecked, Seq = seq, Utc = utc, Through = 0 });

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var line in lines)
                    {
                        stream.Write(Serialize(line));
                    }

                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, FilePath, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            _cached = null;
            _logger.LogInformation("The archive of {Store} was replaced: {Count} records", StoreName, written.Count);
            return written;
        }
    }

    /// <summary>
    /// Parses archive bytes (for tests and <see cref="Read"/>).
    /// </summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <param name="history">When given, collects every archived line that is no longer a record: replaced by a later line
    /// of the same id, or removed by a reattached or superseded line (<see cref="Purge"/>'s <c>includeHistory</c>).</param>
    /// <returns>The folded contents.</returns>
    internal static ArchiveContents Parse(ReadOnlySpan<byte> utf8, List<ArchivedRecord>? history = null)
    {
        utf8 = JsonStoreFormat.SkipByteOrderMark(utf8);
        var current = new Dictionary<string, ArchivedRecord>(StringComparer.Ordinal);
        var historyIds = new HashSet<string>(StringComparer.Ordinal);
        long lastChecked = 0;
        long maxSeq = 0;
        var ignored = 0;
        var invalid = 0;
        var truncated = false;
        var position = 0;
        while (position < utf8.Length)
        {
            var end = utf8[position..].IndexOf((byte)'\n');
            var hasNewline = end >= 0;
            var lineBytes = hasNewline ? utf8.Slice(position, end) : utf8[position..];
            position = hasNewline ? position + end + 1 : utf8.Length;
            if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
            {
                lineBytes = lineBytes[..^1];
            }

            if (lineBytes.Trim((byte)' ').Length == 0)
            {
                continue;
            }

            ArchiveLine? line;
            try
            {
                line = JsonSerializer.Deserialize<ArchiveLine>(lineBytes, LineOptions);
            }
            catch (JsonException)
            {
                if (hasNewline)
                {
                    invalid++;
                }
                else
                {
                    truncated = true;
                }

                continue;
            }

            if (line is null)
            {
                invalid++;
                continue;
            }

            maxSeq = Math.Max(maxSeq, line.Seq);
            if (line.V != FormatVersion)
            {
                ignored++;
                continue;
            }

            switch (line.Op)
            {
                case OpArchived when line is { Id.Length: > 0, Identity: not null, Record: not null, DetachedUtc: not null }:
                    if (current.TryGetValue(line.Id, out var replaced))
                    {
                        historyIds.Add(replaced.Id);
                        history?.Add(replaced);
                    }

                    current[line.Id] = new ArchivedRecord
                    {
                        Id = line.Id,
                        UserId = line.UserId,
                        Identity = line.Identity,
                        AttachedUtc = line.AttachedUtc,
                        DetachedUtc = line.DetachedUtc.Value,
                        Record = line.Record,
                        ArchivedUtc = line.Utc,
                        Seq = line.Seq,
                        Reason = string.IsNullOrEmpty(line.Reason) ? null : line.Reason
                    };
                    break;
                case OpReattached or OpSuperseded when line.Ids is not null:
                    foreach (var id in line.Ids)
                    {
                        if (id is not null && current.TryGetValue(id, out var record) && record.Seq < line.Seq)
                        {
                            current.Remove(id);
                            historyIds.Add(record.Id);
                            history?.Add(record);
                        }
                    }

                    break;
                case OpChecked when line.Through is { } through:
                    lastChecked = through;
                    break;
                case OpArchived or OpReattached or OpSuperseded or OpChecked:
                    invalid++;
                    break;
                default:
                    ignored++;
                    break;
            }
        }

        return new ArchiveContents
        {
            Exists = true,
            Records = current.Values.OrderBy(r => r.Seq).ToList(),
            HistoryIds = historyIds,
            LastCheckedSeq = lastChecked,
            NextSeq = maxSeq + 1,
            IgnoredLines = ignored,
            InvalidLines = invalid,
            TruncatedTail = truncated
        };
    }

    private static JsonSerializerOptions CreateLineOptions()
    {
        var options = JsonStoreFormat.CreateOptions();
        options.WriteIndented = false;
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left beside the archive; the next rewrite overwrites it.
        }
    }

    private static byte[] Serialize(ArchiveLine line)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(line, LineOptions);
        var bytes = new byte[json.Length + 1];
        json.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    private void MarkIds(string op, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var contents = ReadLocked();
            if (contents.Problem is not null)
            {
                throw new IOException(contents.Problem);
            }

            WriteLines([new ArchiveLine { Op = op, Seq = contents.NextSeq, Utc = _timeProvider.GetUtcNow().UtcDateTime, Ids = list }]);
        }
    }

    private ArchiveContents ReadLocked()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                _cached = null;
                return ArchiveContents.Empty;
            }

            if (_cached is { } cached && cached.Length == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
            {
                return cached.Contents;
            }

            if (info.Length > MaxBytes)
            {
                return Unreadable(string.Create(CultureInfo.InvariantCulture, $"The archive {FilePath} is larger than {MaxBytes / (1024 * 1024)} MB; it is not read."), null);
            }

            byte[] bytes;
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            var contents = Parse(bytes);
            if (contents.InvalidLines > 0)
            {
                _logger.LogWarning("The archive {File} has {Count} lines that can't be read; they are ignored", FilePath, contents.InvalidLines);
            }

            _problemLogged = false;
            _cached = (info.Length, info.LastWriteTimeUtc.Ticks, contents);
            return contents;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable($"The archive {FilePath} couldn't be read: {ex.Message}", ex);
        }
    }

    // Under _lock, after ReadLocked found the file readable: the archived lines that are no longer records (Parse's history).
    private List<ArchivedRecord> ReadHistoryLocked()
    {
        var history = new List<ArchivedRecord>();
        if (File.Exists(FilePath))
        {
            Parse(File.ReadAllBytes(FilePath), history);
        }

        return history;
    }

    private ArchiveContents Unreadable(string problem, Exception? ex)
    {
        if (!_problemLogged)
        {
            _problemLogged = true;
            _logger.LogWarning(ex, "{Problem}", problem);
        }

        return ArchiveContents.Empty with { Exists = true, Problem = problem };
    }

    private void WriteLines(IEnumerable<ArchiveLine> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using (var stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            // A line cut short by a crash is left as it is (it reads as a torn line) and the new lines start on a line of
            // their own.
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                var last = stream.ReadByte();
                if (last != '\n')
                {
                    stream.WriteByte((byte)'\n');
                }
            }

            stream.Seek(0, SeekOrigin.End);
            foreach (var line in lines)
            {
                stream.Write(Serialize(line));
            }

            stream.Flush(flushToDisk: true);
        }

        _cached = null;
    }

    /// <summary>One line of the archive.</summary>
    private sealed class ArchiveLine
    {
        public int V { get; set; } = FormatVersion;

        public string Op { get; set; } = string.Empty;

        public long Seq { get; set; }

        public DateTime Utc { get; set; }

        public string? Id { get; set; }

        public Guid? UserId { get; set; }

        public IdentitySnapshot? Identity { get; set; }

        public DateTime? AttachedUtc { get; set; }

        public DateTime? DetachedUtc { get; set; }

        public JsonObject? Record { get; set; }

        // Why the owner archived the record, when it wasn't detached; left out (null) otherwise.
        public string? Reason { get; set; }

        public List<string>? Ids { get; set; }

        public long? Through { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
