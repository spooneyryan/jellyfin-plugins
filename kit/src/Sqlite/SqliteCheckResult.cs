using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// The outcome of a <c>PRAGMA quick_check</c> or <c>PRAGMA integrity_check</c>.
/// </summary>
/// <remarks>
/// A check that can't read a page doesn't throw: SQLite answers with a line such as "Tree 5 page 5: unable to get the
/// page. error code=266" (<c>SQLITE_IOERR_READ</c>, for example a byte-range lock another program holds on Windows) and
/// lists every page below it as "never used". Such a result says nothing about the data, so <see cref="IoError"/> names
/// the line and <see cref="IsInconclusive"/> is <c>true</c>: the database treats it as "can't be checked now" and tries
/// again, never as damage.
/// </remarks>
/// <param name="Kind"><c>quick_check</c> or <c>integrity_check</c>.</param>
/// <param name="Utc">When it ran.</param>
/// <param name="Ok">Whether SQLite answered exactly <c>ok</c>.</param>
/// <param name="Messages">What SQLite answered (at most the first 20 lines), or the error that stopped the check.</param>
internal sealed partial record SqliteCheckResult(string Kind, DateTime Utc, bool Ok, IReadOnlyList<string> Messages)
{
    /// <summary>The kind of the check on open and at the start of maintenance.</summary>
    public const string QuickCheck = "quick_check";

    /// <summary>The kind of the check on every new snapshot, on a snapshot before it is restored, and the second check at open.</summary>
    public const string IntegrityCheck = "integrity_check";

    // SQLite's primary result codes that mean damage; any other code in a check's line is a failure to read.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// Gets SQLite's first line, which names the first problem, or <c>null</c>.
    /// </summary>
    public string? FirstMessage => Messages.Count > 0 ? Messages[0] : null;

    /// <summary>
    /// Gets the first line of a failed check that reports a failure to read rather than damage, or <c>null</c>: "unable
    /// to get the page. error code=N" or an error line (<c>SQLite error N</c>, the database's own wording) whose primary
    /// code isn't <c>SQLITE_CORRUPT</c> (11) or <c>SQLITE_NOTADB</c> (26), "Failed to read ptrmap" and "failed to get
    /// page".
    /// </summary>
    public string? IoError => Ok ? null : FindIoError(Messages);

    /// <summary>
    /// Gets a value indicating whether the check failed only because part of the file couldn't be read
    /// (<see cref="IoError"/>): it says nothing about the data and is tried again.
    /// </summary>
    public bool IsInconclusive => IoError is not null;

    /// <summary>
    /// Gets a value indicating whether the check found damage: it failed, and no line reports a failure to read.
    /// </summary>
    public bool IsDamaged => !Ok && !IsInconclusive;

    /// <summary>
    /// Gets the problems in one line for a log line or a banner: SQLite's lines without the "*** in database main ***"
    /// heading, the first two, and how many more there are.
    /// </summary>
    public string Summary
    {
        get
        {
            var lines = Lines(Messages).ToList();
            if (lines.Count == 0)
            {
                return Ok ? "ok" : "no details";
            }

            var summary = string.Join("; ", lines.Take(2));
            return lines.Count > 2 ? string.Create(CultureInfo.InvariantCulture, $"{summary} (and {lines.Count - 2} more)") : summary;
        }
    }

    /// <summary>
    /// Finds the first line that reports a failure to read (see <see cref="IoError"/>).
    /// </summary>
    /// <param name="messages">A check's rows, each of one or more lines.</param>
    /// <returns>The line, or <c>null</c>.</returns>
    public static string? FindIoError(IEnumerable<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var line in Lines(messages))
        {
            var code = ErrorCodePattern().Match(line);
            if (code.Success)
            {
                if (int.TryParse(code.Groups["code"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    && (value & 0xFF) is SqliteCorrupt or SqliteNotADatabase)
                {
                    continue;
                }

                return line;
            }

            if (line.Contains("Failed to read ptrmap", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed to get page", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// Describes the check with plain values, for Diagnostics.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Kind"] = Kind,
        ["Utc"] = Utc,
        ["Ok"] = Ok,
        ["IoError"] = IoError,
        ["Messages"] = Messages
    };

    private static IEnumerable<string> Lines(IEnumerable<string> messages)
        => messages
            .SelectMany(m => m.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(l => !l.StartsWith("*** in database", StringComparison.Ordinal));

    // "unable to get the page. error code=266" (btreeGetPage failed) and the database's own "SQLite error 10 (266): ..."
    // for an error that stopped the check. "btreeInitPage() returns error code 11" is always damage and isn't matched.
    [GeneratedRegex(@"(?:unable to get the page\. error code=|^SQLite error )(?<code>\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ErrorCodePattern();
}
