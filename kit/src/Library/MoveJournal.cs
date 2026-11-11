using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Library Maintenance's move journal as one read found it (format v1): the header's
/// <see cref="JournalId"/> and the Committed lines, in <c>Seq</c> order. Immutable; <see cref="MoveJournalReader"/> reads
/// and caches it.
/// </summary>
/// <remarks>
/// <para><b>Format.</b> JSON Lines, UTF-8. The first line is the header
/// <c>{"V":1,"Header":true,"JournalId":"&lt;N&gt;","CreatedUtc":"..."}</c>; every later line is
/// <c>{"V":1,"JournalId":"&lt;N&gt;","Seq":17,"Status":"Committed","Utc":"...","OldId":"&lt;N&gt;","NewId":"&lt;N&gt;","Kind":"Movie","OldPath":"...","NewPath":"..."}</c>,
/// plus the additive <c>JobId</c>, <c>Role</c> and <c>Correction</c>. Property names are matched exactly; unknown ones are
/// ignored.</para>
/// <para><b>What counts.</b> Only lines with <c>V</c> 1, the header's <c>JournalId</c>, a <c>Seq</c> above the previous
/// line's, <c>Status</c> <c>Committed</c>, two non-empty ids and a <c>Utc</c> time. A line with another status or another
/// <c>V</c> is ignored (forward compatibility, <see cref="IgnoredLines"/>); a line that isn't one JSON object, repeats the
/// header, names another journal, or breaks the <c>Seq</c> order is invalid (<see cref="InvalidLines"/>). A last line
/// without its line feed that isn't complete JSON is a line still being written and is ignored
/// (<see cref="TruncatedTail"/>). <see cref="LastSeq"/> counts every line whose <c>Seq</c> was in order, ignored ones
/// included, so a consumer's stored position never runs ahead of it.</para>
/// </remarks>
internal sealed class MoveJournal
{
    /// <summary>The only journal format version this kit reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>The <c>Status</c> of a line consumers act on.</summary>
    public const string CommittedStatus = "Committed";

    /// <summary>
    /// How long a Committed line's <c>NewId</c> may stay unknown to the library before the record detaches normally.
    /// </summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromDays(7);

    private const string StatusProperty = "Status";

    private readonly Dictionary<Guid, List<int>> _byOldId = [];
    private readonly Dictionary<Guid, int> _lastByNewId = [];

    private MoveJournal(
        MoveJournalStatus status,
        Guid? journalId = null,
        DateTime? createdUtc = null,
        IReadOnlyList<MoveJournalLine>? lines = null,
        long lastSeq = 0,
        int ignoredLines = 0,
        int invalidLines = 0,
        bool truncatedTail = false,
        string? problem = null)
    {
        Status = status;
        JournalId = journalId;
        CreatedUtc = createdUtc;
        Lines = lines ?? [];
        LastSeq = lastSeq;
        IgnoredLines = ignoredLines;
        InvalidLines = invalidLines;
        TruncatedTail = truncatedTail;
        Problem = problem;

        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (!_byOldId.TryGetValue(line.OldId, out var indexes))
            {
                indexes = [];
                _byOldId.Add(line.OldId, indexes);
            }

            indexes.Add(i);
            _lastByNewId[line.NewId] = i;
        }
    }

    private enum LineKind
    {
        Move,
        Ignored,
        Invalid
    }

    /// <summary>
    /// Gets the journal of a server without one.
    /// </summary>
    public static MoveJournal Missing { get; } = new(MoveJournalStatus.Missing);

    /// <summary>
    /// Gets the state of the file.
    /// </summary>
    public MoveJournalStatus Status { get; }

    /// <summary>
    /// Gets the header's <c>JournalId</c>; null unless <see cref="Status"/> is <see cref="MoveJournalStatus.Ok"/>.
    /// </summary>
    public Guid? JournalId { get; }

    /// <summary>
    /// Gets the header's <c>CreatedUtc</c>, if it had a valid one.
    /// </summary>
    public DateTime? CreatedUtc { get; }

    /// <summary>
    /// Gets the Committed lines, in <c>Seq</c> order.
    /// </summary>
    public IReadOnlyList<MoveJournalLine> Lines { get; }

    /// <summary>
    /// Gets the <c>Seq</c> of the last line in order, Committed or not; 0 without one.
    /// </summary>
    public long LastSeq { get; }

    /// <summary>
    /// Gets the number of well-formed lines that aren't Committed v1 moves (another status or format version).
    /// </summary>
    public int IgnoredLines { get; }

    /// <summary>
    /// Gets the number of lines that were skipped as invalid.
    /// </summary>
    public int InvalidLines { get; }

    /// <summary>
    /// Gets a value indicating whether the last line was incomplete (still being written) and was ignored.
    /// </summary>
    public bool TruncatedTail { get; }

    /// <summary>
    /// Gets a sentence about what is wrong with the file, for the log and Diagnostics; null when nothing is.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// Follows the Committed lines from <paramref name="fromId"/>: the first line after
    /// <paramref name="afterSeq"/> whose <c>OldId</c> is the id, then the first line after that one whose <c>OldId</c> is
    /// its <c>NewId</c>, and so on. See <see cref="MoveChain"/> for why it always ends.
    /// </summary>
    /// <remarks>
    /// Item ids are MD5(type + path), so a path used again gives the old id again: after A→B, a new file at A's old path
    /// is A once more. A record attached to that new A must not follow the old A→B line, so a consumer passes the time the
    /// record was attached to its id as <paramref name="afterUtc"/>, and only lines committed after it count.
    /// </remarks>
    /// <param name="fromId">The id to start from.</param>
    /// <param name="afterSeq">Only lines with a higher <c>Seq</c> count; 0 for the whole journal.</param>
    /// <param name="afterUtc">Only lines committed after this time (UTC; a local time is converted) count; null for any time.</param>
    /// <returns>The chain; empty when no line moves <paramref name="fromId"/>.</returns>
    public MoveChain Chain(Guid fromId, long afterSeq = 0, DateTime? afterUtc = null)
    {
        var steps = new List<MoveJournalLine>();
        var visited = new HashSet<Guid> { fromId };
        var revisits = false;
        var current = fromId;
        var seq = afterSeq;
        var since = afterUtc is { Kind: DateTimeKind.Local } local ? local.ToUniversalTime() : afterUtc;

        // Each step takes a line with a higher Seq than the last one, so at most Lines.Count steps are possible.
        while (steps.Count < Lines.Count && FirstAfter(current, seq, since) is { } line)
        {
            steps.Add(line);
            seq = line.Seq;
            current = line.NewId;
            revisits |= !visited.Add(current);
        }

        return new MoveChain(fromId, current, steps, revisits);
    }

    /// <summary>
    /// Gets the Committed lines after a position, in <c>Seq</c> order.
    /// </summary>
    /// <param name="seq">The position; 0 for every line.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<MoveJournalLine> After(long seq)
        => seq <= 0 ? Lines : Lines.Where(l => l.Seq > seq).ToList();

    /// <summary>
    /// Gets the line that made <paramref name="itemId"/> the current id of a moved item: the last Committed line whose
    /// <c>NewId</c> it is, provided no later line moved it on. Null when there is none.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The line, or null.</returns>
    public MoveJournalLine? ArrivalOf(Guid itemId)
    {
        if (!_lastByNewId.TryGetValue(itemId, out var index))
        {
            return null;
        }

        var line = Lines[index];
        return FirstAfter(itemId, line.Seq) is null ? line : null;
    }

    /// <summary>
    /// Whether a record on <paramref name="itemId"/> is a pending relink: the id is where a Committed line
    /// moved an item, nothing moved it on since, and the line is less than <see cref="PendingWindow"/> old. A verify sweep
    /// asks this only for ids <c>GetItemById</c> doesn't find, and leaves such records attached; after the window they
    /// detach normally with their snapshot.
    /// </summary>
    /// <param name="itemId">The record's item id.</param>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>Whether the record is exempt from detaching for now.</returns>
    public bool IsPendingRelink(Guid itemId, DateTime nowUtc)
        => ArrivalOf(itemId) is { } line && nowUtc - line.Utc < PendingWindow;

    /// <summary>
    /// Lists the ids that are pending relinks now (<see cref="IsPendingRelink"/>), with the line that made each one, for
    /// Diagnostics. Whether the library already has an id is the caller's check.
    /// </summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>The lines, in <c>Seq</c> order.</returns>
    public IReadOnlyList<MoveJournalLine> PendingRelinks(DateTime nowUtc)
        => _lastByNewId
            .Where(e => nowUtc - Lines[e.Value].Utc < PendingWindow && FirstAfter(e.Key, Lines[e.Value].Seq) is null)
            .Select(e => Lines[e.Value])
            .OrderBy(l => l.Seq)
            .ToList();

    /// <summary>
    /// Parses the journal file's bytes.
    /// </summary>
    /// <param name="utf8">The file's content, UTF-8, with or without a byte order mark.</param>
    /// <returns>The journal.</returns>
    internal static MoveJournal Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8 is [0xEF, 0xBB, 0xBF, ..])
        {
            utf8 = utf8[3..];
        }

        var segments = SplitLines(utf8);
        var index = 0;

        // The header: the first line that isn't blank.
        while (index < segments.Count && IsBlank(utf8.Slice(segments[index].Start, segments[index].Length)))
        {
            index++;
        }

        if (index == segments.Count)
        {
            return new MoveJournal(MoveJournalStatus.Empty);
        }

        var headerSegment = segments[index];
        var headerBytes = utf8.Slice(headerSegment.Start, headerSegment.Length);
        index++;
        JsonElement header;
        try
        {
            using var document = JsonDocument.Parse(headerBytes.ToArray());
            header = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return headerSegment.Terminated
                ? new MoveJournal(MoveJournalStatus.InvalidHeader, problem: "The move journal's first line isn't JSON.")
                : new MoveJournal(MoveJournalStatus.Empty, truncatedTail: true);
        }

        if (header.ValueKind != JsonValueKind.Object || !TryGetInt(header, "V", out var headerVersion))
        {
            return new MoveJournal(MoveJournalStatus.InvalidHeader, problem: "The move journal's first line is not a header with a format version.");
        }

        if (headerVersion != FormatVersion)
        {
            return new MoveJournal(
                MoveJournalStatus.UnsupportedVersion,
                problem: string.Create(CultureInfo.InvariantCulture, $"The move journal has format version {headerVersion}; this version reads version {FormatVersion} only."));
        }

        if (!header.TryGetProperty("Header", out var isHeader) || isHeader.ValueKind != JsonValueKind.True
            || !TryGetGuid(header, "JournalId", out var journalId))
        {
            return new MoveJournal(MoveJournalStatus.InvalidHeader, problem: "The move journal's first line is not a header with a JournalId.");
        }

        DateTime? createdUtc = TryGetUtc(header, "CreatedUtc", out var created) ? created : null;

        var lines = new List<MoveJournalLine>();
        long lastSeq = 0;
        var ignored = 0;
        var invalid = 0;
        var truncatedTail = false;
        for (; index < segments.Count; index++)
        {
            var segment = segments[index];
            var bytes = utf8.Slice(segment.Start, segment.Length);
            if (IsBlank(bytes))
            {
                continue;
            }

            JsonElement line;
            try
            {
                using var document = JsonDocument.Parse(bytes.ToArray());
                line = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                if (!segment.Terminated)
                {
                    truncatedTail = true;
                }
                else
                {
                    invalid++;
                }

                continue;
            }

            switch (ReadLine(line, journalId, ref lastSeq, out var move))
            {
                case LineKind.Move:
                    lines.Add(move!);
                    break;
                case LineKind.Ignored:
                    ignored++;
                    break;
                default:
                    invalid++;
                    break;
            }
        }

        var problem = invalid == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{invalid} line(s) of the move journal are invalid and were skipped.");
        return new MoveJournal(MoveJournalStatus.Ok, journalId, createdUtc, lines, lastSeq, ignored, invalid, truncatedTail, problem);
    }

    /// <summary>
    /// Creates the journal of a file that couldn't be read.
    /// </summary>
    /// <param name="problem">What went wrong.</param>
    /// <returns>The journal.</returns>
    internal static MoveJournal Unreadable(string problem) => new(MoveJournalStatus.Unreadable, problem: problem);

    private static LineKind ReadLine(JsonElement line, Guid journalId, ref long lastSeq, out MoveJournalLine? move)
    {
        move = null;
        if (line.ValueKind != JsonValueKind.Object)
        {
            return LineKind.Invalid;
        }

        if (!TryGetInt(line, "V", out var version))
        {
            return LineKind.Invalid;
        }

        if (version != FormatVersion)
        {
            return LineKind.Ignored;
        }

        if (line.TryGetProperty("Header", out _)
            || !TryGetGuid(line, "JournalId", out var lineJournal) || lineJournal != journalId
            || !line.TryGetProperty("Seq", out var seqElement) || seqElement.ValueKind != JsonValueKind.Number
            || !seqElement.TryGetInt64(out var seq) || seq <= lastSeq)
        {
            return LineKind.Invalid;
        }

        // The line is in order: later lines are checked against it, and it counts towards LastSeq whatever it holds.
        lastSeq = seq;

        if (!line.TryGetProperty(StatusProperty, out var status) || status.ValueKind != JsonValueKind.String)
        {
            return LineKind.Invalid;
        }

        if (!status.ValueEquals(CommittedStatus))
        {
            return LineKind.Ignored;
        }

        if (!TryGetUtc(line, "Utc", out var utc) || !TryGetGuid(line, "OldId", out var oldId) || !TryGetGuid(line, "NewId", out var newId))
        {
            return LineKind.Invalid;
        }

        move = new MoveJournalLine(
            seq,
            utc,
            oldId,
            newId,
            GetString(line, "Kind"),
            GetString(line, "OldPath"),
            GetString(line, "NewPath"),
            TryGetGuid(line, "JobId", out var jobId) ? jobId : null,
            GetString(line, "Role"),
            line.TryGetProperty("Correction", out var correction) && correction.ValueKind == JsonValueKind.True);
        return LineKind.Move;
    }

    private MoveJournalLine? FirstAfter(Guid oldId, long seq, DateTime? afterUtc = null)
    {
        if (!_byOldId.TryGetValue(oldId, out var indexes))
        {
            return null;
        }

        // The indexes are in Seq order: find the first line after seq.
        int low = 0, high = indexes.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Lines[indexes[middle]].Seq <= seq)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        // Then the first of those committed after afterUtc. Times are compared line by line rather than searched, so a line
        // whose time is out of order can't hide a later one.
        for (; low < indexes.Count; low++)
        {
            var line = Lines[indexes[low]];
            if (afterUtc is not { } since || line.Utc > since)
            {
                return line;
            }
        }

        return null;
    }

    private static List<(int Start, int Length, bool Terminated)> SplitLines(ReadOnlySpan<byte> utf8)
    {
        var segments = new List<(int Start, int Length, bool Terminated)>();
        var start = 0;
        while (start < utf8.Length)
        {
            var end = utf8[start..].IndexOf((byte)'\n');
            if (end < 0)
            {
                segments.Add((start, utf8.Length - start, false));
                break;
            }

            var length = end > 0 && utf8[start + end - 1] == (byte)'\r' ? end - 1 : end;
            segments.Add((start, length, true));
            start += end + 1;
        }

        return segments;
    }

    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryGetGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value)
            && value != Guid.Empty;
    }

    private static bool TryGetUtc(JsonElement element, string name, out DateTime value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        value = parsed.UtcDateTime;
        return true;
    }

    // A blank value (empty or white space) reads as absent: a blank NewPath must never reach FindByPath, which on 10.11.5
    // puts no path filter on its query for one and answers with the newest item of the whole library.
    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
