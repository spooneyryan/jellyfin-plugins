using System;
using System.IO;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Reads Library Maintenance's move journal, <c>&lt;data&gt;/librarymaintenance/moves.jsonl</c>, for
/// the consumers that follow moved items: Bookmarks, User Ratings, Smart Search and Duplicates.
/// </summary>
/// <remarks>
/// <para>The file is opened with <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/>, so Library
/// Maintenance keeps appending while it is read, and is never written. A read that finds the file unchanged (length and
/// time stamps) returns the journal it parsed before. A file that can't be read gives
/// <see cref="MoveJournalStatus.Unreadable"/> and is tried again on the next read; one Warning is logged until it can be
/// read again, and one per new problem the file has (invalid lines, a bad header, another format version). Never
/// throws for the file's sake.</para>
/// <para>Consumers read at startup and when Library Maintenance's hook fires: <see cref="CatchUp(Guid?, long)"/> gives the
/// lines after their stored position and the position to store, and <see cref="IdentityResolver"/> follows the journal
/// through <see cref="Read"/>.</para>
/// </remarks>
internal sealed class MoveJournalReader
{
    /// <summary>Library Maintenance's data folder, under the server's data path.</summary>
    public const string LibraryMaintenanceFolder = "librarymaintenance";

    /// <summary>The journal's file name.</summary>
    public const string FileName = "moves.jsonl";

    /// <summary>The largest journal read (about 800,000 moves at Library Maintenance's 330 bytes a line).</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private Cached? _cached;
    private string? _lastLoggedProblem;
    private bool _unreadableLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoveJournalReader"/> class for the server's journal,
    /// <c>&lt;IApplicationPaths.DataPath&gt;/librarymaintenance/moves.jsonl</c>.
    /// </summary>
    /// <param name="applicationPaths">The server's paths.</param>
    /// <param name="logger">The logger (category <c>Jellyfin.Plugin.&lt;X&gt;.Kit.MoveJournal</c>).</param>
    public MoveJournalReader(IApplicationPaths applicationPaths, ILogger logger)
        : this(Path.Combine(ArgumentNullOrDataPath(applicationPaths), LibraryMaintenanceFolder, FileName), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MoveJournalReader"/> class for a given file.
    /// </summary>
    /// <param name="file">The journal file.</param>
    /// <param name="logger">The logger.</param>
    public MoveJournalReader(string file, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(logger);

        FilePath = Path.GetFullPath(file);
        _logger = logger;
    }

    /// <summary>
    /// Gets the journal file's full path.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Reads the journal, or returns the one read before when the file hasn't changed.
    /// </summary>
    /// <returns>The journal; <see cref="MoveJournal.Missing"/> when there is no file.</returns>
    public MoveJournal Read()
    {
        lock (_lock)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(FilePath);
                if (!info.Exists)
                {
                    _cached = null;
                    _unreadableLogged = false;
                    return MoveJournal.Missing;
                }

                var key = (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
                if (_cached is { } cached && cached.Key == key)
                {
                    return cached.Journal;
                }

                if (info.Length > MaxBytes)
                {
                    return Unreadable($"The move journal {FilePath} is larger than {MaxBytes / (1024 * 1024)} MB; it is not read.", null);
                }

                var journal = MoveJournal.Parse(ReadAllBytes());
                if (_unreadableLogged)
                {
                    _unreadableLogged = false;
                    _logger.LogInformation("The move journal {File} could be read again", FilePath);
                }

                LogProblem(journal);

                // The key is the one seen before reading: if the file grew meanwhile, the next read sees a new key.
                _cached = new Cached(key, journal);
                return journal;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return Unreadable($"The move journal {FilePath} couldn't be read: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Works out what a consumer at <c>(<paramref name="storedJournalId"/>, <paramref name="storedLastMoveSeq"/>)</c> has
    /// to apply. A journal whose <c>JournalId</c> differs from a stored one, or whose last <c>Seq</c> is below
    /// the stored position, is replayed from the start (<see cref="MoveJournalCatchUp.Reset"/>); replaying is idempotent
    /// because a line only relinks records still on its <c>OldId</c>. A consumer without a stored journal id starts from
    /// the beginning. When the journal isn't <see cref="MoveJournalStatus.Ok"/> there is nothing to apply and the stored
    /// position is kept.
    /// </summary>
    /// <param name="storedJournalId">The consumer's stored <c>MoveJournalId</c>; null before its first catch-up.</param>
    /// <param name="storedLastMoveSeq">The consumer's stored <c>LastMoveSeq</c>.</param>
    /// <returns>The lines to apply and the position to store afterwards.</returns>
    public MoveJournalCatchUp CatchUp(Guid? storedJournalId, long storedLastMoveSeq)
        => CatchUp(Read(), storedJournalId, storedLastMoveSeq);

    /// <summary>
    /// Works out what a consumer has to apply from a journal already read (see <see cref="CatchUp(Guid?, long)"/>).
    /// </summary>
    /// <param name="journal">The journal.</param>
    /// <param name="storedJournalId">The consumer's stored <c>MoveJournalId</c>.</param>
    /// <param name="storedLastMoveSeq">The consumer's stored <c>LastMoveSeq</c>.</param>
    /// <returns>The lines to apply and the position to store afterwards.</returns>
    public static MoveJournalCatchUp CatchUp(MoveJournal journal, Guid? storedJournalId, long storedLastMoveSeq)
    {
        ArgumentNullException.ThrowIfNull(journal);

        if (journal.Status != MoveJournalStatus.Ok || journal.JournalId is not { } journalId)
        {
            return new MoveJournalCatchUp(journal, [], storedJournalId, storedLastMoveSeq, MoveJournalReset.None);
        }

        var reset = MoveJournalReset.None;
        var after = storedLastMoveSeq;
        if (storedJournalId is null)
        {
            after = 0;
        }
        else if (storedJournalId != journalId)
        {
            reset = MoveJournalReset.NewJournalId;
            after = 0;
        }
        else if (journal.LastSeq < storedLastMoveSeq)
        {
            reset = MoveJournalReset.SeqBelowStored;
            after = 0;
        }

        return new MoveJournalCatchUp(journal, journal.After(after), journalId, Math.Max(after, journal.LastSeq), reset);
    }

    private static string ArgumentNullOrDataPath(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return applicationPaths.DataPath;
    }

    private byte[] ReadAllBytes()
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private MoveJournal Unreadable(string problem, Exception? exception)
    {
        _cached = null;
        if (!_unreadableLogged)
        {
            _unreadableLogged = true;
            _logger.LogWarning(exception, "{Problem} Moved items are not followed from the journal until it can be read", problem);
        }
        else
        {
            _logger.LogDebug("{Problem}", problem);
        }

        return MoveJournal.Unreadable(problem);
    }

    private void LogProblem(MoveJournal journal)
    {
        if (journal.Problem is { } problem && !string.Equals(problem, _lastLoggedProblem, StringComparison.Ordinal))
        {
            _logger.LogWarning("{Problem} ({File})", problem, FilePath);
        }

        _lastLoggedProblem = journal.Problem;
    }

    private sealed record Cached((long Length, long Written, long Created) Key, MoveJournal Journal);
}
