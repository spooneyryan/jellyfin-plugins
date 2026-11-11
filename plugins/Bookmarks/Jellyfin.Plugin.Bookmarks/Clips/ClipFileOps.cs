using System;
using System.Collections.Generic;
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
/// Every operation on a clip file: delete, move, serve, and the retries of the files an automatic
/// deletion couldn't delete. Each goes through <see cref="ClipFileGuard"/> first, and nothing the guard refuses is touched.
/// </summary>
/// <remarks>
/// <para><b>File first, then the record.</b> A request's delete acts on the file first, with up to <see cref="Attempts"/>
/// attempts 200 ms apart, and its record changes only once the file is gone. The manager makes each attempt with
/// <see cref="DeleteOnce"/> inside the store's write of the record, so the record it checks is the one it writes, while the
/// pauses between attempts are outside the store's lock. On Windows a file opened without delete sharing (our own downloads
/// and previews in another tab, and, for clips inside a library, Jellyfin's playback, ffprobe and trickplay) can't be
/// deleted or renamed: after the attempts such a sharing or lock violation (<c>0x80070020</c>, <c>0x80070021</c>) is
/// <see cref="ClipFileOutcome.InUse"/>, which a request answers with 409 and <see cref="InUseMessage"/>
/// (<see cref="ToRequestError"/>), the record unchanged. A file that isn't there counts as deleted, but only in a clip
/// folder that is there: in one that isn't (an unplugged drive) or can't be read, nothing is touched, a request
/// answers 503 with the record unchanged, and an automatic deletion keeps the file listed until the folder is back.</para>
/// <para><b>Automatic deletions</b>, which no user waits for (a remake's old file, a job's discarded output, the
/// deleted-user <c>Delete</c> policy), never fail on a file in use: <see cref="DeleteOrQueueAsync"/> lists it in
/// <c>PendingFileDeletes</c>, and <see cref="RetryPendingDeletesAsync"/> tries again (at startup, from
/// <see cref="PartialFileJanitor"/>; daily from the maintenance task) until it is gone. Those two write the store
/// on their own, so they ask <see cref="BackgroundWriteGate"/> for their caller first.</para>
/// <para><b>Stale <c>.partial</c> files</b> are the janitor's: it lists the clip folders
/// (<see cref="ListClipFolder"/>) and deletes with <see cref="DeleteStalePartialAsync"/>, which refuses any name but a clip
/// being written (<see cref="ClipNamePattern"/>).</para>
/// <para><b>Never a file another record names</b> (Bookmarks 1.1.3, a defence in depth): every delete (a request's, a bulk
/// delete's, an automatic one) leaves a clip file alone while a record other than the ones letting go of it still names it,
/// its owner's or another user's, a live bookmark or a tombstone. The delete then counts as done for the caller's record
/// (<see cref="ClipFileOutcome.NamedByAnotherRecord"/>, <see cref="ClipFileResult.IsGone"/>), which lets go of the file, and
/// a Warning names both bookmarks; the file stays for the other record. Before 1.1.3 two records could name one file (a new
/// clip took the name of a "File missing" record's file), and the first one's delete or remake then deleted the other's
/// clip.</para>
/// <para>Its log lines at Information and above name a clip file only by its bookmark's id (a clip file's name holds the
/// bookmark's name or note): the path goes to a Debug line of its own.</para>
/// <para>Every file it moves into place or deletes is forgotten by the My Clips list's <see cref="ClipStateCache"/>, so the
/// list judges that path again at once (otherwise a clip made again under the name of a file that had gone, moved into
/// place here, read "missing" for up to 30 s).</para>
/// <para><b>Renames</b> (<c>ClipFileOps.Renames.cs</c>): a clip file renamed within its folder, file first and
/// then the record, with the rename recorded in <see cref="ClipRenameJournal"/> before the move, so the server never loses
/// track of a file it moved and didn't record yet; both paths of a rename under way or not finished are held
/// (<see cref="Check"/>).</para>
/// </remarks>
internal sealed partial class ClipFileOps
{
    /// <summary>
    /// How many times a request's delete or move asks the file system before it gives up.
    /// </summary>
    public const int Attempts = 3;

    /// <summary>
    /// The 409 message for a clip file in use.
    /// </summary>
    public const string InUseMessage = "The clip file is in use (being played or downloaded); try again shortly.";

    /// <summary>
    /// The 404 message a user gets for a clip file outside the clip folders (users get 404, and nothing is touched).
    /// </summary>
    public const string NotInClipFoldersMessage = "The clip file isn't in this server's clip folders, so it can't be used.";

    /// <summary>
    /// The 409 message an admin gets for a clip file outside the clip folders (the admin clip routes of 1.2.0).
    /// </summary>
    public const string OutsideClipFoldersMessage = "The clip file is outside the clip folders; nothing was changed.";

    /// <summary>
    /// The 503 message for a clip file that couldn't be changed for another reason (no permission, a drive not ready).
    /// </summary>
    public const string FailedMessage = "The clip file couldn't be changed right now; nothing was changed. Try again later.";

    /// <summary>
    /// The 503 message for a clip file whose clip folder isn't there ("clip folder offline").
    /// </summary>
    public const string FolderUnavailableMessage = "The clip's folder isn't available (an unplugged drive, or a folder that was moved or removed); nothing was changed. Try again once it is back.";

    /// <summary>
    /// The 503 message for a clip whose file is gone from a clip folder that is there but isn't recognised as the one its clips
    /// were made in (no marker file of its own, <see cref="ClipPathCheck.RootUnrecognised"/>).
    /// </summary>
    public const string FolderUnrecognisedMessage = "The clip's folder is there, but it isn't recognised as the one its clips were made in (a drive or share that isn't mounted, or a folder in its place?), so its file isn't taken for gone and nothing was changed. Your server admin can confirm the folder on the Bookmarks settings page's Diagnostics tab.";

    /// <summary>
    /// The 503 message for a clip file one of whose paths a rename holds: being renamed just now, or renamed
    /// without its record written yet (<see cref="ClipPathCheck.RenameUnfinished"/>).
    /// </summary>
    public const string RenameUnfinishedMessage = "The clip file is being renamed, or its last rename isn't finished yet; nothing was changed. Try again shortly.";

    /// <summary>
    /// The 503 message of a rename, and the error of a clip that couldn't be planned, when the clip folder answered a look-up
    /// of a name with an error (since 1.2.0: a drive that isn't ready, a share that doesn't answer;
    /// <see cref="ClipFolderUnreachableException"/>).
    /// </summary>
    public const string FolderUnreachableMessage = "The clip folder can't be reached right now (a drive that isn't ready, or a share that doesn't answer); nothing was changed. Try again later.";

    /// <summary>
    /// The error a clip gets, and the 503 message of its request, when the clip folder couldn't be reached while its file's
    /// name was chosen.
    /// </summary>
    public const string FolderUnreachableClipMessage = "The clip folder can't be reached right now (a drive that isn't ready, or a share that doesn't answer), so the clip wasn't made. Try again later.";

    /// <summary>
    /// The 409 message of a rename of a clip file another record names too (since 1.2.0;
    /// worded without blame: two records can name one file after an import, a restore or a version
    /// before 1.1.3, and the other may be another user's): moving it would leave the other record without its file.
    /// </summary>
    public const string NamedByAnotherMessage = "Another clip names this clip's file too (possibly another user's clip), so the file can't be renamed; nothing was changed. Delete or forget this clip to keep the other clip's file.";

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int FileExists = unchecked((int)0x80070050);
    private const int AlreadyExists = unchecked((int)0x800700B7);

    private readonly IBookmarkStore _store;
    private readonly BackgroundWriteGate _gate;
    private readonly IClipFileSystem _fileSystem;
    private readonly ClipStateCache? _states;
    private readonly BookmarkPaths? _paths;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFileOps"/> class over the real file system.
    /// </summary>
    /// <param name="store">The bookmark store: its <c>ClipRoots</c>, <c>PendingFileDeletes</c> and records.</param>
    /// <param name="gate">Asked before the automatic operations write the store.</param>
    /// <param name="states">The My Clips list's clip states, told about every file moved into place or deleted.</param>
    /// <param name="paths">The plugin's paths: its data folder and the default clip folder in it.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The lifecycle's clock (the kit's <c>Clock</c>), for the missing marks of a confirmed clip
    /// folder's clips.</param>
    public ClipFileOps(IBookmarkStore store, BackgroundWriteGate gate, ClipStateCache states, BookmarkPaths paths, ILogger<ClipFileOps> logger, TimeProvider clock)
        : this(store, gate, logger, PhysicalClipFileSystem.Instance, DefaultRetryDelay, states ?? throw new ArgumentNullException(nameof(states)), paths ?? throw new ArgumentNullException(nameof(paths)), clock)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFileOps"/> class over a given file system (tests).
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="gate">Asked before the automatic operations write the store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="retryDelay">The pause between attempts.</param>
    /// <param name="states">The My Clips list's clip states to tell, if any.</param>
    /// <param name="paths">The plugin's paths, if the default clip folder is known; without them no folder counts as the
    /// default one.</param>
    /// <param name="clock">The lifecycle's clock; the system clock by default.</param>
    internal ClipFileOps(IBookmarkStore store, BackgroundWriteGate gate, ILogger logger, IClipFileSystem fileSystem, TimeSpan retryDelay, ClipStateCache? states = null, BookmarkPaths? paths = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _store = store;
        _gate = gate;
        _logger = logger;
        _fileSystem = fileSystem;
        _states = states;
        _paths = paths;
        _clock = clock ?? TimeProvider.System;
        RetryDelay = retryDelay;

        // The My Clips list shows a clip whose rename isn't finished as such, from the journal this reads.
        if (states is not null)
        {
            states.RenameUnfinished = HasUnfinishedRename;
        }
    }

    /// <summary>
    /// Gets the pause between two attempts by default: 200 ms.
    /// </summary>
    public static TimeSpan DefaultRetryDelay { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gets the pause between two attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; }

    /// <summary>
    /// Turns a failed operation a user asked for into its answer: 409 for a file in use; 404 for a path the guard
    /// refuses (for an admin, 409 "outside the clip folders"); 503 otherwise, with its own message when the clip folder isn't
    /// there. Nothing changed in any case.
    /// </summary>
    /// <param name="result">The operation's result; it didn't succeed.</param>
    /// <param name="forAdmin">Whether an admin route asked (since 1.2.0).</param>
    /// <returns>The exception to throw.</returns>
    public static PluginException ToRequestError(ClipFileResult result, bool forAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            ClipFileOutcome.InUse => PluginException.Conflict(InUseMessage),
            ClipFileOutcome.Refused => forAdmin ? PluginException.Conflict(OutsideClipFoldersMessage) : PluginException.NotFound(NotInClipFoldersMessage),
            ClipFileOutcome.Failed when result.Check.RenameUnfinished => PluginException.Unavailable(RenameUnfinishedMessage),
            ClipFileOutcome.Failed when result.Check.RootUnrecognised => PluginException.Unavailable(FolderUnrecognisedMessage),
            ClipFileOutcome.Failed when result.Check.Verdict == ClipPathVerdict.FolderUnavailable => PluginException.Unavailable(FolderUnavailableMessage),
            ClipFileOutcome.Failed => PluginException.Unavailable(FailedMessage),
            _ => throw new ArgumentException("The operation succeeded; there is no error to answer.", nameof(result))
        };
    }

    /// <summary>
    /// Whether an exception is Windows' "in use" answer: a sharing violation or a lock violation.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>Whether the file is in use.</returns>
    public static bool IsInUse(Exception exception)
        => exception is IOException && exception.HResult is SharingViolation or LockViolation;

    /// <summary>
    /// Checks a path against the recorded clip folders (<see cref="ClipFileGuard"/>). A path one end of a clip file rename
    /// holds (being renamed, or renamed without its record written yet; <see cref="ClipRenameJournal"/>; also the
    /// record's path of a journal entry that can't be read) reads as <see cref="ClipPathVerdict.Unreadable"/>
    /// with <see cref="ClipPathCheck.RenameUnfinished"/>: nothing deletes, forgets or moves it until the rename is finished.
    /// A rename whose record names its new path already holds nothing (no 503 for a delete between its write
    /// and the entry's end).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="allowPartial">Whether a clip's temporary <c>.partial</c> file passes too.</param>
    /// <returns>The verdict.</returns>
    public ClipPathCheck Check(string? path, bool allowPartial = false)
    {
        var check = ClipFileGuard.Check(path, _store.ClipRoots, _fileSystem, allowPartial);
        return check is { IsInClipFolders: true, FullPath: { } full } && IsHeld(full)
            ? check with { Verdict = ClipPathVerdict.Unreadable, Error = new IOException(RenameUnfinishedMessage), RenameUnfinished = true }
            : check;
    }

    /// <summary>
    /// Gets the full path of a clip file to serve: one the guard accepts and that exists. A refused path is logged.
    /// </summary>
    /// <param name="path">The record's path.</param>
    /// <param name="bookmarkId">The record, named in the log.</param>
    /// <returns>The full path, or <c>null</c> (a user's route answers 404).</returns>
    public string? FindServable(string? path, Guid bookmarkId)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // The file as it is, whatever a rename holds: serving changes nothing. While its rename is under way (moved, the record
        // not written yet: a few milliseconds under the store's lock), a file gone from the record's path is served
        // from where the rename put it, so a preview or a download asked for just then isn't a 404; only the rename of the very
        // record being served (never another user's file through a path theirs once had).
        var check = ClipFileGuard.Check(path, _store.ClipRoots, _fileSystem);
        if (check.Verdict == ClipPathVerdict.Exists)
        {
            return check.FullPath;
        }

        if (check is { Verdict: ClipPathVerdict.Missing, FullPath: { } full }
            && Journal?.Holding(full) is { } rename
            && rename.BookmarkId.Equals(bookmarkId)
            && ClipRootPaths.Comparer.Equals(rename.From, full)
            && ClipFileGuard.Check(rename.To, _store.ClipRoots, _fileSystem) is { Verdict: ClipPathVerdict.Exists } moved)
        {
            return moved.FullPath;
        }

        Refuse(check, "serve", bookmarkId, path);
        return null;
    }

    /// <summary>
    /// Makes one attempt at deleting a clip file, synchronously: for a request's delete, which calls it inside the store's
    /// write of the record (file first) and waits <see cref="RetryDelay"/> outside the store's lock before the
    /// next attempt while <see cref="ClipFileResult.WorthRetrying"/> says so. A failure is logged on the last attempt, or
    /// when it isn't worth retrying.
    /// </summary>
    /// <remarks>The record, and the ones in <paramref name="deletedWith"/>, still name the file in the store as the write sees
    /// it: they let go of it. Any other record that names it keeps it (<see cref="ClipFileOutcome.NamedByAnotherRecord"/>).</remarks>
    /// <param name="path">The record's path.</param>
    /// <param name="bookmarkId">The record, named in the log.</param>
    /// <param name="attempt">Which attempt this is, from 1 to <see cref="Attempts"/>.</param>
    /// <param name="deletedWith">The other records the same write lets go of their clip files (a bulk delete's), which may
    /// name the same file.</param>
    /// <returns>What happened; <see cref="ClipFileResult.IsGone"/> when the record may change.</returns>
    public ClipFileResult DeleteOnce(string? path, Guid bookmarkId, int attempt, IReadOnlySet<Guid>? deletedWith = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var lettingGo = new HashSet<Guid> { bookmarkId };
        if (deletedWith is not null)
        {
            lettingGo.UnionWith(deletedWith);
        }

        var result = Prepare(path, bookmarkId, allowPartial: false, lettingGo, out var check) ?? AttemptDelete(check, path!, bookmarkId, attempt, final: attempt >= Attempts);
        if (result.Outcome == ClipFileOutcome.Done)
        {
            // The renames that led to the file go with it (their paths can hold a note's words).
            ForgetRenamesOf(bookmarkId, result.Check.FullPath);
        }

        return result;
    }

    /// <summary>
    /// Answers a request's delete from a check made before it (a path the guard refuses, or a clip folder that can't
    /// be reached, is answered before the store's lock is taken), logged as a refused delete, without asking the file system
    /// again or touching the file: a folder that came back since that check must not have the file deleted outside the
    /// store's write of its record.
    /// </summary>
    /// <param name="check">The check, refused (<see cref="ClipPathCheck.IsRefused"/>) or unreachable
    /// (<see cref="ClipPathCheck.IsUnreachable"/>).</param>
    /// <param name="bookmarkId">The record, named in the log.</param>
    /// <param name="path">The record's path, for the Debug line.</param>
    /// <param name="operation">What was refused, for the log: <c>delete</c>, or <c>rename</c>.</param>
    /// <returns><see cref="ClipFileOutcome.Refused"/>, or <see cref="ClipFileOutcome.Failed"/> for a folder that can't be
    /// reached.</returns>
    /// <exception cref="ArgumentException">The check allows the path to be used.</exception>
    public ClipFileResult Refusal(ClipPathCheck check, Guid bookmarkId, string? path, string operation = "delete")
    {
        ArgumentNullException.ThrowIfNull(check);
        var refusal = Refuse(check, operation, bookmarkId, path) ?? throw new ArgumentException("The path may be used; there is no refusal to answer.", nameof(check));

        // The My Clips list judges the clip again at once: the row the page reads after the refusal shows
        // what this check found, not a verdict kept from before (a file read missing while its folder was there).
        _states?.Forget(check.FullPath);
        return refusal;
    }

    /// <summary>
    /// Deletes a clip file, with up to <see cref="Attempts"/> attempts <see cref="RetryDelay"/> apart; never one a record
    /// still names (<see cref="ClipFileOutcome.NamedByAnotherRecord"/>).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="bookmarkId">The record it belongs to, named in the log.</param>
    /// <param name="cancellationToken">Cancels waiting between attempts.</param>
    /// <returns>What happened.</returns>
    public async Task<ClipFileResult> DeleteAsync(string? path, Guid bookmarkId, CancellationToken cancellationToken)
    {
        var result = await DeleteCoreAsync(path, bookmarkId, Attempts, allowPartial: false, null, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ClipFileOutcome.Done)
        {
            ForgetRenamesOf(bookmarkId, result.Check.FullPath);
        }

        return result;
    }

    /// <summary>
    /// Deletes a clip job's temporary <c>.partial</c> file (a failed, cancelled or interrupted job's), as
    /// <see cref="DeleteAsync"/> does, but a file that can't be deleted is only logged: stale <c>.partial</c> files are the
    /// janitor's, and listing one in <c>PendingFileDeletes</c> could delete a later job's file of the same name.
    /// </summary>
    /// <param name="path">The temporary file.</param>
    /// <param name="bookmarkId">The job's bookmark, named in the log.</param>
    /// <returns>What happened.</returns>
    public Task<ClipFileResult> DeleteTemporaryAsync(string? path, Guid bookmarkId)
        => DeleteCoreAsync(path, bookmarkId, Attempts, allowPartial: true, null, CancellationToken.None);

    /// <summary>
    /// Deletes a stale <c>.partial</c> file the janitor found, as <see cref="DeleteTemporaryAsync"/> does: only a
    /// name <see cref="ClipNamePattern"/> gives a clip being written (any other name is refused and left alone, whatever the
    /// caller found), through the guard, with up to <see cref="Attempts"/> attempts. A file that can't be deleted is only
    /// logged and left for the next start; it is never listed in <c>PendingFileDeletes</c>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels waiting between attempts.</param>
    /// <returns>What happened.</returns>
    public Task<ClipFileResult> DeleteStalePartialAsync(string path, CancellationToken cancellationToken)
    {
        if (!ClipNamePattern.IsPartialFileName(Path.GetFileName(path)))
        {
            return Task.FromResult(Refuse(new ClipPathCheck(ClipPathVerdict.NotAClipFile, null, null), "delete", null, path)!);
        }

        return DeleteCoreAsync(path, null, Attempts, allowPartial: true, null, cancellationToken);
    }

    /// <summary>
    /// Whether a clip folder is there (<see cref="IClipPathProbe.FolderExists"/>): not an unplugged drive, a folder moved or
    /// removed, or a share that can't be reached.
    /// </summary>
    /// <param name="folder">The clip folder's full path.</param>
    /// <returns>Whether it is there.</returns>
    public bool FolderExists(string folder) => _fileSystem.FolderExists(folder);

    /// <summary>
    /// Looks at a recorded clip folder afresh: whether it is there, and what its marker
    /// file (<see cref="ClipRootMarker"/>) says with the token the store records for the folder
    /// (<see cref="ClipRootMarker.Judge"/>), the proof that it is the folder its clips were made in. A marker that can't be
    /// read counts as none (logged at Debug, with the folder), and a folder that can't be looked at as not there. Nothing is
    /// written.
    /// </summary>
    /// <param name="root">The clip folder, normalised (<see cref="ClipRootPaths.TryNormalize"/>).</param>
    /// <returns>What is there.</returns>
    public ClipRootState CheckRoot(string root) => CheckRoot(_store, _fileSystem, root, _logger);

    /// <summary>
    /// Looks at a recorded clip folder afresh, as <see cref="CheckRoot(string)"/> does, over a given store and file system
    /// (for <see cref="ClipStorageGuard"/>'s Diagnostics line).
    /// </summary>
    /// <param name="store">The store, for the folder's token.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="root">The clip folder, normalised.</param>
    /// <param name="logger">For the Debug line of a folder or marker that can't be read.</param>
    /// <returns>What is there.</returns>
    internal static ClipRootState CheckRoot(IBookmarkStore store, IClipFileSystem fileSystem, string root, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            if (!fileSystem.FolderExists(root))
            {
                return ClipRootState.Absent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not look at the clip folder {Folder}", root);
            return ClipRootState.Absent;
        }

        try
        {
            return ClipRootMarker.Judge(fileSystem.ReadRootMarker(root), root, store.GetClipRootToken(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read the marker file of the clip folder {Folder}", root);
            return ClipRootState.Unmarked;
        }
    }

    /// <summary>
    /// Whether a recorded clip folder is there and recognised as the one its clips were made in (<see cref="CheckRoot(string)"/>
    /// is <see cref="ClipRootState.Marked"/>, or <see cref="ClipRootState.Adoptable"/>: a marker that names it while the store
    /// records no token for it yet), so a clip file gone from it is gone (for the clip routes' deletes and Forget).
    /// </summary>
    /// <param name="root">The clip folder, normalised.</param>
    /// <returns>Whether it is the clips' own.</returns>
    public bool IsRecognisedRoot(string root) => CheckRoot(root) is ClipRootState.Marked or ClipRootState.Adoptable;

    /// <summary>
    /// Answers a user's delete or Forget of a clip whose file is <see cref="ClipPathVerdict.Missing"/> from a clip folder that
    /// isn't recognised as the clips' own (<see cref="IsRecognisedRoot"/> false): nothing is touched, the file isn't
    /// taken for gone, and the request answers 503 with <see cref="FolderUnrecognisedMessage"/> (<see cref="ToRequestError"/>);
    /// a bulk delete reports it as its folder unavailable. Logged without the path (a Debug line names it).
    /// </summary>
    /// <param name="check">The check that found the file missing.</param>
    /// <param name="bookmarkId">The record, named in the log.</param>
    /// <param name="path">The record's path, for the Debug line.</param>
    /// <returns><see cref="ClipFileOutcome.Failed"/> with <see cref="ClipPathCheck.RootUnrecognised"/>.</returns>
    public ClipFileResult Unrecognised(ClipPathCheck check, Guid bookmarkId, string? path)
    {
        ArgumentNullException.ThrowIfNull(check);
        _logger.LogWarning("Left a clip of bookmark {BookmarkId} as it is: its file isn't there, and its clip folder isn't recognised as the one its clips were made in (no marker file of its own)", bookmarkId);
        _logger.LogDebug("Left {Path} as it is: missing from an unrecognised clip folder", path);

        // As for Refusal: the list's row read after this answer says the folder isn't recognised, not the "missing" it may
        // have kept from before the folder changed (else Forget was offered for up to 30 s after the 503).
        _states?.Forget(check.FullPath);
        return new ClipFileResult(ClipFileOutcome.Failed, check with { RootUnrecognised = true }, Error: "The clip folder isn't recognised as the one its clips were made in.");
    }

    /// <summary>
    /// Counts the clips recorded in a clip folder (the records, live and tombstones, whose clip file lies in it as the guard
    /// sees it): how many, how many of their files are there, and how many are gone (not there, or
    /// their folder isn't; a clip being made is left out, its job deciding its file, as the sweep leaves it out). Asks the
    /// file system about each.
    /// </summary>
    /// <param name="root">The clip folder, normalised.</param>
    /// <returns>The counts.</returns>
    public ClipFolderClips CountClips(string root) => CountClips(_store, _fileSystem, root);

    /// <summary>
    /// Counts the clips recorded in a clip folder, as <see cref="CountClips(string)"/> does, over a given store and file system.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="root">The clip folder, normalised.</param>
    /// <returns>The counts.</returns>
    internal static ClipFolderClips CountClips(IBookmarkStore store, IClipPathProbe fileSystem, string root)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(root);
        int clips = 0, present = 0, gone = 0, marked = 0;
        var roots = store.ClipRoots;
        foreach (var record in store.GetAll())
        {
            if (record.Clip is not { FilePath: { Length: > 0 } path } clip)
            {
                continue;
            }

            var check = ClipFileGuard.Check(path, roots, fileSystem);
            if (check.Root is null || !ClipRootPaths.Comparer.Equals(check.Root, root))
            {
                continue;
            }

            clips++;
            if (check.Verdict == ClipPathVerdict.Exists)
            {
                present++;
            }
            else if (check.Verdict is ClipPathVerdict.Missing or ClipPathVerdict.FolderUnavailable && !BookmarkRecordRules.HasClipJob(record))
            {
                gone++;
                if (clip.MissingSinceUtc is not null)
                {
                    marked++;
                }
            }
        }

        return new ClipFolderClips(clips, present, gone, marked);
    }

    /// <summary>
    /// Gives a recorded clip folder that is there its marker file (<see cref="ClipRootMarker"/>) with a new token, and records
    /// the token, for the clip worker, the sweep's backfill and a confirmation
    /// (<see cref="ConfirmFolderAsync"/>), which decide when a folder may get one. A folder marked already is left as it is;
    /// one whose marker names it with a token while none is recorded has that token recorded and keeps its file (adopted). The
    /// file is written inside the store's write, under its lock, so two writers never leave a file and a record with different
    /// tokens; a store write that fails after it leaves a marker whose token isn't recorded, which the next look adopts while
    /// none is (and reads as another folder's otherwise, until its clips prove it again). A failure is a Warning without the
    /// folder (a Debug line names it) and changes nothing else; a folder that isn't recorded gets nothing.
    /// </summary>
    /// <param name="root">The clip folder, normalised.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done.</returns>
    public async Task<ClipRootMarking> WriteRootMarkerAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var token = ClipRootMarker.NewToken();
        var outcome = ClipRootMarking.Failed;
        try
        {
            await _store.UpdateDocumentAsync(
                document =>
                {
                    outcome = ClipRootMarking.Failed;
                    if (!document.ClipRoots.Contains(root, ClipRootPaths.Comparer))
                    {
                        return document;
                    }

                    var recorded = document.ClipRootTokens.GetValueOrDefault(root);
                    var (names, onDisk) = ClipRootMarker.Read(ReadMarkerOrNone(root), root);
                    if (names && onDisk is not null && (recorded is null || onDisk.Equals(recorded, StringComparison.OrdinalIgnoreCase)))
                    {
                        outcome = recorded is null ? ClipRootMarking.Adopted : ClipRootMarking.AlreadyMarked;
                        return recorded is null ? document.WithClipRootToken(root, onDisk) : document;
                    }

                    _fileSystem.WriteRootMarker(root, ClipRootMarker.Text(root, token));
                    outcome = ClipRootMarking.Written;
                    return document.WithClipRootToken(root, token);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginException)
        {
            _logger.LogWarning("Could not write the marker file ({File}) of a clip folder: {Error}", ClipRootMarker.FileName, ex.GetType().Name);
            _logger.LogDebug(ex, "Could not write the marker file of the clip folder {Folder}", root);
            return ClipRootMarking.Failed;
        }

        _logger.LogDebug("The marker file of the clip folder {Folder}: {Outcome}", root, outcome);
        if (outcome is ClipRootMarking.Written or ClipRootMarking.Adopted)
        {
            // The folder is recognised now: the list judges its clips again at once (a missing file is missing).
            _states?.ForgetUnder(root);
        }

        return outcome;
    }

    /// <summary>
    /// Gets the default clip folder (<c>clips</c> in the plugin's data folder) when it is recorded and nothing is at its path
    /// while the plugin's data folder is there: deleted, not away, since a folder inside a data
    /// folder that is there isn't on a drive of its own that could be unplugged. A link or anything else at its path doesn't
    /// count. Otherwise <c>null</c>, also without the plugin's paths.
    /// </summary>
    /// <returns>The folder, normalised, or <c>null</c>.</returns>
    public string? DeletedDefaultFolder()
    {
        if (_paths is null
            || !ClipRootPaths.TryNormalize(_paths.DefaultClipsDirectory, out var root)
            || !ClipRootPaths.TryNormalize(_paths.Root, out var data)
            || !_store.ClipRoots.Any(r => ClipRootPaths.TryNormalize(r, out var recorded) && ClipRootPaths.Comparer.Equals(recorded, root)))
        {
            return null;
        }

        try
        {
            return _fileSystem.FolderExists(data) && _fileSystem.GetAttributes(root) is null ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look at the default clip folder {Folder}", root);
            return null;
        }
    }

    /// <summary>
    /// Confirms that a recorded clip folder is the one its clips were made in, and that those of its clips whose file isn't
    /// there are gone: an admin's answer for a folder the missing-clip sweep leaves out ("This is the
    /// clip folder; its clips are gone"), and the default clip folder made again after it was deleted
    /// (<see cref="DeletedDefaultFolder"/>). The folder is made when it isn't there, given its marker file with a new token
    /// unless it has its own (<see cref="WriteRootMarkerAsync"/>), and each of its clips whose file isn't there, not one being
    /// made, is marked missing now (<c>ClipInfo.MissingSinceUtc</c>, on the lifecycle's clock), in one store write that leaves
    /// a clip changed since it was looked at as it is; the sweep and its retention then take them as any. A dry run only
    /// counts. Never deletes, moves or renames a file.
    /// </summary>
    /// <param name="root">The clip folder, normalised and recorded.</param>
    /// <param name="dryRun">Whether to only count.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was (or would be) done.</returns>
    public async Task<ClipFolderConfirmation> ConfirmFolderAsync(string root, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var existed = SafeFolderExists(root);
        var before = CountClips(root);
        if (dryRun)
        {
            return new ClipFolderConfirmation(root, existed, false, ClipRootMarking.Failed, before, before.Gone - before.Marked, null);
        }

        var created = false;
        if (!existed)
        {
            try
            {
                Directory.CreateDirectory(root);
                created = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.LogWarning("Could not make a clip folder again: {Error}", ex.GetType().Name);
                _logger.LogDebug(ex, "Could not make the clip folder {Folder} again", root);
                return new ClipFolderConfirmation(root, false, false, ClipRootMarking.Failed, before, 0, ClipFolderConfirmation.NotMadeMessage);
            }
        }

        var marking = await WriteRootMarkerAsync(root, cancellationToken).ConfigureAwait(false);
        if (marking == ClipRootMarking.Failed)
        {
            return new ClipFolderConfirmation(root, existed, created, marking, CountClips(root), 0, ClipFolderConfirmation.MarkerNotWrittenMessage);
        }

        // A fresh look at each of its clips: those whose file isn't there now are marked, each only while it is as looked at.
        var now = _clock.GetUtcNow().UtcDateTime;
        var seen = new Dictionary<Guid, ClipInfo>();
        foreach (var record in _store.GetAll())
        {
            if (record.Clip is { FilePath: { Length: > 0 } path, MissingSinceUtc: null } clip
                && !BookmarkRecordRules.HasClipJob(record)
                && Check(path) is { Verdict: ClipPathVerdict.Missing, Root: { } found }
                && ClipRootPaths.Comparer.Equals(found, root))
            {
                seen[record.Id] = clip;
            }
        }

        var newly = 0;
        if (seen.Count > 0)
        {
            await _store.UpdateDocumentAsync(
                document =>
                {
                    newly = 0;
                    var records = document.Bookmarks.Select(b =>
                    {
                        if (!seen.TryGetValue(b.Id, out var clip) || !Equals(b.Clip, clip))
                        {
                            return b;
                        }

                        newly++;
                        return b with { Clip = clip with { MissingSinceUtc = now } };
                    }).ToList();
                    return newly == 0 ? document : document with { Bookmarks = records };
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new ClipFolderConfirmation(root, existed, created, marking, CountClips(root), newly, null);
    }

    // A marker file's text for WriteRootMarkerAsync, or none when it can't be read.
    private string? ReadMarkerOrNone(string root)
    {
        try
        {
            return _fileSystem.ReadRootMarker(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the marker file of the clip folder {Folder}", root);
            return null;
        }
    }

    private bool SafeFolderExists(string folder)
    {
        try
        {
            return _fileSystem.FolderExists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look at the clip folder {Folder}", folder);
            return false;
        }
    }

    /// <summary>
    /// Lists the files in a clip folder and in the folders directly inside it (<see cref="IClipFileSystem.ListClipFolder"/>),
    /// for the janitor. Nothing is touched.
    /// </summary>
    /// <param name="folder">The clip folder's full path.</param>
    /// <returns>The files, read as they are enumerated.</returns>
    public IEnumerable<ClipFolderFile> ListClipFolder(string folder) => _fileSystem.ListClipFolder(folder);

    /// <summary>
    /// Deletes a clip file no user waits for (an automatic deletion): a remake's previous file, a job's discarded
    /// output, a deleted user's files under the <c>Delete</c> policy. A file in use, or one that can't be deleted or reached
    /// now, is listed in <c>PendingFileDeletes</c> for <see cref="RetryPendingDeletesAsync"/>; a path the guard
    /// refuses is logged and never listed. It is called once the record no longer names the file, so a file any record still
    /// names is kept (<see cref="ClipFileOutcome.NamedByAnotherRecord"/>, with a Warning) and never listed.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="bookmarkId">The record it belonged to, named in the log.</param>
    /// <param name="writer">The part of Bookmarks deleting it, which must be allowed to write the store
    /// (<see cref="BackgroundWriteGate"/>) for the file to be listed.</param>
    /// <param name="cancellationToken">Cancels waiting between attempts and for the store.</param>
    /// <returns>What happened, with <see cref="ClipFileResult.Queued"/> when the file was listed.</returns>
    public async Task<ClipFileResult> DeleteOrQueueAsync(string? path, Guid bookmarkId, BackgroundWriter writer, CancellationToken cancellationToken)
    {
        var result = await DeleteCoreAsync(path, bookmarkId, Attempts, allowPartial: false, null, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ClipFileOutcome.Done)
        {
            // The renames that led to the file go with it.
            ForgetRenamesOf(bookmarkId, result.Check.FullPath);
        }

        if (result.Outcome is not (ClipFileOutcome.InUse or ClipFileOutcome.Failed) || result.Check.FullPath is not { } full || result.Check.Root is null)
        {
            return result;
        }

        if (!_gate.MayWrite(writer))
        {
            return result;
        }

        bool listed;
        try
        {
            listed = await _store.AddPendingFileDeleteAsync(full, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginException ex)
        {
            _logger.LogWarning(ex, "Could not list a clip file of bookmark {BookmarkId} to be deleted later; it stays in the clip folder", bookmarkId);
            return result;
        }

        if (!listed)
        {
            _logger.LogWarning(
                "A clip file of bookmark {BookmarkId} couldn't be deleted, and the list of files to delete later is full ({Max}); it stays in the clip folder",
                bookmarkId,
                BookmarkStoreDocument.MaxPendingFileDeletes);
            return result;
        }

        _logger.LogInformation("A clip file of bookmark {BookmarkId} couldn't be deleted now ({Outcome}); it is deleted later", bookmarkId, result.Outcome);
        ForgetRenamesOf(bookmarkId, full);
        return result with { Queued = true };
    }

    /// <summary>
    /// Moves a clip file within the clip folders, never over an existing file: up to <see cref="Attempts"/> attempts
    /// <see cref="RetryDelay"/> apart. The worker moves a finished clip from its <c>.partial</c> into place with it; the
    /// My Clips page's <i>Rename file</i> (1.2.0) renames with it.
    /// </summary>
    /// <param name="source">The file (a clip's <c>.partial</c> is accepted).</param>
    /// <param name="destination">Its new path, which must not exist.</param>
    /// <param name="bookmarkId">The record, named in the log.</param>
    /// <param name="cancellationToken">Cancels waiting between attempts.</param>
    /// <returns>What happened: <see cref="ClipFileOutcome.Done"/>, or the source <see cref="ClipFileOutcome.Missing"/>,
    /// <see cref="ClipFileOutcome.InUse"/>, <see cref="ClipFileOutcome.Refused"/> (either path) or
    /// <see cref="ClipFileOutcome.Failed"/> (the destination exists, among others).</returns>
    public async Task<ClipFileResult> MoveAsync(string source, string destination, Guid bookmarkId, CancellationToken cancellationToken)
    {
        var from = Check(source, allowPartial: true);
        if (from.Verdict == ClipPathVerdict.Missing)
        {
            return new ClipFileResult(ClipFileOutcome.Missing, from, Error: "The file to move isn't there.");
        }

        if (Refuse(from, "move", bookmarkId, source) is { } refusedFrom)
        {
            return refusedFrom;
        }

        var to = Check(destination);
        if (to.Verdict == ClipPathVerdict.Exists)
        {
            return Fail(new ClipFileResult(ClipFileOutcome.Failed, to, Error: "A file with the clip's name exists already."), "move", bookmarkId, null, destination);
        }

        if (to.Verdict != ClipPathVerdict.Missing && Refuse(to, "move", bookmarkId, destination) is { } refusedTo)
        {
            return refusedTo;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _fileSystem.MoveFile(from.FullPath!, to.FullPath!);

                // Both paths changed: the list judges them again at once (a clip made again under the name of a file that had
                // gone is there now).
                _states?.Forget(from.FullPath);
                _states?.Forget(to.FullPath);
                return new ClipFileResult(ClipFileOutcome.Done, from, attempt);
            }
            catch (FileNotFoundException)
            {
                return new ClipFileResult(ClipFileOutcome.Missing, from, attempt, "The file to move isn't there.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts && IsWorthRetrying(ex))
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var error = ex.HResult is FileExists or AlreadyExists
                    ? "A file with the clip's name exists already."
                    : ClipFiles.RedactForLog(ex.Message, from.FullPath, to.FullPath, source, destination);
                return Fail(new ClipFileResult(IsInUse(ex) ? ClipFileOutcome.InUse : ClipFileOutcome.Failed, from, attempt, error, HResult: ex.HResult), "move", bookmarkId, ex, source);
            }
        }
    }

    /// <summary>
    /// Replaces a clip's own file with its remake, finished in its <c>.partial</c> (since Bookmarks 1.2.1: a remake keeps its
    /// file's name, <see cref="ReusableOwnPath"/>): the <c>.partial</c> is moved over the file, or into place when the file
    /// is gone, up to <see cref="Attempts"/> attempts <see cref="RetryDelay"/> apart, as <see cref="MoveAsync"/>. A file
    /// still in use after that (watched, downloaded) is <see cref="ClipFileOutcome.InUse"/>, and the worker puts the clip
    /// under a fresh name instead. Once replaced, the renames' history of the old file is forgotten.
    /// </summary>
    /// <param name="source">The clip's <c>.partial</c>.</param>
    /// <param name="destination">The clip's own file.</param>
    /// <param name="bookmarkId">The record, named in the log.</param>
    /// <param name="cancellationToken">Cancels waiting between attempts.</param>
    /// <returns>What happened, as <see cref="MoveAsync"/> says it.</returns>
    public async Task<ClipFileResult> ReplaceAsync(string source, string destination, Guid bookmarkId, CancellationToken cancellationToken)
    {
        var from = Check(source, allowPartial: true);
        if (from.Verdict == ClipPathVerdict.Missing)
        {
            return new ClipFileResult(ClipFileOutcome.Missing, from, Error: "The file to move isn't there.");
        }

        if (Refuse(from, "replace", bookmarkId, source) is { } refusedFrom)
        {
            return refusedFrom;
        }

        var to = Check(destination);
        if (to.Verdict is not (ClipPathVerdict.Exists or ClipPathVerdict.Missing) && Refuse(to, "replace", bookmarkId, destination) is { } refusedTo)
        {
            return refusedTo;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _fileSystem.ReplaceFile(from.FullPath!, to.FullPath!);
                _states?.Forget(from.FullPath);
                _states?.Forget(to.FullPath);
                ForgetRenamesOf(bookmarkId, to.FullPath);
                return new ClipFileResult(ClipFileOutcome.Done, from, attempt);
            }
            catch (FileNotFoundException)
            {
                return new ClipFileResult(ClipFileOutcome.Missing, from, attempt, "The file to move isn't there.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts && IsWorthRetrying(ex))
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var error = ClipFiles.RedactForLog(ex.Message, from.FullPath, to.FullPath, source, destination);
                _logger.LogDebug(ex, "The remade clip of bookmark {BookmarkId} couldn't replace its file", bookmarkId);
                return new ClipFileResult(IsInUse(ex) ? ClipFileOutcome.InUse : ClipFileOutcome.Failed, from, attempt, error, HResult: ex.HResult);
            }
        }
    }

    /// <summary>
    /// Tries once more to delete every file in <c>PendingFileDeletes</c>: at startup
    /// (<see cref="PartialFileJanitor"/>) and daily (the maintenance task). An entry is dropped when its file is deleted
    /// or already gone from a clip folder that is there, when the guard refuses its path (nothing is touched), or when a
    /// record holds its path again (a new clip written under the freed name); any other entry, one in a clip folder that
    /// isn't there or can't be read included, stays with one more attempt counted. The files are handled
    /// outside the store's lock, and one write then applies the outcomes; entries added meanwhile are left as they are.
    /// </summary>
    /// <param name="writer">The part of Bookmarks retrying, which must be allowed to write the store.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What the pass did.</returns>
    public async Task<PendingFileDeleteRetry> RetryPendingDeletesAsync(BackgroundWriter writer, CancellationToken cancellationToken)
    {
        if (!_gate.MayWrite(writer) || _store.PendingFileDeletes.Count == 0)
        {
            return PendingFileDeleteRetry.None;
        }

        var referenced = ReferencedPaths();
        var drop = new Dictionary<string, bool>(ClipRootPaths.Comparer);
        int deleted = 0, gone = 0, refused = 0, stillReferenced = 0, kept = 0;
        foreach (var entry in _store.PendingFileDeletes)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Path) || drop.ContainsKey(entry.Path))
            {
                continue;
            }

            var check = Check(entry.Path);
            if (check.IsRefused)
            {
                refused++;
                drop[entry.Path] = true;
                _logger.LogWarning("Dropped a file waiting to be deleted: it isn't a clip file in the clip folders ({Verdict}); nothing was deleted", check.Verdict);
                _logger.LogDebug("Dropped {Path} from the files waiting to be deleted ({Verdict})", entry.Path, check.Verdict);
            }
            else if (check.FullPath is { } full && referenced.Contains(full))
            {
                stillReferenced++;
                drop[entry.Path] = true;
            }
            else if (check.Verdict == ClipPathVerdict.Missing)
            {
                gone++;
                drop[entry.Path] = true;
            }
            else
            {
                var result = check.IsUnreachable
                    ? Fail(new ClipFileResult(ClipFileOutcome.Failed, check, Error: DescribeUnreachable(check, entry.Path)), "delete", null, check.Error, entry.Path)
                    : AttemptDelete(check, entry.Path, null, attempt: 1, final: true);
                if (result.Outcome == ClipFileOutcome.Done)
                {
                    deleted++;
                }
                else if (result.IsGone)
                {
                    gone++;
                }
                else
                {
                    kept++;
                }

                drop[entry.Path] = result.IsGone;
            }
        }

        await _store.UpdatePendingFileDeletesAsync(
            e => !drop.TryGetValue(e.Path, out var isGone) ? e : isGone ? null : e with { Attempts = e.Attempts + 1 },
            cancellationToken).ConfigureAwait(false);

        var retry = new PendingFileDeleteRetry(deleted, gone, refused, stillReferenced, kept);
        _logger.LogInformation(
            "Retried the clip files waiting to be deleted: {Deleted} deleted, {Gone} already gone, {Refused} outside the clip folders and {Referenced} in use by a clip again (dropped), {Kept} still waiting",
            retry.Deleted,
            retry.Gone,
            retry.Refused,
            retry.Referenced,
            retry.Kept);
        return retry;
    }

    // Sharing and lock violations pass; so does "access denied", which Windows also answers for a file whose delete is
    // pending until another handle closes.
    private static bool IsWorthRetrying(Exception exception) => IsInUse(exception) || exception is UnauthorizedAccessException;

    // Why a path couldn't be used, without the file's path or name: its clip folder isn't there, or the error's message.
    private static string DescribeUnreachable(ClipPathCheck check, string? path)
        => check.Verdict == ClipPathVerdict.FolderUnavailable
            ? "The clip folder isn't there (an unplugged drive, or a folder that was moved or removed)."
            : check.Error is null ? "The clip file can't be reached." : ClipFiles.RedactForLog(check.Error.Message, check.FullPath, path);

    private async Task<ClipFileResult> DeleteCoreAsync(string? path, Guid? bookmarkId, int attempts, bool allowPartial, IReadOnlySet<Guid>? lettingGo, CancellationToken cancellationToken)
    {
        if (Prepare(path, bookmarkId, allowPartial, lettingGo, out var check) is { } early)
        {
            return early;
        }

        for (var attempt = 1; ; attempt++)
        {
            var result = AttemptDelete(check, path!, bookmarkId, attempt, final: attempt >= attempts);
            if (!result.WorthRetrying || attempt >= attempts)
            {
                return result;
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    // The guard's part of a delete: the result when the file is gone already, may not be touched, or can't be reached (each
    // logged as such), or when a record other than those letting go of it (null: any record) still names it (Bookmarks
    // 1.1.3, with a Warning); null when the file may be deleted.
    private ClipFileResult? Prepare(string? path, Guid? bookmarkId, bool allowPartial, IReadOnlySet<Guid>? lettingGo, out ClipPathCheck check)
    {
        if (string.IsNullOrEmpty(path))
        {
            // No file (a job that failed before it planned one): nothing to delete.
            check = new ClipPathCheck(ClipPathVerdict.NotAPath, null, null);
            return new ClipFileResult(ClipFileOutcome.Missing, check);
        }

        check = Check(path, allowPartial);
        if (check.Verdict == ClipPathVerdict.Missing)
        {
            _states?.Forget(check.FullPath);
            return new ClipFileResult(ClipFileOutcome.Missing, check);
        }

        if (Refuse(check, "delete", bookmarkId, path) is { } refused)
        {
            return refused;
        }

        if (NamedBy(check.FullPath!, lettingGo) is not { } other)
        {
            return null;
        }

        _logger.LogWarning(
            "Kept a clip file of bookmark {BookmarkId} instead of deleting it: bookmark {OtherId} still names it. Only the reference to it was removed",
            bookmarkId?.ToString() ?? "(none)",
            other.Id);
        _logger.LogDebug("Kept {Path}: bookmark {OtherId} names it", path, other.Id);
        return new ClipFileResult(ClipFileOutcome.NamedByAnotherRecord, check);
    }

    /// <summary>
    /// Gets a record other than <paramref name="self"/> that names a clip file's full path (since 1.2.0: a rename
    /// must not move a file away from another record that still names it, as two records could
    /// before 1.1.3), compared as clip paths are; called under the store's lock, so the records are the ones the write sees.
    /// </summary>
    /// <param name="fullPath">The clip file's full path (as <see cref="Check"/> gives it).</param>
    /// <param name="self">The record whose file it is.</param>
    /// <returns>The other record, or <c>null</c>.</returns>
    public Bookmark? NamedByAnother(string fullPath, Guid self)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        return NamedBy(fullPath, new HashSet<Guid> { self });
    }

    // The record, other than those letting go of it (null: none), that names a clip file's full path, or null (Bookmarks
    // 1.1.3). The file names are compared first, so a long list costs little.
    private Bookmark? NamedBy(string fullPath, IReadOnlySet<Guid>? lettingGo)
    {
        var name = Path.GetFileName(fullPath.AsSpan());
        foreach (var record in _store.GetClipRecords(null))
        {
            if (record.Clip?.FilePath is not { Length: > 0 } path
                || lettingGo?.Contains(record.Id) == true
                || !Path.GetFileName(path.AsSpan()).Equals(name, ClipRootPaths.Comparison))
            {
                continue;
            }

            if (ClipFileGuard.TryGetFullPath(path, out var full) && ClipRootPaths.Comparer.Equals(full, fullPath))
            {
                return record;
            }
        }

        return null;
    }

    // One delete of a file the guard accepted. A failure worth another attempt is logged only on the final one. A file gone
    // (deleted now, or not there) is judged again at once by the list.
    private ClipFileResult AttemptDelete(ClipPathCheck check, string path, Guid? bookmarkId, int attempt, bool final)
    {
        try
        {
            _fileSystem.DeleteFile(check.FullPath!);
            _states?.Forget(check.FullPath);
            return new ClipFileResult(ClipFileOutcome.Done, check, attempt);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _states?.Forget(check.FullPath);
            return new ClipFileResult(ClipFileOutcome.Missing, check, attempt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var worthRetrying = IsWorthRetrying(ex);
            var result = new ClipFileResult(
                IsInUse(ex) ? ClipFileOutcome.InUse : ClipFileOutcome.Failed,
                check,
                attempt,
                ClipFiles.RedactForLog(ex.Message, check.FullPath, path),
                WorthRetrying: worthRetrying);
            return final || !worthRetrying ? Fail(result, "delete", bookmarkId, ex, path) : result;
        }
    }

    // The result for a path that may not be touched (Refused) or couldn't be inspected, or whose clip folder isn't there
    // (Failed), logged; null when the path may be used.
    private ClipFileResult? Refuse(ClipPathCheck check, string operation, Guid? bookmarkId, string? path)
    {
        if (check.IsUnreachable)
        {
            return Fail(new ClipFileResult(ClipFileOutcome.Failed, check, Error: DescribeUnreachable(check, path)), operation, bookmarkId, check.Error, path);
        }

        if (!check.IsRefused)
        {
            return null;
        }

        _logger.LogWarning(
            "Refused to {Operation} a clip file of bookmark {BookmarkId}: {Verdict}; nothing was touched",
            operation,
            bookmarkId?.ToString() ?? "(none)",
            check.Verdict);
        _logger.LogDebug("Refused to {Operation} {Path}: {Verdict}", operation, path, check.Verdict);
        return new ClipFileResult(ClipFileOutcome.Refused, check);
    }

    private ClipFileResult Fail(ClipFileResult result, string operation, Guid? bookmarkId, Exception? exception, string? path)
    {
        var id = bookmarkId?.ToString() ?? "(none)";
        if (result.Outcome == ClipFileOutcome.InUse)
        {
            _logger.LogInformation("Could not {Operation} a clip file of bookmark {BookmarkId}: it is in use ({Attempts} attempts)", operation, id, result.Attempts);
        }
        else
        {
            _logger.LogWarning("Could not {Operation} a clip file of bookmark {BookmarkId}: {Error}", operation, id, result.Error);
        }

        // The exception's message names the file, so it is logged whole at Debug only.
        _logger.LogDebug(exception, "Could not {Operation} {Path}", operation, path);
        return result;
    }

    private HashSet<string> ReferencedPaths()
    {
        var paths = new HashSet<string>(ClipRootPaths.Comparer);
        foreach (var record in _store.GetAll())
        {
            if (record.Clip?.FilePath is { Length: > 0 } path)
            {
                try
                {
                    paths.Add(Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Not a path: nothing can refer to it.
                }
            }
        }

        return paths;
    }
}
