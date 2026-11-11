using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The file-operation tests for <see cref="ClipFileOps"/>: deletes and moves act only on what
/// the guard accepts; a sharing violation (<c>0x80070020</c>) or lock violation (<c>0x80070021</c>) after three attempts is
/// "in use" (409 for a request); a missing file counts as deleted, but not in a clip folder that isn't there (an unplugged
/// drive); an automatic delete lists a file in use in <c>PendingFileDeletes</c>, and the retry drops what is
/// gone, refused or used by a clip again. The faults come from <see cref="FakeClipFileSystem"/>; the last tests hold real
/// files open on Windows.
/// </summary>
public sealed class ClipFileOpsTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly BookmarkStore _store;
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly ListLogger _logger = new();
    private readonly ClipFileOps _files;

    public ClipFileOpsTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(_root);
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("bookmarks.json"), _root);
        _files = ClipFileSupport.Ops(_store, _logger, _fileSystem);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delete_OfAClipFile_DeletesIt()
    {
        var file = Clip("a.mp4");

        var result = await _files.DeleteAsync(file, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Done, result.Outcome);
        Assert.True(result.IsGone);
        Assert.Equal(1, result.Attempts);
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task Delete_OfAFileThatIsntThere_Succeeds_WithoutAskingTheFileSystem()
    {
        // Deleting a file that no longer exists counts as success.
        var result = await _files.DeleteAsync(Path.Combine(_root, "alice", "gone.mkv"), Guid.NewGuid(), Ct);
        var nothing = await _files.DeleteAsync(null, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Missing, result.Outcome);
        Assert.True(result.IsGone);
        Assert.Equal(ClipFileOutcome.Missing, nothing.Outcome);
        Assert.Empty(_fileSystem.Deletes);
    }

    [Theory]
    [InlineData("library/Heat (1995).mkv", "OutsideClipFolders")]
    [InlineData("clips2/Heat (1995).mkv", "OutsideClipFolders")]
    [InlineData("clips/alice/notes.txt", "NotAClipFile")]
    [InlineData("bookmarks.json", "NotAClipFile")]
    public async Task Delete_OfAPathTheGuardRefuses_TouchesNothing(string relative, string expected)
    {
        var verdict = Enum.Parse<ClipPathVerdict>(expected);
        var file = relative == "bookmarks.json" ? _directory.Combine(relative) : _directory.CreateFile(relative);
        var id = Guid.NewGuid();

        var result = await _files.DeleteAsync(file, id, Ct);

        Assert.Equal(ClipFileOutcome.Refused, result.Outcome);
        Assert.Equal(verdict, result.Check.Verdict);
        Assert.True(File.Exists(file));
        Assert.Empty(_fileSystem.Deletes);
        Assert.Equal($"Refused to delete a clip file of bookmark {id}: {verdict}; nothing was touched", Assert.Single(_logger.At(LogLevel.Warning)).Message);
    }

    [Fact]
    public async Task Delete_ThroughAReparsePoint_TouchesNothing()
    {
        var file = Clip("a.mp4");
        _fileSystem.ReparsePoints.Add(Path.Combine(_root, "alice"));

        var result = await _files.DeleteAsync(file, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Refused, result.Outcome);
        Assert.Equal(ClipPathVerdict.ReparsePoint, result.Check.Verdict);
        Assert.True(File.Exists(file));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_OfAFileInUse_TriesThreeTimes_ThenIsInUse_AndTheFileStays(bool lockViolation)
    {
        var file = Clip("a.mp4");
        var id = Guid.NewGuid();
        _fileSystem.DeleteFailure = (p, _) => FakeClipFileSystem.InUse(p, lockViolation);

        var result = await _files.DeleteAsync(file, id, Ct);

        Assert.Equal(ClipFileOutcome.InUse, result.Outcome);
        Assert.Equal(ClipFileOps.Attempts, result.Attempts);
        Assert.Equal(3, _fileSystem.Deletes.Count);
        Assert.True(File.Exists(file));
        Assert.False(result.IsGone);
        Assert.Equal($"Could not delete a clip file of bookmark {id}: it is in use (3 attempts)", Assert.Single(_logger.At(LogLevel.Information)).Message);
        var error = ClipFileOps.ToRequestError(result);
        Assert.Equal(PluginErrorKind.Conflict, error.Kind);
        Assert.Equal(ClipFileOps.InUseMessage, error.Message);
    }

    [Fact]
    public async Task Delete_OfAFileInUseOnlyBriefly_SucceedsOnALaterAttempt()
    {
        var file = Clip("a.mp4");
        _fileSystem.DeleteFailure = (p, attempt) => attempt < 3 ? FakeClipFileSystem.InUse(p) : null;

        var result = await _files.DeleteAsync(file, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Done, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task Delete_ThatFailsForAnotherReason_IsFailed_AtOnce()
    {
        var file = Clip("a.mp4");
        _fileSystem.DeleteFailure = (p, _) => new IOException($"The media is write protected: '{p}'", unchecked((int)0x80070013));

        var result = await _files.DeleteAsync(file, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal("The media is write protected: '<clip file>'", result.Error);
        Assert.True(File.Exists(file));
        Assert.Equal(PluginErrorKind.Unavailable, ClipFileOps.ToRequestError(result).Kind);
    }

    [Fact]
    public async Task Delete_WithAccessDenied_IsTriedAgain_ThenFailed()
    {
        // Windows also answers "access denied" while another handle keeps a file whose delete is pending.
        var file = Clip("a.mp4");
        _fileSystem.DeleteFailure = (_, _) => new UnauthorizedAccessException("Access to the path is denied.");

        var result = await _files.DeleteAsync(file, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.True(result.WorthRetrying);
    }

    [Fact]
    public async Task Delete_OfAPathThatCantBeInspected_IsFailed_WithoutAskingToDelete()
    {
        var file = Clip("a.mp4");
        _fileSystem.AttributesFailure = _ => new IOException("The device is not ready.");

        var result = await _files.DeleteAsync(file, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Failed, result.Outcome);
        Assert.Equal(ClipPathVerdict.Unreadable, result.Check.Verdict);
        Assert.Equal("The device is not ready.", result.Error);
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public void DeleteOnce_LogsAFailure_OnlyOnTheLastAttempt()
    {
        // The manager's request delete: one attempt per write of the record, the pauses outside the store's lock.
        var file = Clip("a.mp4");
        _fileSystem.HoldOpen(file);
        var id = Guid.NewGuid();

        var first = _files.DeleteOnce(file, id, 1);
        var second = _files.DeleteOnce(file, id, 2);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
        var last = _files.DeleteOnce(file, id, 3);

        Assert.All([first, second, last], r => Assert.Equal(ClipFileOutcome.InUse, r.Outcome));
        Assert.All([first, second], r => Assert.True(r.WorthRetrying));
        Assert.Equal(3, last.Attempts);
        Assert.Single(_logger.At(LogLevel.Information));
        Assert.Equal(ClipFileOps.DefaultRetryDelay, new ClipFileOps(_store, new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance), new ClipStateCache(_store), BookmarkPaths.ForRoot(Path.GetTempPath()), NullLogger<ClipFileOps>.Instance, TimeProvider.System).RetryDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), ClipFileOps.DefaultRetryDelay);
    }

    [Fact]
    public void ToRequestError_AnswersAUser404_AndAnAdmin409WithTheReason()
    {
        var check = new ClipPathCheck(ClipPathVerdict.OutsideClipFolders, @"C:\elsewhere\x.mp4", null);
        var refused = new ClipFileResult(ClipFileOutcome.Refused, check);

        var user = ClipFileOps.ToRequestError(refused);
        var admin = ClipFileOps.ToRequestError(refused, forAdmin: true);

        Assert.Equal(PluginErrorKind.NotFound, user.Kind);
        Assert.Equal(ClipFileOps.NotInClipFoldersMessage, user.Message);
        Assert.Equal(PluginErrorKind.Conflict, admin.Kind);
        Assert.Equal(ClipFileOps.OutsideClipFoldersMessage, admin.Message);
        Assert.Equal(PluginErrorKind.Conflict, ClipFileOps.ToRequestError(refused with { Outcome = ClipFileOutcome.InUse }, forAdmin: true).Kind);
        Assert.Throws<ArgumentException>(() => ClipFileOps.ToRequestError(refused with { Outcome = ClipFileOutcome.Done }));
        Assert.Throws<ArgumentException>(() => ClipFileOps.ToRequestError(refused with { Outcome = ClipFileOutcome.Missing }));
        Assert.DoesNotContain("elsewhere", user.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_AnswersFromTheCheckItIsGiven_AndNeverLooksAgainOrTouchesTheFile()
    {
        // Found in testing: a request's check before the store's lock found the clip folder unplugged, and the answer came from
        // a second look (DeleteOnce), which found the folder back and deleted the file outside the store's write. The refusal
        // is that first check's, logged as a refused delete; the file is never asked for or touched again.
        var file = Clip("a.mp4");
        var id = Guid.NewGuid();
        var offline = new ClipPathCheck(ClipPathVerdict.FolderUnavailable, file, _root);
        var unreadable = new ClipPathCheck(ClipPathVerdict.Unreadable, file, _root, new IOException("The device is not ready."));
        var outside = new ClipPathCheck(ClipPathVerdict.OutsideClipFolders, file, null);

        var unavailable = _files.Refusal(offline, id, file);
        var failed = _files.Refusal(unreadable, id, file);
        var refused = _files.Refusal(outside, id, file);

        Assert.Equal((ClipFileOutcome.Failed, 0), (unavailable.Outcome, unavailable.Attempts));
        Assert.Same(offline, unavailable.Check);
        Assert.Equal(ClipFileOps.FolderUnavailableMessage, ClipFileOps.ToRequestError(unavailable).Message);
        Assert.Equal(ClipFileOutcome.Failed, failed.Outcome);
        Assert.Equal(ClipFileOps.FailedMessage, ClipFileOps.ToRequestError(failed).Message);
        Assert.Equal(ClipFileOutcome.Refused, refused.Outcome);
        Assert.Equal(PluginErrorKind.NotFound, ClipFileOps.ToRequestError(refused).Kind);
        Assert.Empty(_fileSystem.Deletes);
        Assert.True(File.Exists(file));
        Assert.Contains($"Could not delete a clip file of bookmark {id}: The clip folder isn't there (an unplugged drive, or a folder that was moved or removed).", _logger.At(LogLevel.Warning).Select(e => e.Message));
        Assert.Contains($"Refused to delete a clip file of bookmark {id}: OutsideClipFolders; nothing was touched", _logger.At(LogLevel.Warning).Select(e => e.Message));

        // A check that lets the path be used has no refusal to answer.
        Assert.Throws<ArgumentException>(() => _files.Refusal(new ClipPathCheck(ClipPathVerdict.Exists, file, _root), id, file));
        Assert.Throws<ArgumentException>(() => _files.Refusal(new ClipPathCheck(ClipPathVerdict.Missing, file, _root), id, file));
    }

    [Fact]
    public async Task DeleteOrQueue_OfAFileInUse_ListsItOnce_CountingAttempts()
    {
        var file = Clip("Heat (1995) - Bank job [00.01.00-00.01.30].mp4");
        _fileSystem.HoldOpen(file);
        var id = Guid.NewGuid();

        var first = await _files.DeleteOrQueueAsync(file, id, BackgroundWriter.ClipWorker, Ct);
        var again = await _files.DeleteOrQueueAsync(file, id, BackgroundWriter.ClipWorker, Ct);

        Assert.True(first.Queued);
        Assert.True(again.Queued);
        var entry = Assert.Single(_store.PendingFileDeletes);
        Assert.Equal(file, entry.Path);
        Assert.Equal(2, entry.Attempts);
        Assert.True(File.Exists(file));
        Assert.Contains($"A clip file of bookmark {id} couldn't be deleted now (InUse); it is deleted later", _logger.At(LogLevel.Information).Select(e => e.Message));
    }

    [Fact]
    public async Task DeleteOrQueue_ListsTheFullPath()
    {
        var file = Clip("a.mkv");
        _fileSystem.HoldOpen(file);
        var roundabout = Path.Combine(_root, "alice", "..", "alice", "a.mkv");

        await _files.DeleteOrQueueAsync(roundabout, Guid.NewGuid(), BackgroundWriter.ClipWorker, Ct);

        Assert.Equal(file, Assert.Single(_store.PendingFileDeletes).Path);
    }

    [Fact]
    public async Task DeleteOrQueue_OfWhatIsGoneOrRefused_ListsNothing()
    {
        var outside = _directory.CreateFile(Path.Combine("library", "Heat (1995).mkv"));
        _fileSystem.HoldOpen(outside);

        var refused = await _files.DeleteOrQueueAsync(outside, Guid.NewGuid(), BackgroundWriter.ClipWorker, Ct);
        var gone = await _files.DeleteOrQueueAsync(Path.Combine(_root, "gone.mp4"), Guid.NewGuid(), BackgroundWriter.ClipWorker, Ct);
        var deleted = await _files.DeleteOrQueueAsync(Clip("a.mp4"), Guid.NewGuid(), BackgroundWriter.ClipWorker, Ct);

        Assert.Equal(ClipFileOutcome.Refused, refused.Outcome);
        Assert.Equal(ClipFileOutcome.Missing, gone.Outcome);
        Assert.Equal(ClipFileOutcome.Done, deleted.Outcome);
        Assert.All([refused, gone, deleted], r => Assert.False(r.Queued));
        Assert.Empty(_store.PendingFileDeletes);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task DeleteOrQueue_OfAFileThatCantBeDeletedOtherwise_ListsItToo()
    {
        var file = Clip("a.mp4");
        _fileSystem.DeleteFailure = (_, _) => new IOException("The request failed.", unchecked((int)0x8007001F));

        var result = await _files.DeleteOrQueueAsync(file, Guid.NewGuid(), BackgroundWriter.ClipWorker, Ct);

        Assert.Equal(ClipFileOutcome.Failed, result.Outcome);
        Assert.True(result.Queued);
        Assert.Equal(file, Assert.Single(_store.PendingFileDeletes).Path);
    }

    [Fact]
    public async Task DeleteOrQueue_WhenTheListIsFull_LeavesTheFile_AndWarns()
    {
        await _store.UpdateDocumentAsync(
            d => d with
            {
                PendingFileDeletes = Enumerable.Range(0, BookmarkStoreDocument.MaxPendingFileDeletes)
                    .Select(i => new PendingFileDelete { Path = Path.Combine(_root, $"old-{i}.mp4"), SinceUtc = DateTime.UtcNow, Attempts = 1 })
                    .ToList()
            },
            Ct);
        var file = Clip("a.mp4");
        _fileSystem.HoldOpen(file);
        var id = Guid.NewGuid();

        var result = await _files.DeleteOrQueueAsync(file, id, BackgroundWriter.ClipWorker, Ct);

        Assert.False(result.Queued);
        Assert.Equal(BookmarkStoreDocument.MaxPendingFileDeletes, _store.PendingFileDeletes.Count);
        Assert.DoesNotContain(_store.PendingFileDeletes, e => e.Path == file);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.StartsWith($"A clip file of bookmark {id} couldn't be deleted, and the list of files to delete later is full (1000)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAutomaticOperations_WhileTheDataIsReadOnly_WriteNothing()
    {
        // A newer version's file: the gate keeps the automatic writers idle, so nothing is listed or dropped.
        using var directory = new TempDirectory();
        var root = directory.Combine("clips");
        var file = directory.CreateFile(Path.Combine("clips", "a.mp4"));
        var storeFile = directory.Combine("bookmarks.json");
        var newer = $$"""{"Version":3,"ClipRoots":[{{System.Text.Json.JsonSerializer.Serialize(root)}}],"PendingFileDeletes":[{"Path":{{System.Text.Json.JsonSerializer.Serialize(Path.Combine(root, "gone.mp4"))}},"SinceUtc":"2026-09-30T00:00:00Z","Attempts":1}],"Bookmarks":[]}""";
        await File.WriteAllTextAsync(storeFile, newer, Ct);
        using var store = BookmarkStore.Create(storeFile, NullLogger.Instance);
        var fileSystem = new FakeClipFileSystem();
        fileSystem.HoldOpen(file);
        var files = ClipFileSupport.Ops(store, fileSystem: fileSystem);

        var result = await files.DeleteOrQueueAsync(file, Guid.NewGuid(), BackgroundWriter.ClipWorker, Ct);
        var retry = await files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct);

        Assert.True(store.IsReadOnly);
        Assert.Equal(ClipFileOutcome.InUse, result.Outcome);
        Assert.False(result.Queued);
        Assert.Same(PendingFileDeleteRetry.None, retry);
        Assert.Equal(newer, await File.ReadAllTextAsync(storeFile, Ct));
    }

    [Fact]
    public async Task DeleteTemporary_DeletesAPartialFile_AndNeverListsOneInUse()
    {
        // A later job may reserve the same .partial name, so a stale one is the janitor's, never a pending delete.
        var partial = Clip("a.mp4.partial");
        var held = Clip("b.mp4.partial");
        _fileSystem.HoldOpen(held);

        var deleted = await _files.DeleteTemporaryAsync(partial, Guid.NewGuid());
        var inUse = await _files.DeleteTemporaryAsync(held, Guid.NewGuid());

        Assert.Equal(ClipFileOutcome.Done, deleted.Outcome);
        Assert.Equal(ClipFileOutcome.InUse, inUse.Outcome);
        Assert.False(File.Exists(partial));
        Assert.Empty(_store.PendingFileDeletes);
        Assert.Equal(ClipFileOutcome.Refused, (await _files.DeleteAsync(held, Guid.NewGuid(), Ct)).Outcome);
    }

    [Fact]
    public async Task Retry_DeletesWhatItCan_DropsGoneRefusedAndReferencedEntries_AndCountsAnAttemptForTheRest()
    {
        var deletable = Clip("old-1.mp4");
        var held = Clip("old-2.mkv");
        var referenced = Clip("Heat (1995) [00.01.00-00.01.30].mp4");
        var outside = _directory.CreateFile(Path.Combine("library", "Heat (1995).mkv"));
        var gone = Path.Combine(_root, "alice", "old-3.mp4");
        _fileSystem.HoldOpen(held);
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = referenced } };
        await _store.AddAsync(bookmark, Ct);
        foreach (var path in new[] { deletable, held, referenced, outside, gone })
        {
            await _store.AddPendingFileDeleteAsync(path, Ct);
        }

        var retry = await _files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct);

        Assert.Equal(new PendingFileDeleteRetry(Deleted: 1, Gone: 1, Refused: 1, Referenced: 1, Kept: 1), retry);
        var left = Assert.Single(_store.PendingFileDeletes);
        Assert.Equal(held, left.Path);
        Assert.Equal(2, left.Attempts);
        Assert.False(File.Exists(deletable));
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(referenced));
        Assert.True(File.Exists(outside));
        Assert.Equal([deletable, held], _fileSystem.Deletes);
        Assert.Contains(
            "Retried the clip files waiting to be deleted: 1 deleted, 1 already gone, 1 outside the clip folders and 1 in use by a clip again (dropped), 1 still waiting",
            _logger.At(LogLevel.Information).Select(e => e.Message));

        // Once the file is free, the next pass deletes it and the list is empty.
        _fileSystem.Release();
        Assert.Equal(1, (await _files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct)).Deleted);
        Assert.Empty(_store.PendingFileDeletes);
        Assert.False(File.Exists(held));
        Assert.Same(PendingFileDeleteRetry.None, await _files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct));
    }

    [Fact]
    public async Task Retry_KeepsAnEntryWhoseFolderCantBeReached()
    {
        // An offline drive: nothing is dropped, so the file is deleted once the drive is back.
        var file = Clip("old.mp4");
        await _store.AddPendingFileDeleteAsync(file, Ct);
        _fileSystem.AttributesFailure = _ => new IOException("The device is not ready.");

        var retry = await _files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct);

        Assert.Equal(1, retry.Kept);
        Assert.Equal(2, Assert.Single(_store.PendingFileDeletes).Attempts);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task InAClipFolderThatIsntThere_NothingCountsAsGone_ADeleteFails_AndAnAutomaticOneListsTheFile()
    {
        // An unplugged drive never causes deletions. On the real file system, a recorded clip folder gone after its clip
        // was written: in a 1.1 development build its files read as missing, and a missing file counts as deleted.
        var (usb, file) = await RecordedClipFolderWithAFileAsync("Heat (1995) - Bank job [00.01.00-00.01.30].mp4");
        var files = ClipFileSupport.Ops(_store, _logger, PhysicalClipFileSystem.Instance);
        var id = Guid.NewGuid();
        ClipFileSupport.Unplug(usb);

        var deleted = await files.DeleteAsync(file, id, Ct);
        var once = files.DeleteOnce(file, id, ClipFileOps.Attempts);
        var moved = await files.MoveAsync(file, Path.Combine(usb, "alice", "renamed.mp4"), id, Ct);
        var served = files.FindServable(file, id);
        var queued = await files.DeleteOrQueueAsync(file, id, BackgroundWriter.ClipWorker, Ct);

        Assert.All([deleted, once, moved, queued], r =>
        {
            Assert.Equal(ClipFileOutcome.Failed, r.Outcome);
            Assert.False(r.IsGone);
            Assert.Equal(ClipPathVerdict.FolderUnavailable, r.Check.Verdict);
        });
        Assert.Null(served);
        var error = ClipFileOps.ToRequestError(deleted);
        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        Assert.Equal(ClipFileOps.FolderUnavailableMessage, error.Message);
        Assert.True(queued.Queued);
        Assert.Equal(file, Assert.Single(_store.PendingFileDeletes).Path);
        Assert.Contains(
            $"Could not delete a clip file of bookmark {id}: The clip folder isn't there (an unplugged drive, or a folder that was moved or removed).",
            _logger.At(LogLevel.Warning).Select(e => e.Message));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains("Heat", StringComparison.Ordinal));

        ClipFileSupport.PlugIn(usb);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Retry_KeepsAnEntryWhoseClipFolderIsntThere_AndDeletesItOnceTheFolderIsBack()
    {
        var (usb, file) = await RecordedClipFolderWithAFileAsync("old.mp4");
        var files = ClipFileSupport.Ops(_store, _logger, PhysicalClipFileSystem.Instance);
        await _store.AddPendingFileDeleteAsync(file, Ct);
        ClipFileSupport.Unplug(usb);

        var retry = await files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct);

        Assert.Equal(new PendingFileDeleteRetry(Deleted: 0, Gone: 0, Refused: 0, Referenced: 0, Kept: 1), retry);
        Assert.Equal(2, Assert.Single(_store.PendingFileDeletes).Attempts);

        ClipFileSupport.PlugIn(usb);
        Assert.True(File.Exists(file));
        Assert.Equal(1, (await files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct)).Deleted);
        Assert.Empty(_store.PendingFileDeletes);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Move_MovesAClipIntoPlace_NeverOverAnotherFile()
    {
        var partial = Clip("new.mp4.partial");
        var destination = Path.Combine(_root, "alice", "new.mp4");
        var taken = Clip("taken.mp4", "someone else's");

        var moved = await _files.MoveAsync(partial, destination, Guid.NewGuid(), Ct);
        var refused = await _files.MoveAsync(destination, taken, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Done, moved.Outcome);
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(partial));
        Assert.Equal(ClipFileOutcome.Failed, refused.Outcome);
        Assert.Equal("A file with the clip's name exists already.", refused.Error);
        Assert.Equal("someone else's", await File.ReadAllTextAsync(taken, Ct));
        Assert.True(File.Exists(destination));
        Assert.Single(_fileSystem.Moves);
    }

    [Fact]
    public async Task Move_OfAFileInUse_TriesThreeTimes_ThenIsInUse()
    {
        // The rename of Rename file (since 1.2.0) goes through here too: a clip being watched can't be renamed (409).
        var file = Clip("a.mp4");
        _fileSystem.HoldOpen(file);

        var result = await _files.MoveAsync(file, Path.Combine(_root, "alice", "b.mp4"), Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.InUse, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, _fileSystem.Moves.Count);
        Assert.True(File.Exists(file));
        Assert.Equal(ClipFileOps.InUseMessage, ClipFileOps.ToRequestError(result).Message);
    }

    [Fact]
    public async Task Move_OutOfOrIntoWhatTheGuardRefuses_TouchesNothing()
    {
        var file = Clip("a.mp4");
        var outside = _directory.CreateFile(Path.Combine("library", "Heat (1995).mkv"));

        var into = await _files.MoveAsync(file, _directory.Combine("library", "b.mp4"), Guid.NewGuid(), Ct);
        var outOf = await _files.MoveAsync(outside, Path.Combine(_root, "alice", "c.mkv"), Guid.NewGuid(), Ct);
        var missing = await _files.MoveAsync(Path.Combine(_root, "alice", "gone.mp4.partial"), Path.Combine(_root, "alice", "d.mp4"), Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Refused, into.Outcome);
        Assert.Equal(ClipFileOutcome.Refused, outOf.Outcome);
        Assert.Equal(ClipFileOutcome.Missing, missing.Outcome);
        Assert.Empty(_fileSystem.Moves);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void FindServable_ServesOnlyAnExistingClipFileInTheClipFolders()
    {
        var file = Clip("a.mp4");
        var outside = _directory.CreateFile(Path.Combine("library", "Heat (1995).mkv"));
        var id = Guid.NewGuid();

        Assert.Equal(file, _files.FindServable(file, id));
        Assert.Null(_files.FindServable(Path.Combine(_root, "alice", "gone.mp4"), id));
        Assert.Null(_files.FindServable(null, id));
        Assert.Null(_files.FindServable(outside, id));
        Assert.Equal($"Refused to serve a clip file of bookmark {id}: OutsideClipFolders; nothing was touched", Assert.Single(_logger.At(LogLevel.Warning)).Message);
    }

    [Fact]
    public async Task AFileHeldOpenWithoutDeleteSharing_IsInUse_UntilItIsClosed()
    {
        // On NTFS: new FileStream(path, Open, Read, FileShare.ReadWrite), as a download or preview holds a clip
        // (Kestrel's PhysicalFile shares read and write, not delete). With the real 200 ms between attempts.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete or rename a file open without delete sharing.");
        var file = Clip("Heat (1995) - Bank job [00.01.00-00.01.30].mp4");
        var files = ClipFileSupport.Ops(_store, retryDelay: ClipFileOps.DefaultRetryDelay);
        var watch = Stopwatch.StartNew();
        ClipFileResult deleted;
        ClipFileResult renamed;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            deleted = await files.DeleteAsync(file, Guid.NewGuid(), Ct);
            renamed = await files.MoveAsync(file, Path.Combine(_root, "alice", "renamed.mp4"), Guid.NewGuid(), Ct);

            try
            {
                File.Delete(file);
                Assert.Fail("Windows deleted a file held open without delete sharing.");
            }
            catch (IOException ex)
            {
                Assert.True(ClipFileOps.IsInUse(ex), $"0x{ex.HResult:X8}");
            }
        }

        Assert.Equal(ClipFileOutcome.InUse, deleted.Outcome);
        Assert.Equal(3, deleted.Attempts);
        Assert.Equal(ClipFileOutcome.InUse, renamed.Outcome);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(750), $"two pauses of 200 ms for each: {watch.Elapsed}");
        Assert.True(File.Exists(file));

        var renamedNow = await files.MoveAsync(file, Path.Combine(_root, "alice", "renamed.mp4"), Guid.NewGuid(), Ct);
        var deletedNow = await files.DeleteAsync(Path.Combine(_root, "alice", "renamed.mp4"), Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Done, renamedNow.Outcome);
        Assert.Equal(ClipFileOutcome.Done, deletedNow.Outcome);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "alice")));
    }

    [Fact]
    public async Task AFileHeldOpenWithDeleteSharing_IsDeleted()
    {
        // Only handles without FILE_SHARE_DELETE block a delete.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing modes.");
        var file = Clip("a.mkv");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var result = await _files.DeleteAsync(file, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Done, result.Outcome);
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    private string Clip(string name, string contents = "clip") => _directory.CreateFile(Path.Combine("clips", "alice", name), contents);

    // A second clip folder (a USB drive's, say), recorded, with a clip file in it.
    private async Task<(string Folder, string File)> RecordedClipFolderWithAFileAsync(string name)
    {
        var folder = _directory.Combine("usb");
        var file = _directory.CreateFile(Path.Combine("usb", "alice", name), "clip");
        Assert.True(await _store.AddClipRootAsync(folder, Ct));
        return (folder, file);
    }
}
