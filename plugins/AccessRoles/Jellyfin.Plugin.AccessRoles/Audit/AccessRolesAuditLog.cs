using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// The audit log: append-only JSON Lines in <c>data/accessroles/audit/audit-&lt;yyyyMM&gt;.jsonl</c>,
/// one file per month (UTC), numbered across files, read newest first with paging, exported as they are, and rotated by
/// the daily maintenance: files older than <c>AuditRetentionMonths</c> (12) go, then the oldest ones while all of them
/// are over 50 MB (never the current month's).
/// </summary>
/// <remarks>
/// <para>The lines hold user, role and library names, so only admin endpoints read them; the server log gets
/// counts only. A line that can't be read (a write cut short) is skipped; a write starts on a new line after one. A write
/// that fails is logged and counted in Diagnostics and never undoes the change it describes.</para>
/// </remarks>
internal sealed partial class AccessRolesAuditLog : IDisposable
{
    /// <summary>The size above which the oldest files go (the 50 MB cap).</summary>
    public const long CapBytes = 50L * 1024 * 1024;

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Func<int> _retentionMonths;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly long _capBytes;
    private long? _lastSeq;
    private int _writeErrors;
    private string? _lastError;
    private DateTime? _lastWriteUtc;
    private IDictionary<string, object?>? _lastRetention;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesAuditLog"/> class.
    /// </summary>
    /// <param name="directory">The audit folder (<c>AccessRolesPaths.AuditDirectory</c>); created at the first write.</param>
    /// <param name="retentionMonths">Reads <c>AuditRetentionMonths</c>; 0 or less keeps every month (the cap still applies).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="capBytes">The size above which the oldest months go; <see cref="CapBytes"/> by default.</param>
    public AccessRolesAuditLog(string directory, Func<int> retentionMonths, ILogger logger, TimeProvider? timeProvider = null, long capBytes = CapBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(retentionMonths);
        ArgumentNullException.ThrowIfNull(logger);
        Directory = directory;
        _retentionMonths = retentionMonths;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capBytes = capBytes;
    }

    /// <summary>Gets the line format: PascalCase, one line, nulls left out, ids in Jellyfin's <c>N</c> form.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new AuditGuidConverter() }
    };

    /// <summary>Gets the audit folder.</summary>
    public string Directory { get; }

    /// <summary>
    /// Gets a month's file name.
    /// </summary>
    /// <param name="utc">A time in the month, UTC.</param>
    /// <returns><c>audit-&lt;yyyyMM&gt;.jsonl</c>.</returns>
    public static string FileName(DateTime utc) => "audit-" + utc.ToString("yyyyMM", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>
    /// Appends lines, numbered after the last line there is, with the time now.
    /// </summary>
    /// <param name="records">The lines.</param>
    /// <param name="cancellationToken">Cancels waiting for another write.</param>
    /// <returns>The lines written; none when the write failed (logged and counted).</returns>
    public async Task<IReadOnlyList<AuditEntry>> AppendAsync(IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var seq = _lastSeq ?? ReadLastSeq();
            var entries = records.Select(r => new AuditEntry
            {
                Seq = ++seq,
                Utc = now,
                Actor = r.Actor,
                Action = r.Action,
                TargetUserId = r.TargetUserId,
                TargetRoleId = r.TargetRoleId,
                TargetName = r.TargetName,
                Details = r.Details
            }).ToList();

            var text = new StringBuilder();
            foreach (var entry in entries)
            {
                text.Append(JsonSerializer.Serialize(entry, Options)).Append('\n');
            }

            System.IO.Directory.CreateDirectory(Directory);
            Append(Path.Combine(Directory, FileName(now)), text);
            _lastSeq = seq;
            _lastWriteUtc = now;
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastSeq = null;
            _writeErrors++;
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Access Roles couldn't write {Count} audit lines to its audit folder", records.Count);
            return [];
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads a page of the log, newest first (<c>GET /AccessRoles/Admin/Audit</c>).
    /// </summary>
    /// <param name="startIndex">The first line of the page among the matching ones.</param>
    /// <param name="limit">The page's size.</param>
    /// <param name="userId">Only lines about this user or by this admin, when set.</param>
    /// <param name="action">Only lines of this action (any letter case), when set.</param>
    /// <param name="fromUtc">Only lines since then, when set.</param>
    /// <param name="toUtc">Only lines before then, when set.</param>
    /// <param name="rule">Only lines of this rule, when set (<see cref="OfRule"/>).</param>
    /// <param name="beforeSeq">
    /// Only lines older than the line of this <c>Seq</c>, when set: the cursor of the next page,
    /// which lines written meanwhile don't shift as they shift an index.
    /// </param>
    /// <returns>The page.</returns>
    public AuditPage Read(int startIndex, int limit, Guid? userId, string? action, DateTime? fromUtc, DateTime? toUtc = null, string? rule = null, long? beforeSeq = null)
    {
        var matches = new List<AuditEntry>();
        foreach (var (path, month) in Files().OrderByDescending(f => f.Month))
        {
            if ((fromUtc is { } from && month.AddMonths(1) <= from) || (toUtc is { } to && month >= to))
            {
                continue;
            }

            matches.AddRange(ReadEntries(path).Select(e => e.Entry).Where(e =>
                (userId is not { } id || id.Equals(e.TargetUserId) || id.Equals(e.Actor.UserId))
                && (string.IsNullOrEmpty(action) || string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase))
                && (fromUtc is not { } since || e.Utc >= since)
                && (toUtc is not { } until || e.Utc < until)
                && (beforeSeq is not { } before || e.Seq < before)
                && (string.IsNullOrEmpty(rule) || OfRule(e, rule))));
        }

        var ordered = matches.OrderByDescending(e => e.Seq).ToList();
        return new AuditPage([.. ordered.Skip(startIndex).Take(limit)], ordered.Count, startIndex);
    }

    /// <summary>
    /// Gets whether a line names a rule: its <c>Rule</c> detail is the rule, ignoring letter case, or one of the
    /// rules a name joined with <c>+</c> names (<c>ServerHardening.RuleNames</c>).
    /// </summary>
    /// <param name="entry">The line.</param>
    /// <param name="rule">The rule, or a name joined with <c>+</c>.</param>
    /// <returns>Whether it names it.</returns>
    public static bool OfRule(AuditEntry entry, string rule)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rule);
        if (entry.Details is null || !entry.Details.TryGetValue("Rule", out var value) || value is null)
        {
            return false;
        }

        var name = value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element ? element.GetString() : value.ToString();
        return !string.IsNullOrEmpty(name)
            && (string.Equals(name, rule, StringComparison.OrdinalIgnoreCase)
                || name.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(rule, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the lines of a time range as they are in the files, oldest first (<c>Audit/Export</c>).
    /// </summary>
    /// <param name="fromUtc">Only lines since then, when set.</param>
    /// <param name="toUtc">Only lines before then, when set.</param>
    /// <returns>The lines' text.</returns>
    public IReadOnlyList<string> ReadLines(DateTime? fromUtc, DateTime? toUtc)
    {
        var lines = new List<string>();
        foreach (var (path, month) in Files().OrderBy(f => f.Month))
        {
            if ((fromUtc is { } from && month.AddMonths(1) <= from) || (toUtc is { } to && month >= to))
            {
                continue;
            }

            lines.AddRange(ReadEntries(path).Where(e => (fromUtc is not { } since || e.Entry.Utc >= since) && (toUtc is not { } until || e.Entry.Utc < until)).Select(e => e.Line));
        }

        return lines;
    }

    /// <summary>
    /// Rotates the files (the daily maintenance's <c>Audit</c> step): months older than
    /// <c>AuditRetentionMonths</c> go, then the oldest months while the files together are over <see cref="CapBytes"/>;
    /// the current month's file always stays.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for a write.</param>
    /// <returns>What was done, for Diagnostics <c>Maintenance</c>.</returns>
    public async Task<IDictionary<string, object?>> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var current = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var months = _retentionMonths();
            var files = Files().OrderBy(f => f.Month).Select(f => (f.Path, f.Month, Size: new FileInfo(f.Path).Length)).ToList();
            var deleted = new List<string>();
            foreach (var file in files.ToList())
            {
                var expired = months > 0 && file.Month.AddMonths(1) <= now.AddMonths(-months);
                if (file.Month < current && (expired || files.Sum(f => f.Size) > _capBytes))
                {
                    File.Delete(file.Path);
                    files.Remove(file);
                    deleted.Add(Path.GetFileName(file.Path));
                }
            }

            if (deleted.Count > 0)
            {
                _logger.LogInformation("Access Roles' audit retention removed {Count} monthly audit files", deleted.Count);
            }

            _lastRetention = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["RetentionMonths"] = months,
                ["Deleted"] = deleted,
                ["Files"] = files.Count,
                ["Bytes"] = files.Sum(f => f.Size)
            };
            return _lastRetention;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Describes the log for Diagnostics: its files, the last line's number, the write errors and the last rotation.
    /// </summary>
    /// <returns>The entry.</returns>
    public IDictionary<string, object?> Describe()
    {
        var files = Files().OrderBy(f => f.Month).ToList();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Directory"] = Directory,
            ["Files"] = files.Count,
            ["Bytes"] = files.Sum(f => new FileInfo(f.Path).Length),
            ["Oldest"] = files.Count == 0 ? null : Path.GetFileName(files[0].Path),
            ["LastSeq"] = _lastSeq ?? ReadLastSeq(),
            ["LastWriteUtc"] = _lastWriteUtc,
            ["WriteErrors"] = _writeErrors,
            ["LastError"] = _lastError,
            ["RetentionMonths"] = _retentionMonths(),
            ["CapBytes"] = _capBytes,
            ["LastRetention"] = _lastRetention
        };
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    [GeneratedRegex(@"^audit-(?<year>\d{4})(?<month>\d{2})\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    // Small and rare (an admin's change), so written synchronously and flushed to disk before the request answers.
    private static void Append(string path, StringBuilder text)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        // A write cut short leaves a line without its end: start on a new line, so only that one is lost.
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                text.Insert(0, '\n');
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(_utf8.GetBytes(text.ToString()));
        stream.Flush(flushToDisk: true);
    }

    private static IEnumerable<(AuditEntry Entry, string Line)> ReadEntries(string path)
    {
        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, _utf8);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            AuditEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<AuditEntry>(line, Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null)
            {
                yield return (entry, line);
            }
        }
    }

    private List<(string Path, DateTime Month)> Files()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var files = new List<(string Path, DateTime Month)>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "audit-*.jsonl"))
        {
            var match = FileNamePattern().Match(Path.GetFileName(path));
            if (match.Success
                && int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month)
                && year >= 2000 && month is >= 1 and <= 12)
            {
                files.Add((path, new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc)));
            }
        }

        return files;
    }

    private long ReadLastSeq()
    {
        foreach (var (path, _) in Files().OrderByDescending(f => f.Month))
        {
            var last = ReadEntries(path).Select(e => e.Entry.Seq).DefaultIfEmpty(-1).Max();
            if (last >= 0)
            {
                return last;
            }
        }

        return 0;
    }
}
