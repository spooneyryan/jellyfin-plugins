using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The file part of a clip's rename (Rename and Rename file, since 1.2.0; <c>ClipFileRenamer</c> renames through
/// <c>ClipFileOps</c>), and the journal that makes it safe across a crash.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> As every request's file operation: the file first, then the record, each attempt inside the
/// store's write of the record after the record is checked again, the 200 ms pauses outside the store's lock. The move is
/// one rename within the clip's own folder (<c>MoveFileEx</c> without replacing: atomic on one volume, never over a file),
/// so at every moment the file is at exactly one of its two paths.</para>
/// <para><b>The journal.</b> Just before the move, <see cref="RenameOnce"/> records the rename in
/// <see cref="ClipRenameJournal"/> (flushed to the disk, its move into place too); right after the move the clip file is
/// flushed (<see cref="IClipFileSystem.FlushFile"/>: on NTFS the clip volume's log, so the rename is on the disk before the
/// record naming it, on the data folder's volume, is written). Once the record naming the new path is
/// written, <see cref="FinishRename"/> drops the entry and notes the rename in <see cref="ClipRenameHistory"/>. A crash
/// between the move and the record's write, or a record's write that fails while the move back fails too
/// (<see cref="UndoRenameAsync"/>), leaves the entry: <see cref="FinishRenamesAsync"/> then makes the record name the path
/// the file is at (at the next start, before the clip jobs resume; while one waits, after a minute and less often while nothing changes,
/// <see cref="RetryWaitingRenamesAsync"/>; daily before the missing-clip sweep; before the next rename, delete or remake of
/// that clip). Meanwhile <see cref="Check"/> holds both paths, so nothing deletes, forgets or moves the clip, the My Clips
/// list shows it as a rename not finished, and a download is served from where the file is. A clip whose entry can't be read
/// is held by its id until an admin removes the entry.</para>
/// <para><b>Names.</b> <see cref="ChooseRenameTarget"/> takes a name no file, folder or clip being written (its
/// <c>.partial</c>) has, that no record of any user names (a record whose file is missing still owns its path, or two records
/// would share one file), no file waiting to be deleted names and no rename holds, compared as clip paths are (without regard
/// to case on Windows); the clip's own path is free for it, so a change of case alone is a rename too. The clip worker's
/// planner takes the same rule (<see cref="NameTaken"/>). <see cref="NameGate"/> makes that choice and the
/// worker's choice of a new clip's name one at a time.</para>
/// <para><b>A restore.</b> A snapshot or an import from before a rename names the clip's old path: the renames' history lets
/// <see cref="RelinkRenamed"/> name the renamed file again, never a file another record names.</para>
/// </remarks>
internal sealed partial class ClipFileOps
{
    /// <summary>
    /// How long the server waits before it tries the renames that wait again (a clip folder that can't be reached, data that
    /// couldn't be written).
    /// </summary>
    public static readonly TimeSpan RenameRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest pause between two tries of the renames that wait while nothing about them changes (the pause
    /// doubles from <see cref="RenameRetryInterval"/> up to this one, and is back to it once something
    /// changes).
    /// </summary>
    public static readonly TimeSpan RenameRetryMaxInterval = TimeSpan.FromMinutes(16);

    /// <summary>
    /// The longest a clip folder may take to answer whether it is there before a pass over the renames that wait counts it as
    /// unreachable for that pass (a slow share never holds the store's lock).
    /// </summary>
    public static readonly TimeSpan DefaultRootProbeSlowThreshold = TimeSpan.FromSeconds(2);

    private readonly object _journalLock = new();
    private readonly HashSet<Guid> _renaming = [];

    // The renames whose move couldn't be flushed, by record, with the very entry (a recovery, a finish
    // or an undo of an older entry of a clip never clears the mark of a newer rename of it).
    private readonly Dictionary<Guid, ClipRenameEntry> _unflushed = [];
    private ClipRenameJournal? _journal;
    private ClipRenameHistory? _history;
    private bool _journalLooked;

    // What a rename that wasn't finished turned out to be (FinishRenamesAsync, ClipRenameRecovery).
    private enum RenameOutcome
    {
        Waiting,
        Written,
        Completed,
        NotMoved,
        MovedOn,
        Lost
    }

    /// <summary>
    /// Gets the gate of new clip file names: the clip worker plans and reserves a new clip's name (its <c>.partial</c>), and a
    /// rename chooses and takes a new name, one at a time, so a name one of them found free can't be taken by the other before
    /// it is used. Taken before the store's lock, never inside it.
    /// </summary>
    public SemaphoreSlim NameGate { get; } = new(1, 1);

    /// <summary>
    /// Gets or sets the longest a clip folder may take to answer whether it is there in a pass over the renames that wait
    /// (<see cref="DefaultRootProbeSlowThreshold"/>; tests set a shorter one).
    /// </summary>
    internal TimeSpan RootProbeSlowThreshold { get; set; } = DefaultRootProbeSlowThreshold;

    /// <summary>
    /// Gets the journal of clip file renames, in the plugin's data folder (the folder of <c>bookmarks.json</c>); <c>null</c>
    /// when the store has no file (a test's mock), and then no rename of a file is made.
    /// </summary>
    internal ClipRenameJournal? Journal
    {
        get
        {
            Look();
            return _journal;
        }
    }

    /// <summary>
    /// Gets the history of the renames finished, next to the journal; <c>null</c> as <see cref="Journal"/>.
    /// </summary>
    internal ClipRenameHistory? History
    {
        get
        {
            Look();
            return _history;
        }
    }

    /// <summary>
    /// Gets how many renames wait to be finished now (not under way in this process, and not written already), those whose
    /// entry can't be read left out (nothing can finish them).
    /// </summary>
    public int WaitingRenames
    {
        get
        {
            if (Journal is not { } journal)
            {
                return 0;
            }

            var entries = journal.Entries;
            lock (_renaming)
            {
                return entries.Count(e => !_renaming.Contains(e.BookmarkId));
            }
        }
    }

    /// <summary>
    /// Whether a rename of a record's clip file wasn't finished (its record not written after the move, or its entry left by a
    /// process that stopped during it, or an entry that can't be read), not counting one under way in this process just now,
    /// nor one whose record names its new path already.
    /// </summary>
    /// <param name="bookmarkId">The record's id.</param>
    /// <returns>Whether one waits to be finished.</returns>
    public bool HasUnfinishedRename(Guid bookmarkId)
    {
        if (Journal is not { } journal)
        {
            return false;
        }

        if (journal.IsUnreadable(bookmarkId))
        {
            return true;
        }

        if (journal.Get(bookmarkId) is not { } entry)
        {
            return false;
        }

        lock (_renaming)
        {
            if (_renaming.Contains(bookmarkId))
            {
                return false;
            }
        }

        return !IsWritten(entry);
    }

    /// <summary>
    /// Forgets the My Clips list's kept verdict of a path (a request found it changed: a file gone that the list showed there).
    /// </summary>
    /// <param name="path">The path.</param>
    public void ForgetVerdict(string? path)
    {
        if (ClipFileGuard.TryGetFullPath(path, out var full))
        {
            _states?.Forget(full);
        }
    }

    /// <summary>
    /// Gets the rule of taken clip file names, read once for one choice (a rename's and a new clip's alike): a path is
    /// taken by anything at it (a file or folder), a clip being written under it (its <c>.partial</c>), any record's path
    /// of any user (also one whose file is gone), a file waiting to be deleted, or a path a rename holds. Since 1.2.0 a
    /// path the file system can't answer about (an I/O error other than "not found", or no access: a drive that isn't
    /// ready, a share that doesn't answer) ends the choice at once with <see cref="ClipFolderUnreachableException"/>, where
    /// before it counted as taken and the choice asked about up to a thousand numbered names, each as slow as the folder.
    /// </summary>
    /// <remarks>Since 1.2.0 a look-up the file system can't answer is asked once more after
    /// <see cref="RetryDelay"/> before the choice ends, where the caller may pause (<paramref name="retryOnce"/>: the clip
    /// planner, on a request's thread or under <see cref="NameGate"/>, never inside the store's lock); a rename chooses inside
    /// the store's write and tries its whole attempt again outside the lock instead (<c>ClipFileRenamer</c>).</remarks>
    /// <param name="own">A path that is free for the caller: the clip's own, for its rename.</param>
    /// <param name="retryOnce">Whether the first look-up the file system can't answer is asked once more after a pause.</param>
    /// <returns>Whether a candidate path is taken; it throws <see cref="ClipFolderUnreachableException"/> when the file system
    /// can't answer.</returns>
    public Func<string, bool> NameTaken(string? own = null, bool retryOnce = false)
    {
        var taken = new HashSet<string>(ClipRootPaths.Comparer);
        foreach (var record in _store.GetAll())
        {
            if (ClipFileGuard.TryGetFullPath(record.Clip?.FilePath, out var full))
            {
                taken.Add(full);
            }
        }

        foreach (var pending in _store.PendingFileDeletes)
        {
            if (ClipFileGuard.TryGetFullPath(pending?.Path, out var full))
            {
                taken.Add(full);
            }
        }

        // One pause per choice, not per name: the folder had its second chance.
        var pauseLeft = retryOnce;
        bool PauseOnce()
        {
            if (!pauseLeft)
            {
                return false;
            }

            pauseLeft = false;
            Thread.Sleep(RetryDelay);
            return true;
        }

        return candidate => (own is null || !ClipRootPaths.Comparer.Equals(candidate, own)) && IsTaken(candidate, taken, PauseOnce);
    }

    /// <summary>
    /// Tells whether a remake of a clip may write its new file under the clip's own path (since Bookmarks 1.2.1: a remake
    /// keeps its file's name, where it used to get a fresh one, " (2)" and on): the path is the record's own, no other record
    /// names it, no file waiting to be deleted is at it, no rename holds it, and no clip is being written under it (its
    /// <c>.partial</c>). The worker then writes the clip to the <c>.partial</c> and replaces the file with it at the end
    /// (<see cref="ReplaceAsync"/>), or, when it can't (the file in use), puts it under a fresh name after all.
    /// </summary>
    /// <param name="bookmarkId">The record.</param>
    /// <param name="path">Its clip's path.</param>
    /// <param name="whileWriting">Whether the remake's own <c>.partial</c> is there (the worker's look before it replaces
    /// the file), so it doesn't count.</param>
    /// <returns>The full path when it may be reused; else <c>null</c>.</returns>
    public string? ReusableOwnPath(Guid bookmarkId, string? path, bool whileWriting = false)
    {
        if (!ClipFileGuard.TryGetFullPath(path, out var full))
        {
            return null;
        }

        foreach (var record in _store.GetAll())
        {
            if (!record.Id.Equals(bookmarkId) && ClipFileGuard.TryGetFullPath(record.Clip?.FilePath, out var other) && ClipRootPaths.Comparer.Equals(other, full))
            {
                return null;
            }
        }

        if (_store.PendingFileDeletes.Any(p => ClipFileGuard.TryGetFullPath(p?.Path, out var pending) && ClipRootPaths.Comparer.Equals(pending, full))
            || Journal?.Holding(full) is not null
            || IsHeld(full))
        {
            return null;
        }

        if (whileWriting)
        {
            return full;
        }

        try
        {
            return _fileSystem.GetAttributes(full + ClipFileGuard.PartialExtension) is null ? full : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look for a clip being written under the clip file of bookmark {BookmarkId}", bookmarkId);
            return null;
        }
    }

    /// <summary>
    /// Chooses a clip file's new path in its own folder for a file name (within the same folder,
    /// with <c>MakeUnique</c>): the name itself when it is free, else with <c> (2)</c>, <c> (3)</c>… (see the class
    /// remarks for what makes a name taken). Called under the store's lock, so the records and the files waiting to be deleted
    /// it reads are the ones the write sees, while <see cref="NameGate"/> is held.
    /// </summary>
    /// <param name="current">The clip file's full path now.</param>
    /// <param name="fileName">The file name it should have.</param>
    /// <returns>The new full path: the same as <paramref name="current"/> (ordinally) when the name is its own already, or when
    /// only a numbered name it has already is free (nothing to rename); one that differs from it only in case for a change of
    /// case.</returns>
    /// <exception cref="PluginException">503 <see cref="FolderUnreachableMessage"/> when the clip folder can't answer a
    /// look-up; nothing was moved.</exception>
    public string ChooseRenameTarget(string current, string fileName)
    {
        if (TryChooseRenameTarget(current, fileName, out var target, out var error))
        {
            return target;
        }

        WarnRenameTargetUnreachable(error);
        throw PluginException.Unavailable(FolderUnreachableMessage);
    }

    /// <summary>
    /// Chooses a clip file's new path as <see cref="ChooseRenameTarget"/> does, without throwing when the clip folder can't
    /// answer a look-up: the rename's attempt, inside the store's write, then ends with nothing moved,
    /// and the renamer tries it once more after a pause outside the store's lock.
    /// </summary>
    /// <param name="current">The clip file's full path now.</param>
    /// <param name="fileName">The file name it should have.</param>
    /// <param name="target">The new full path, as <see cref="ChooseRenameTarget"/> gives it (the current one when none was
    /// chosen).</param>
    /// <param name="error">Why the clip folder couldn't answer, when it couldn't.</param>
    /// <returns>Whether a path was chosen.</returns>
    public bool TryChooseRenameTarget(string current, string fileName, out string target, out Exception? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(current);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        var folder = Path.GetDirectoryName(current) ?? throw new ArgumentException("A clip file's path has a folder.", nameof(current));

        // The clip's own path is free for it: a file numbered " (2)" already, whose plain name is taken, keeps its number (not
        // " (3)"); one that differs only in case is renamed to the new case.
        try
        {
            target = ClipFileNaming.MakeUnique(folder, fileName, NameTaken(current));
            error = null;
            return true;
        }
        catch (ClipFolderUnreachableException ex)
        {
            _logger.LogDebug(ex.InnerException ?? ex, "Could not look at a clip file name in {Folder}", folder);
            target = current;
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// Says that a rename's new name couldn't be chosen because its clip folder can't be reached (a Warning without a path).
    /// </summary>
    /// <param name="error">The look-up's error, from <see cref="TryChooseRenameTarget"/>.</param>
    public void WarnRenameTargetUnreachable(Exception? error)
        => _logger.LogWarning("Could not choose a new name for a clip file: its clip folder can't be reached ({Error})", error?.InnerException?.GetType().Name ?? error?.GetType().Name ?? nameof(IOException));

    /// <summary>
    /// Makes one attempt at renaming a clip file within its folder for a rename a user asked for, synchronously,
    /// inside the store's write of its record: the source must be a clip file that is there, the destination a free clip
    /// path in the same folder (or the source's own path in another case); the rename is recorded in the journal, flushed,
    /// then the file is moved without replacing anything and the move flushed. A move that fails is taken back out of the
    /// journal (the file is where it was); one that throws although the file did move (a share's lost answer: the old path
    /// empty and a file at the new one, which was free) counts as done, with a Warning when the file there can't be told to be
    /// the clip's. A failure is logged on the last attempt, or when it isn't worth another.
    /// </summary>
    /// <param name="entry">The rename: the record, both full paths (as <see cref="Check"/> and <see cref="ChooseRenameTarget"/>
    /// gave them), the name change and the record's <c>UpdatedAt</c>.</param>
    /// <param name="attempt">Which attempt this is, from 1 to <see cref="Attempts"/>.</param>
    /// <param name="recorded">The entry as the journal holds it (with the file's identity), when the rename is made: what
    /// <see cref="FinishRename"/> or <see cref="UndoRenameAsync"/> takes.</param>
    /// <returns><see cref="ClipFileOutcome.Done"/> (call <see cref="FinishRename"/> once the record is written, or
    /// <see cref="UndoRenameAsync"/> when its write fails); otherwise nothing changed: the source
    /// <see cref="ClipFileOutcome.Missing"/>, <see cref="ClipFileOutcome.InUse"/>, <see cref="ClipFileOutcome.Refused"/>, or
    /// <see cref="ClipFileOutcome.Failed"/> (<see cref="ClipFileResult.NameTaken"/> when the destination was taken just
    /// then).</returns>
    public ClipFileResult RenameOnce(ClipRenameEntry entry, int attempt, out ClipRenameEntry? recorded)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        recorded = null;
        var final = attempt >= Attempts;
        var from = Check(entry.From);
        if (from.Verdict == ClipPathVerdict.Missing)
        {
            _states?.Forget(from.FullPath);
            return new ClipFileResult(ClipFileOutcome.Missing, from, Error: "The clip file isn't there.");
        }

        if (Refuse(from, "rename", entry.BookmarkId, entry.From) is { } refusedFrom)
        {
            return refusedFrom;
        }

        var source = from.FullPath!;
        if (!ClipFileGuard.TryGetFullPath(entry.To, out var wanted)
            || !string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(wanted), ClipRootPaths.Comparison))
        {
            throw new ArgumentException("A clip file is renamed within its own folder only.", nameof(entry));
        }

        var sameFile = ClipRootPaths.Comparer.Equals(source, wanted);
        var to = Check(wanted);

        if (!sameFile && to.Verdict == ClipPathVerdict.Exists)
        {
            _logger.LogDebug("The new name of the clip file of bookmark {BookmarkId} was taken just now: {Path}", entry.BookmarkId, entry.To);
            return new ClipFileResult(ClipFileOutcome.Failed, to, attempt, "A file with the clip's new name exists already.", NameTaken: true);
        }

        if (!sameFile && to.Verdict != ClipPathVerdict.Missing && Refuse(to, "rename", entry.BookmarkId, entry.To) is { } refusedTo)
        {
            return refusedTo;
        }

        var journal = Journal;
        if (journal is null)
        {
            return Fail(new ClipFileResult(ClipFileOutcome.Failed, from, Error: "The rename can't be recorded (no data folder)."), "rename", entry.BookmarkId, null, entry.From);
        }

        // In the case-only rename the destination is the source's own path in its new case (the guard's full path keeps the
        // case it was given).
        var destination = sameFile ? wanted : to.FullPath!;
        var identity = _fileSystem.GetFileIdentity(source);
        var candidate = entry with { From = source, To = destination, File = identity };
        lock (_renaming)
        {
            // An earlier rename of this clip whose move wasn't flushed is replaced by this one's entry.
            _renaming.Add(entry.BookmarkId);
            _unflushed.Remove(entry.BookmarkId);
        }

        // Whatever happens below that isn't a rename made, the clip isn't "under way" any more: an entry left (a stop, an
        // error nobody expected) is the recovery's, from what is on disk.
        var made = false;
        try
        {
            try
            {
                journal.Add(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(new ClipFileResult(ClipFileOutcome.Failed, from, attempt, "The rename couldn't be recorded in the plugin's data folder.", HResult: ex.HResult), "rename", entry.BookmarkId, ex, entry.From);
            }

            try
            {
                _fileSystem.MoveFile(source, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!MovedAnyway(candidate, sameFile, ex))
                {
                    journal.Remove(candidate);
                    return MoveFailed(candidate, from, to, attempt, final, ex);
                }
            }

            // On the disk before the record names it. A flush the file system refuses leaves the entry after
            // the record's write, for the next pass to drop once the file is seen at its new path.
            if (!_fileSystem.FlushFile(destination))
            {
                _logger.LogDebug("The rename of the clip file of bookmark {BookmarkId} couldn't be flushed to the disk; its journal entry stays until the next pass", entry.BookmarkId);
                lock (_renaming)
                {
                    _unflushed[entry.BookmarkId] = candidate;
                }
            }

            made = true;
            recorded = candidate;
            return new ClipFileResult(ClipFileOutcome.Done, from, attempt);
        }
        finally
        {
            if (!made)
            {
                EndRenaming(entry.BookmarkId);
            }
        }
    }

    /// <summary>
    /// Ends a rename whose record is written (<see cref="RenameOnce"/> was <see cref="ClipFileOutcome.Done"/>): the rename goes
    /// into the history, its journal entry goes (unless its move couldn't be flushed: then the next pass drops it), and the My
    /// Clips list judges both paths again at once.
    /// </summary>
    /// <param name="entry">The rename, as <see cref="RenameOnce"/> recorded it.</param>
    public void FinishRename(ClipRenameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        NoteFinished(entry, entry.To);
        bool unflushed;
        lock (_renaming)
        {
            unflushed = ClearUnflushed(entry);
        }

        if (!unflushed)
        {
            Journal?.Remove(entry);
        }

        EndRenaming(entry.BookmarkId);
        _states?.Forget(entry.From);
        _states?.Forget(entry.To);
    }

    /// <summary>
    /// Puts a renamed clip file back where its record says it is, after the record's write failed (the store couldn't write
    /// <c>bookmarks.json</c>): up to <see cref="Attempts"/> moves back <see cref="RetryDelay"/> apart, never over a file, each
    /// flushed. Put back, the rename's entry goes and nothing changed. Otherwise the entry stays and holds both paths (a
    /// Warning), and <see cref="FinishRenamesAsync"/> makes the record name the new path once it can be written.
    /// </summary>
    /// <param name="entry">The rename, as <see cref="RenameOnce"/> recorded it.</param>
    /// <param name="cancellationToken">Cancels waiting between attempts.</param>
    /// <returns>Whether the file is back at its old path.</returns>
    public async Task<bool> UndoRenameAsync(ClipRenameEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sameFile = ClipRootPaths.Comparer.Equals(entry.From, entry.To);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var back = ClipFileGuard.Check(entry.From, _store.ClipRoots, _fileSystem);
                    if (!sameFile && back.Verdict == ClipPathVerdict.Exists)
                    {
                        // Something is at the old path now: the file can't go back over it.
                        break;
                    }

                    _fileSystem.MoveFile(entry.To, entry.From);

                    // The move back on the disk before the entry goes, or the recovery after a power cut could find the file at
                    // the new path and no entry naming it; one that isn't flushed keeps the entry (the recovery finds the
                    // file back at its old path, the record naming it: not moved).
                    var flushed = _fileSystem.FlushFile(entry.From);
                    lock (_renaming)
                    {
                        ClearUnflushed(entry);
                    }

                    if (flushed)
                    {
                        Journal?.Remove(entry);
                    }

                    _states?.Forget(entry.From);
                    _states?.Forget(entry.To);
                    _logger.LogInformation("The clip file of bookmark {BookmarkId} was put back under its old name: its record couldn't be written", entry.BookmarkId);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= Attempts || !IsWorthRetrying(ex))
                    {
                        _logger.LogDebug(ex, "Could not put {Path} back", entry.To);
                        break;
                    }
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }

            lock (_renaming)
            {
                ClearUnflushed(entry);
            }

            _states?.Forget(entry.From);
            _states?.Forget(entry.To);
            _logger.LogWarning(
                "The clip file of bookmark {BookmarkId} was renamed, but neither its record could be written nor the file put back; the rename is finished as soon as the bookmark data can be written (it is tried again after a minute, then less often while nothing changes)",
                entry.BookmarkId);
            return false;
        }
        finally
        {
            EndRenaming(entry.BookmarkId);
        }
    }

    /// <summary>
    /// Finishes every clip file rename the journal holds that isn't under way: at startup (before the clip jobs
    /// resume, from <see cref="PartialFileJanitor"/>), while one waits (<see cref="RetryWaitingRenamesAsync"/>),
    /// daily (the maintenance task's <c>ClipFiles</c> step, before the missing-clip sweep) and before a request's rename,
    /// delete or remake of a clip one holds. For each, under the store's lock, from what is on disk (each path judged afresh,
    /// the file told apart by its identity): the record naming the new path already, the file there, is
    /// <see cref="ClipRenameRecovery.Written"/>; naming the old one, the file moved (it is at the new path, not the old) is
    /// <see cref="ClipRenameRecovery.Completed"/>: the record now names the new path, with the rename's name when the record is
    /// unchanged since; also when the new path holds a file no record names that can't be told to be the clip's, in a clip
    /// folder recognised as the clips' own, with a Warning (never a clip lost to an id the file system
    /// changed); the file still (or, after a power cut, again) at the old path is <see cref="ClipRenameRecovery.NotMoved"/>,
    /// the record naming the old path; neither is <see cref="ClipRenameRecovery.Lost"/>; a record gone or naming another file
    /// is <see cref="ClipRenameRecovery.MovedOn"/>. A change of case alone is judged by the case the folder holds the file in.
    /// Each entry's clip folder is first asked whether it is there outside the store's lock
    /// (each folder once per pass, <see cref="RootProbeSlowThreshold"/> at most): one that isn't, can't answer or answers too
    /// slowly keeps its entries waiting without taking the lock, so a share that doesn't answer never holds up other writes.
    /// Each of those drops its entry (and a rename finished goes into the history); no file is moved, deleted or written. A
    /// clip folder that can't be reached, or that is there but isn't recognised as the clips' own while the file is at neither
    /// path (an empty mount point), or a write that fails, keeps it
    /// (<see cref="ClipRenameRecovery.Waiting"/>). An entry that can't be read is counted
    /// (<see cref="ClipRenameRecovery.Unreadable"/>) and stays.
    /// </summary>
    /// <param name="writer">The part of Bookmarks asking, when it isn't a request: it must be allowed to write the store
    /// (<see cref="BackgroundWriteGate"/>), or nothing is done.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <param name="quiet">Whether a pass that finishes nothing logs at Debug only (the retry while renames wait).</param>
    /// <returns>What the pass did.</returns>
    public async Task<ClipRenameRecovery> FinishRenamesAsync(BackgroundWriter? writer, CancellationToken cancellationToken, bool quiet = false)
    {
        var journal = Journal;
        if (journal is null)
        {
            return ClipRenameRecovery.None;
        }

        List<ClipRenameEntry> entries;
        lock (_renaming)
        {
            entries = journal.Entries.Where(e => !_renaming.Contains(e.BookmarkId)).ToList();
        }

        var unreadable = journal.UnreadableFiles.Count;
        if (entries.Count == 0)
        {
            return ClipRenameRecovery.None with { Unreadable = unreadable };
        }

        if ((writer is { } background && !_gate.MayWrite(background)) || _store.IsReadOnly)
        {
            return ClipRenameRecovery.None with { Waiting = entries.Count, Unreadable = unreadable };
        }

        int written = 0, completed = 0, notMoved = 0, movedOn = 0, lost = 0, waiting = 0;
        var looked = new Dictionary<string, bool>(ClipRootPaths.Comparer);
        var roots = _store.ClipRoots;
        foreach (var entry in entries)
        {
            // Outside the store's lock: a clip folder that isn't there, can't answer or answers too slowly keeps the entry
            // waiting (Decide would judge it so, with its look under the lock).
            if (!IsRootReachable(entry.From, roots, looked) || !IsRootReachable(entry.To, roots, looked))
            {
                waiting++;
                _logger.LogDebug("The rename of {From} to {To} (bookmark {BookmarkId}) waits: its clip folder can't be reached now", entry.From, entry.To, entry.BookmarkId);
                continue;
            }

            var outcome = RenameOutcome.Waiting;
            try
            {
                await _store.UpdateDocumentAsync(
                    document =>
                    {
                        outcome = Decide(document, entry, out var finished);
                        return finished is null
                            ? document
                            : document with { Bookmarks = document.Bookmarks.Select(b => b.Id.Equals(entry.BookmarkId) ? finished : b).ToList() };
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PluginException ex)
            {
                if (quiet)
                {
                    _logger.LogDebug("Could not finish the rename of the clip file of bookmark {BookmarkId}: the bookmark data couldn't be written ({Error})", entry.BookmarkId, ex.Message);
                }
                else
                {
                    _logger.LogWarning("Could not finish the rename of the clip file of bookmark {BookmarkId}: the bookmark data couldn't be written ({Error})", entry.BookmarkId, ex.Message);
                }

                outcome = RenameOutcome.Waiting;
            }

            switch (outcome)
            {
                case RenameOutcome.Written: written++; break;
                case RenameOutcome.Completed: completed++; break;
                case RenameOutcome.NotMoved: notMoved++; break;
                case RenameOutcome.MovedOn: movedOn++; break;
                case RenameOutcome.Lost: lost++; break;
                default: waiting++; break;
            }

            if (outcome != RenameOutcome.Waiting)
            {
                if (outcome is RenameOutcome.Written or RenameOutcome.Completed)
                {
                    NoteFinished(entry, entry.To);
                }

                journal.Remove(entry);
                lock (_renaming)
                {
                    ClearUnflushed(entry);
                }

                _states?.Forget(entry.From);
                _states?.Forget(entry.To);
            }

            _logger.LogDebug("The rename of {From} to {To} (bookmark {BookmarkId}): {Outcome}", entry.From, entry.To, entry.BookmarkId, outcome);
        }

        var recovery = new ClipRenameRecovery(written, completed, notMoved, movedOn, lost, waiting) { Unreadable = unreadable };
        var level = quiet && waiting == entries.Count ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(
            level,
            "Clip file renames that weren't finished: {Completed} finished (the record names the new file now), {Written} already written, {NotMoved} not moved, {MovedOn} whose clip changed since, {Lost} whose file is at neither path, {Waiting} still waiting (a clip folder that can't be reached or isn't recognised, or data that can't be written), {Unreadable} whose journal entry can't be read",
            recovery.Completed,
            recovery.Written,
            recovery.NotMoved,
            recovery.MovedOn,
            recovery.Lost,
            recovery.Waiting,
            recovery.Unreadable);
        return recovery;
    }

    /// <summary>
    /// Tries the renames that wait again (not only at that clip's next action, the daily maintenance or a
    /// restart), quietly while nothing changes, until the server stops; the clip worker's service runs it from its start.
    /// It backs off while a pass changes nothing: the pause doubles from
    /// <paramref name="interval"/> up to <see cref="RenameRetryMaxInterval"/>, and is <paramref name="interval"/> again as soon
    /// as the renames that wait change (one finished, or a new one waiting, which it notices within
    /// <paramref name="interval"/>).
    /// </summary>
    /// <param name="interval">The first pause, and how often it looks whether the renames that wait changed
    /// (<see cref="RenameRetryInterval"/>).</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>A task that ends when it is stopped.</returns>
    public Task RetryWaitingRenamesAsync(TimeSpan interval, CancellationToken cancellationToken)
        => RetryWaitingRenamesCoreAsync(interval, RenameRetryMaxInterval, null, cancellationToken);

    /// <summary>
    /// The pause after one that changed nothing (<see cref="RetryWaitingRenamesAsync"/>): twice
    /// as long, at most <paramref name="longest"/>.
    /// </summary>
    /// <param name="pause">The pause so far.</param>
    /// <param name="longest">The longest pause.</param>
    /// <returns>The next pause.</returns>
    internal static TimeSpan NextRetryPause(TimeSpan pause, TimeSpan longest) => pause >= longest / 2 ? longest : pause * 2;

    /// <summary>
    /// <see cref="RetryWaitingRenamesAsync"/> with its longest pause, telling a test the pause
    /// chosen after each pass.
    /// </summary>
    /// <param name="first">The first pause, and the step it looks at the renames that wait in.</param>
    /// <param name="longest">The longest pause.</param>
    /// <param name="afterPass">Told the next pause after each pass, if given.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>A task that ends when it is stopped.</returns>
    internal async Task RetryWaitingRenamesCoreAsync(TimeSpan first, TimeSpan longest, Action<TimeSpan>? afterPass, CancellationToken cancellationToken)
    {
        var pause = first;
        var sinceLastPass = TimeSpan.Zero;
        string? lastSeen = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(first, cancellationToken).ConfigureAwait(false);
                sinceLastPass += first;
                if (WaitingRenames == 0)
                {
                    (pause, sinceLastPass, lastSeen) = (first, TimeSpan.Zero, null);
                    continue;
                }

                // A change since the last pass (a rename finished elsewhere, a new one waiting) is tried at once, and the pause
                // starts over; otherwise only once the pause is over.
                var before = WaitingSignature();
                var changedSince = !string.Equals(before, lastSeen, StringComparison.Ordinal);
                if (!changedSince && sinceLastPass < pause)
                {
                    continue;
                }

                await FinishRenamesAsync(BackgroundWriter.PartialFileJanitor, cancellationToken, quiet: true).ConfigureAwait(false);
                var after = WaitingSignature();
                pause = changedSince || !string.Equals(before, after, StringComparison.Ordinal) ? first : NextRetryPause(pause, longest);
                sinceLastPass = TimeSpan.Zero;
                lastSeen = after;
                afterPass?.Invoke(pause);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Tried again after the next pause; the journal keeps them.
                _logger.LogDebug(ex, "Could not try the clip file renames that wait again");
                pause = NextRetryPause(pause, longest);
                sinceLastPass = TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Says, at the daily maintenance, that renames still wait: a Warning with how many, those whose journal
    /// entry can't be read among them, naming Diagnostics' <c>ClipRenames</c>.
    /// </summary>
    /// <param name="recovery">The maintenance's pass.</param>
    public void WarnIfRenamesWait(ClipRenameRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.Waiting + recovery.Unreadable == 0)
        {
            return;
        }

        _logger.LogWarning(
            "{Count} clip file renames still aren't finished ({Unreadable} of them with a journal entry that can't be read): their clips are left as they are (nothing deletes, forgets or moves them) and show \"Rename not finished\"; see the Bookmarks settings page's Diagnostics (ClipRenames)",
            recovery.Waiting + recovery.Unreadable,
            recovery.Unreadable);
    }

    /// <summary>
    /// What Diagnostics shows of the clip file renames (its <c>ClipRenames</c> entry): how many wait, how many
    /// entries can't be read (and their files' names, for an admin to look at), and the records they hold.
    /// </summary>
    /// <returns>The entry.</returns>
    public IDictionary<string, object?> DescribeRenames()
    {
        var journal = Journal;
        var entries = journal?.Entries ?? [];
        List<Guid> waiting;
        lock (_renaming)
        {
            waiting = entries.Where(e => !_renaming.Contains(e.BookmarkId) && !IsWritten(e)).Select(e => e.BookmarkId).ToList();
        }

        var unreadable = journal?.UnreadableFiles ?? [];
        var held = waiting.Concat(journal?.UnreadableIds ?? []).Distinct().Order().ToList();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Unfinished"] = waiting.Count + unreadable.Count,
            ["Waiting"] = waiting.Count,
            ["Unreadable"] = unreadable.Count,
            ["UnreadableFiles"] = unreadable.Take(20).ToList(),
            ["Bookmarks"] = held.Take(20).ToList(),
            ["History"] = History?.Entries.Count ?? 0
        };
    }

    /// <summary>
    /// Names a renamed clip file again where a restored or imported record names a path its file had left (see
    /// <see cref="ClipRenameRelink"/>): from the record the data had before (<paramref name="before"/>, an admin's restore
    /// or an import), else from the renames' history. Never a file another record names or one waiting to be deleted;
    /// nothing is moved or written but the records returned.
    /// </summary>
    /// <param name="document">The document as it is about to be written.</param>
    /// <param name="before">The document before the restore or the import, if there was one.</param>
    /// <param name="relinked">How many records name their renamed file again.</param>
    /// <returns>The document to write.</returns>
    public BookmarkStoreDocument RelinkRenamed(BookmarkStoreDocument document, BookmarkStoreDocument? before, out int relinked)
        => RelinkRestored(document, before, out relinked, out _);

    /// <summary>
    /// <see cref="RelinkRenamed"/>, also saying how many records let go
    /// of a file they would have shared with another record (<see cref="ClipRenameRelink"/>).
    /// </summary>
    /// <param name="document">The document as it is about to be written.</param>
    /// <param name="before">The document before the restore or the import, if there was one.</param>
    /// <param name="relinked">How many records name their renamed file again.</param>
    /// <param name="letGo">How many records were let go of a file another record has.</param>
    /// <returns>The document to write.</returns>
    public BookmarkStoreDocument RelinkRestored(BookmarkStoreDocument document, BookmarkStoreDocument? before, out int relinked, out int letGo)
        => ClipRenameRelink.Apply(document, before, History?.Entries ?? [], _fileSystem, _logger, restored: true, out relinked, out letGo);

    /// <summary>
    /// The relink of <see cref="RelinkRenamed"/> from the history alone, as a write of its own (at startup and in the daily
    /// maintenance, before the missing-clip sweep: a restore made while a clip folder was away is put right once it is back).
    /// </summary>
    /// <param name="writer">The part of Bookmarks asking.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many records name their renamed file again.</returns>
    public async Task<int> RelinkRenamedAsync(BackgroundWriter writer, CancellationToken cancellationToken)
    {
        if (History is not { } history || history.Entries.Count == 0 || !_gate.MayWrite(writer) || _store.IsReadOnly)
        {
            return 0;
        }

        // Not a restore: the records are the data as it is, so only a path whose file is gone is named again (a record whose
        // file is there is never judged a stranger to it here; a remade clip can sit under a name an old rename left).
        var relinked = 0;
        await _store.UpdateDocumentAsync(
            document => ClipRenameRelink.Apply(document, null, history.Entries, _fileSystem, _logger, restored: false, out relinked, out _),
            cancellationToken).ConfigureAwait(false);
        return relinked;
    }

    /// <summary>
    /// The daily care of the renames' history: no retention by days (a restore of the
    /// oldest snapshot kept, or of an old export, still needs the renames made since), only its size
    /// (<see cref="ClipRenameHistory.MaxEntries"/>); and the renames of clips whose record is gone or has no clip file any more
    /// are dropped (their paths can start with a note's words), unless a file one of them led to is still there (or its clip
    /// folder can't be reached now), which a restore of that record could still name again. Outside the store's lock.
    /// </summary>
    /// <returns>How many renames were dropped.</returns>
    public int PruneRenameHistory()
    {
        if (History is not { } history)
        {
            return 0;
        }

        var dropped = history.Prune();
        var gone = history.Entries
            .GroupBy(e => e.BookmarkId)
            .Where(g => _store.Get(g.Key)?.Clip?.FilePath is not { Length: > 0 } && !g.Any(e => Check(e.To) is { } there && (there.Verdict == ClipPathVerdict.Exists || there.IsUnreachable)))
            .Select(g => g.Key)
            .ToList();
        return gone.Count == 0 ? dropped : dropped + history.Forget(gone.ToHashSet());
    }

    /// <summary>
    /// Forgets the renames' history of records that are gone for good (a deleted user's records
    /// under <c>DeletedUserPolicy=Delete</c>), whose paths can start with a note's words.
    /// </summary>
    /// <param name="bookmarkIds">The records' ids.</param>
    /// <returns>How many renames were dropped.</returns>
    public int ForgetRenames(IEnumerable<Guid> bookmarkIds)
    {
        ArgumentNullException.ThrowIfNull(bookmarkIds);
        return History?.Forget(bookmarkIds.ToHashSet()) ?? 0;
    }

    // Whether a path one end of a rename holds: being renamed, or renamed without its record written (an entry whose record
    // names its new path already holds nothing: the rename is made, only its entry is still to go); and the record's path of
    // an entry that can't be read.
    private bool IsHeld(string fullPath)
    {
        if (Journal is not { } journal)
        {
            return false;
        }

        if (journal.Holding(fullPath) is { } entry && !IsWritten(entry))
        {
            return true;
        }

        foreach (var id in journal.UnreadableIds)
        {
            if (ClipFileGuard.TryGetFullPath(_store.Get(id)?.Clip?.FilePath, out var held) && ClipRootPaths.Comparer.Equals(held, fullPath))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the record of an entry names its new path already (as it was given, case included).
    private bool IsWritten(ClipRenameEntry entry)
        => _store.Get(entry.BookmarkId)?.Clip?.FilePath is { } path && string.Equals(path, entry.To, StringComparison.Ordinal);

    // The journal and the history, made at the first look: in the plugin's data folder.
    private void Look()
    {
        lock (_journalLock)
        {
            if (_journalLooked)
            {
                return;
            }

            _journalLooked = true;
            var folder = _paths?.Root;
            try
            {
                folder ??= Path.GetDirectoryName(_store.Documents?.FilePath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "The bookmark store has no file; clip files can't be renamed");
                folder = null;
            }

            if (!string.IsNullOrEmpty(folder))
            {
                _journal = new ClipRenameJournal(folder, _fileSystem, _logger);
                _history = new ClipRenameHistory(folder, _fileSystem, _logger);
            }
        }
    }

    // A finished rename into the history (best effort: a history that can't be written is a Warning there), with the file's
    // identity read at its new path now that it is there (FAT and exFAT can number a file anew
    // when it is renamed, so the identity read before the move might not be the one a later relink finds), else the entry's.
    private void NoteFinished(ClipRenameEntry entry, string path)
    {
        if (History is not { } history)
        {
            return;
        }

        history.Add(entry with { File = _fileSystem.GetFileIdentity(path) ?? entry.File }, _clock.GetUtcNow().UtcDateTime);
    }

    // The history of a clip file deleted: the renames that led to it forgotten (best effort).
    private void ForgetRenamesOf(Guid? bookmarkId, string? fullPath)
    {
        if (bookmarkId is { } id && !string.IsNullOrEmpty(fullPath))
        {
            History?.ForgetChain(id, fullPath);
        }
    }

    // A rename's mark of a move not flushed cleared, only while it is that rename's (under _renaming's lock).
    private bool ClearUnflushed(ClipRenameEntry entry)
    {
        if (!_unflushed.TryGetValue(entry.BookmarkId, out var marked) || !IsSameRename(marked, entry))
        {
            return false;
        }

        _unflushed.Remove(entry.BookmarkId);
        return true;
    }

    // Whether two entries are one rename: the same record, paths and start (the identity aside: the history's is read anew).
    private static bool IsSameRename(ClipRenameEntry a, ClipRenameEntry b)
        => a.BookmarkId.Equals(b.BookmarkId)
            && string.Equals(a.From, b.From, StringComparison.Ordinal)
            && string.Equals(a.To, b.To, StringComparison.Ordinal)
            && a.StartedUtc.Ticks == b.StartedUtc.Ticks;

    // What the renames that wait are, to tell a pass that changed something from one that didn't.
    private string WaitingSignature()
    {
        if (Journal is not { } journal)
        {
            return string.Empty;
        }

        var entries = journal.Entries;
        List<string> keys;
        lock (_renaming)
        {
            keys = entries
                .Where(e => !_renaming.Contains(e.BookmarkId) && !IsWritten(e))
                .Select(e => e.BookmarkId.ToString("N", System.Globalization.CultureInfo.InvariantCulture) + "@" + e.StartedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        return string.Join(',', keys) + "/" + journal.UnreadableFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Whether the clip folder of a rename's path answers that it is there, quickly enough (once per folder and pass, outside
    // the store's lock). A path under no recorded clip folder needs no look (Decide refuses it).
    private bool IsRootReachable(string path, IReadOnlyList<string> roots, Dictionary<string, bool> looked)
    {
        if (!ClipFileGuard.TryGetFullPath(path, out var full) || ClipFileGuard.FindRoot(full, roots) is not { } root)
        {
            return true;
        }

        if (looked.TryGetValue(root, out var known))
        {
            return known;
        }

        var started = Stopwatch.GetTimestamp();
        bool there;
        try
        {
            there = _fileSystem.FolderExists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The clip folder {Folder} can't answer whether it is there", root);
            there = false;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var reachable = there && elapsed <= RootProbeSlowThreshold;
        if (there && !reachable)
        {
            _logger.LogDebug("The clip folder {Folder} took {Elapsed} ms to answer; its renames wait for a later pass", root, (long)elapsed.TotalMilliseconds);
        }

        looked[root] = reachable;
        return reachable;
    }

    // Whether a move that threw did move the file all the same (a share's lost answer): a change of case alone
    // by the case the folder holds the file in now; otherwise the old path empty and a file at the new one, which was free
    // when the rename chose it under the store's lock, with a Warning when it can't be told to be the clip's by its identity.
    private bool MovedAnyway(ClipRenameEntry entry, bool sameFile, Exception error)
    {
        if (sameFile)
        {
            if (string.Equals(_fileSystem.GetOnDiskName(entry.To), Path.GetFileName(entry.To), StringComparison.Ordinal))
            {
                _logger.LogDebug(error, "Renaming {Path} failed with the file renamed all the same", entry.From);
                return true;
            }

            return false;
        }

        FileAttributes? source;
        try
        {
            source = _fileSystem.GetAttributes(entry.From);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (source is not null || _fileSystem.GetFileIdentity(entry.To) is not { } now)
        {
            return false;
        }

        if (!ClipFileIdentity.IsSameFile(entry.File, now))
        {
            _logger.LogWarning("Renaming the clip file of bookmark {BookmarkId} reported a failure, but the file is gone from its old name and a file is at its new one, which it is taken for (its identity couldn't be confirmed)", entry.BookmarkId);
        }

        _logger.LogDebug(error, "Renaming {Path} failed with the file moved all the same", entry.From);
        return true;
    }

    // A move that failed, the file where it was: the request's answer.
    private ClipFileResult MoveFailed(ClipRenameEntry entry, ClipPathCheck from, ClipPathCheck to, int attempt, bool final, Exception ex)
    {
        if (ex is FileNotFoundException)
        {
            _states?.Forget(entry.From);
            return new ClipFileResult(ClipFileOutcome.Missing, from, attempt, "The clip file isn't there.");
        }

        if (ex.HResult is FileExists or AlreadyExists)
        {
            _logger.LogDebug("The new name of the clip file of bookmark {BookmarkId} was taken just now: {Path}", entry.BookmarkId, entry.To);
            return new ClipFileResult(ClipFileOutcome.Failed, to, attempt, "A file with the clip's new name exists already.", NameTaken: true);
        }

        var worthRetrying = IsWorthRetrying(ex);
        var result = new ClipFileResult(
            IsInUse(ex) ? ClipFileOutcome.InUse : ClipFileOutcome.Failed,
            from,
            attempt,
            ClipFiles.RedactForLog(ex.Message, entry.From, entry.To),
            WorthRetrying: worthRetrying,
            HResult: ex.HResult);
        return final || !worthRetrying ? Fail(result, "rename", entry.BookmarkId, ex, entry.From) : result;
    }

    // What a rename that wasn't finished is, from the record and the disk (FinishRenamesAsync), and the record to write.
    private RenameOutcome Decide(BookmarkStoreDocument document, ClipRenameEntry entry, out Bookmark? finished)
    {
        finished = null;
        var from = ClipFileGuard.Check(entry.From, document.ClipRoots, _fileSystem);
        var to = ClipFileGuard.Check(entry.To, document.ClipRoots, _fileSystem);
        if (from.IsUnreachable || to.IsUnreachable)
        {
            return RenameOutcome.Waiting;
        }

        // A clip folder that is there but isn't recognised as the clips' own (no marker of its own; an empty mount
        // point, a disk that isn't mounted): a file at neither path there says nothing.
        bool? recognised = null;
        bool Recognised() => recognised ??= (from.Root ?? to.Root) is { } root && IsRecognisedRoot(root);
        RenameOutcome Gone() => Recognised() ? RenameOutcome.Lost : RenameOutcome.Waiting;

        var record = document.Bookmarks.FirstOrDefault(b => b.Id.Equals(entry.BookmarkId));
        var path = ClipFileGuard.TryGetFullPath(record?.Clip?.FilePath, out var full) ? full : null;
        if (ClipRootPaths.Comparer.Equals(entry.From, entry.To))
        {
            return DecideCaseOnly(entry, record, path, from, Gone, out finished);
        }

        bool Ours(ClipPathCheck check, out ClipFileIdentity? identity)
        {
            identity = check.Verdict == ClipPathVerdict.Exists ? _fileSystem.GetFileIdentity(check.FullPath!) : null;
            return identity is not null && (entry.File is null || ClipFileIdentity.IsSameFile(entry.File, identity));
        }

        var fromOurs = Ours(from, out _);
        var toOurs = Ours(to, out _);
        bool NamedByAnother(string target) => document.Bookmarks.Any(b => !b.Id.Equals(entry.BookmarkId) && ClipFileGuard.TryGetFullPath(b.Clip?.FilePath, out var other) && ClipRootPaths.Comparer.Equals(other, target));

        // The record names the new path already: written, the file there. A power cut that kept the record's write and lost
        // the rename (the clip on another volume) finds the file back at the old path: the record names it again.
        if (path is not null && ClipRootPaths.Comparer.Equals(path, entry.To))
        {
            if (to.Verdict == ClipPathVerdict.Exists)
            {
                return RenameOutcome.Written;
            }

            if (fromOurs && record is not null)
            {
                finished = record with { Clip = record.Clip! with { FilePath = from.FullPath } };
                _logger.LogWarning("The clip file of bookmark {BookmarkId} is under its old name although its record names the new one (the rename didn't reach the disk before the server stopped): its record names the old name again", entry.BookmarkId);
                return RenameOutcome.NotMoved;
            }

            return Gone();
        }

        if (record is null || path is null || !ClipRootPaths.Comparer.Equals(path, entry.From))
        {
            if (to.Verdict == ClipPathVerdict.Exists && !NamedByAnother(entry.To))
            {
                _logger.LogWarning(
                    "A clip file renamed for bookmark {BookmarkId} before the server stopped is no clip's file any more (the clip was deleted, made again or restored since); it stays where it is",
                    entry.BookmarkId);
            }

            return RenameOutcome.MovedOn;
        }

        // The record names the old path.
        if (toOurs && !fromOurs)
        {
            return Complete(entry, record, to, out finished);
        }

        if (fromOurs)
        {
            return RenameOutcome.NotMoved;
        }

        // Neither path holds the file as the journal recorded it: a file at the new path that no record names, which was free
        // when the rename chose it, is the clip's (an id the file system changed, a time it rounded), in a folder recognised as
        // the clips' own; a Warning says so.
        if (to.Verdict == ClipPathVerdict.Exists && !NamedByAnother(entry.To))
        {
            if (!Recognised())
            {
                return RenameOutcome.Waiting;
            }

            _logger.LogWarning("The clip file of bookmark {BookmarkId} couldn't be told apart by its identity after a rename that wasn't finished; the file at its new name, which no clip names, is taken for it", entry.BookmarkId);
            return Complete(entry, record, to, out finished);
        }

        return Gone();
    }

    // A change of case alone: the file is wherever the case is; the record follows the case the folder holds
    // the file in, the rename's name only when the folder has the new case.
    private RenameOutcome DecideCaseOnly(ClipRenameEntry entry, Bookmark? record, string? path, ClipPathCheck check, Func<RenameOutcome> gone, out Bookmark? finished)
    {
        finished = null;
        if (record is null || path is null || !ClipRootPaths.Comparer.Equals(path, entry.From))
        {
            return RenameOutcome.MovedOn;
        }

        if (check.Verdict != ClipPathVerdict.Exists || _fileSystem.GetOnDiskName(entry.To) is not { } name || Path.GetDirectoryName(entry.To) is not { } folder)
        {
            return check.Verdict == ClipPathVerdict.Missing ? gone() : RenameOutcome.Waiting;
        }

        var onDisk = Path.Combine(folder, name);
        if (string.Equals(path, onDisk, StringComparison.Ordinal))
        {
            return string.Equals(onDisk, entry.To, StringComparison.Ordinal) ? RenameOutcome.Written : RenameOutcome.NotMoved;
        }

        if (string.Equals(onDisk, entry.To, StringComparison.Ordinal))
        {
            return Complete(entry, record, check with { FullPath = onDisk }, out finished);
        }

        finished = record with { Clip = record.Clip! with { FilePath = onDisk } };
        return RenameOutcome.NotMoved;
    }

    // The file is at the new path: the record names it, with the rename's name while nothing changed the record since.
    private RenameOutcome Complete(ClipRenameEntry entry, Bookmark record, ClipPathCheck to, out Bookmark? finished)
    {
        var named = entry.NameChanged && record.UpdatedAt.Ticks == entry.UpdatedAt.Ticks
            ? BookmarkRecordRules.WithClipName(record, entry.Name, DateTime.UtcNow)
            : record;
        finished = named with { Clip = named.Clip! with { FilePath = to.FullPath, MissingSinceUtc = null } };
        _logger.LogInformation("Finished the rename of the clip file of bookmark {BookmarkId}: its record names the renamed file now", entry.BookmarkId);
        return RenameOutcome.Completed;
    }

    // Whether a path is taken for a new clip file name (NameTaken): anything there, or a clip being written under it, a
    // record's or a waiting delete's path, or a rename's. A path the file system can't answer about ends the choice:
    // the next name would be asked about the same folder, as slowly, up to a thousand times.
    private bool IsTaken(string candidate, HashSet<string> taken, Func<bool> pauseOnce)
    {
        if (taken.Contains(candidate) || Journal?.Holding(candidate) is not null)
        {
            return true;
        }

        try
        {
            return LookUp(candidate);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && pauseOnce())
        {
            // A drive that is spinning up, a share that missed one answer; asked once more, after the pause.
            _logger.LogDebug(ex, "A look-up of {Path} failed; asking once more", candidate);
            try
            {
                return LookUp(candidate);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                throw new ClipFolderUnreachableException(FolderUnreachableMessage, again);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ClipFolderUnreachableException(FolderUnreachableMessage, ex);
        }

        bool LookUp(string path) => _fileSystem.GetAttributes(path) is not null || _fileSystem.GetAttributes(path + ClipFileGuard.PartialExtension) is not null;
    }

    private void EndRenaming(Guid bookmarkId)
    {
        lock (_renaming)
        {
            _renaming.Remove(bookmarkId);
        }
    }
}
