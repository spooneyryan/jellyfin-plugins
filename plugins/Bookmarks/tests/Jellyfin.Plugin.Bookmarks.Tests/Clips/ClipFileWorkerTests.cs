using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The clip worker's file operations, with the real worker, store and file
/// system and a fake ffmpeg: a clip folder is recorded in <c>ClipRoots</c> before anything is written there; a remake gets a
/// fresh file name and deletes the previous file only once the record points at the new one; a previous or discarded file
/// in use, or in a clip folder that isn't there (an unplugged drive), waits in <c>PendingFileDeletes</c>, which
/// the janitor retries when the worker starts, before it resumes jobs.
/// </summary>
public sealed partial class ClipFileWorkerTests : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _directory = new();
    private readonly TempDirectory _otherDirectory = new();
    private readonly BookmarkStore _store;
    private readonly FakeClipPlanner _planner;
    private readonly FakeFfmpegRunner _runner = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly ListLogger<ClipExtractionService> _logger = new();
    private readonly List<ClipExtractionService> _services = [];

    public ClipFileWorkerTests()
    {
        // A store as a new install has it: no clip folder recorded yet.
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        _planner = new FakeClipPlanner(_directory) { NamedFiles = true };
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWorker_PlansANewClipsName_OnlyUnderTheGateARenameChoosesItsNameUnder()
    {
        // Found in testing: a job planned between a rename's choice of a name and its move would reserve that very name,
        // and the rename's move would fail at its last step; the worker's own semaphore of 1.0 left them apart.
        var files = ClipFileSupport.Ops(_store, _logger, _fileSystem);
        var service = await StartAsync(files: files);
        var bookmark = await AddAsync();
        await files.NameGate.WaitAsync(Ct);
        int planned;
        try
        {
            await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);

            // Claimed (Running) only once the start's own work is done, a new estimate of the queued job included (made outside
            // the gate when the start comes after the request, as it may on a busy machine): from then on no plan while held.
            await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Running);
            planned = _planner.Plans;
            await Task.Delay(300, Ct);
            Assert.Equal(planned, _planner.Plans);
            Assert.Empty(Directory.Exists(_planner.Root) ? Directory.GetFiles(_planner.Root, "*.partial", SearchOption.AllDirectories) : []);
        }
        finally
        {
            files.NameGate.Release();
        }

        await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);
        Assert.Equal(planned + 1, _planner.Plans);
    }

    [Fact]
    public async Task TheClipFolder_IsRecorded_BeforeTheJobWritesAnything()
    {
        Assert.Empty(_store.ClipRoots);
        var service = await StartAsync();
        _runner.HoldUntilReleased();
        var bookmark = await AddAsync();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);

        Assert.Equal([_planner.Root], _store.ClipRoots);
        _runner.Release();
        var done = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);
        Assert.StartsWith(_planner.Root + Path.DirectorySeparatorChar, done.Clip!.FilePath, StringComparison.Ordinal);
        Assert.Equal(done.Clip.FilePath, ClipFileSupport.Ops(_store).FindServable(done.Clip.FilePath, done.Id));
    }

    [Fact]
    public async Task ANewClipFolder_GetsItsMarkerFile_BeforeTheJobWritesIntoIt_AndSoDoesOneThereThatHoldsNoClip()
    {
        // The marker file proves to the missing-clip sweep that the folder is the one its clips were made in.
        var service = await StartAsync();
        _runner.HoldUntilReleased();
        var bookmark = await AddAsync();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);

        Assert.True(ClipFileSupport.IsMarked(_planner.Root));
        Assert.Contains(_planner.Root, _fileSystem.MarkerWrites);
        _runner.Release();
        await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);
        var text = await File.ReadAllTextAsync(ClipRootMarker.PathIn(_planner.Root), Ct);

        // With a token of its own, which the store records for the folder.
        var token = _store.GetClipRootToken(_planner.Root);
        Assert.Matches("^[0-9a-f]{32}$", token);
        Assert.EndsWith("\nRootId=" + ClipRootPaths.RootId(_planner.Root) + "\nToken=" + token + "\n", text, StringComparison.Ordinal);

        // A folder that is there and holds no clip recorded in Bookmarks (the setting pointed at another folder) gets one too.
        var other = new FakeClipPlanner(_otherDirectory) { NamedFiles = true };
        Directory.CreateDirectory(other.Root);
        await File.WriteAllTextAsync(Path.Combine(other.Root, "notes.txt"), "someone else's file", Ct);
        Assert.False(ClipFileSupport.IsMarked(other.Root));
        var elsewhere = await StartAsync(planner: other);
        var second = await AddAsync();
        await elsewhere.EnqueueAsync(second.Id, second.UserId, BookmarkClipMode.Fast, null, Ct);
        await WaitForStatusAsync(second.Id, BookmarkClipStatus.Completed);
        Assert.True(ClipFileSupport.IsMarked(other.Root));
    }

    [Fact]
    public async Task ARecordedClipFolderThatHoldsClips_ButIsntThere_IsNeverMadeAgain_TheJobFailsBeforeAnythingIsWritten()
    {
        // Found in testing, the worker's part: an early 1.2.0 build made the folder again for a clip asked for while the drive was
        // away (the free-space guard measured the folder above it), and the sweep then found every clip in it "missing".
        var service = await StartAsync();
        var first = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        ClipFileSupport.Unplug(_planner.Root);
        var bookmark = await AddAsync();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal(ClipExtractionService.ClipFolderAwayMessage, failed.Clip!.Error);
        Assert.False(Directory.Exists(_planner.Root));
        Assert.Equal(1, _runner.Runs);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains(ClipExtractionService.ClipFolderAwayMessage, StringComparison.Ordinal) && !e.Message.Contains(_planner.Root, StringComparison.OrdinalIgnoreCase));

        // Back: the clip is made, next to the first one.
        ClipFileSupport.PlugIn(_planner.Root);
        var made = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        Assert.True(File.Exists(made.Clip!.FilePath));
        Assert.True(File.Exists(first.Clip!.FilePath));
    }

    [Fact]
    public async Task ARecordedClipFolderThere_WithoutItsMarkerOrAnyOfItsClips_IsntWrittenInto()
    {
        // An empty mount point (or a share whose disk isn't mounted, another drive under the same letter) at the folder's path:
        // a clip written there would be the one clip "there", and prove the wrong folder to the sweep.
        var service = await StartAsync();
        await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        ClipFileSupport.Unplug(_planner.Root);
        Directory.CreateDirectory(_planner.Root);
        var bookmark = await AddAsync();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal(ClipExtractionService.ClipFolderUnrecognisedMessage, failed.Clip!.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_planner.Root));
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task AClipFolderWithoutItsMarker_GetsItFromTheNextJob_WhenOneOfItsClipsIsThere()
    {
        // A clip folder of Bookmarks 1.1 (or of an early 1.2.0 build): its clip there proves it, the job writes the marker (the
        // backfill) and is made.
        var service = await StartAsync();
        await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        ClipFileSupport.UnmarkClipFolder(_planner.Root);
        Assert.False(ClipFileSupport.IsMarked(_planner.Root));

        var made = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);

        Assert.True(File.Exists(made.Clip!.FilePath));
        Assert.True(ClipFileSupport.IsMarked(_planner.Root));
        Assert.Equal(_store.GetClipRootToken(_planner.Root), ClipFileSupport.TokenOf(_planner.Root));
    }

    [Fact]
    public async Task AFolderInTheClipFoldersPlace_HoldingAClipOneOfOlderVersionsWroteThere_IsntProvenByIt_TheJobFails()
    {
        // Found in testing, the worker's part: Golf, Hotel and India (a deleted bookmark's kept clip) and Juliet made in
        // the folder; the real folder away (renamed aside) and a folder in its place holding only Juliet's file, as Bookmarks
        // 1.1's worker left one while the drive was away (it made the folder again and wrote the clip there). The backfill took
        // that one clip there for proof before; now a folder whose clips aren't all there is unproven, and nothing is written.
        var service = await StartAsync();
        var golf = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        var india = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        var juliet = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        await _store.UpdateOrRemoveAsync(india.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow), Ct);
        ClipFileSupport.Unplug(_planner.Root);
        var stray = Path.Combine(_planner.Root, Path.GetRelativePath(_planner.Root, juliet.Clip!.FilePath!));
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.Copy(Path.Combine(_planner.Root + "-unplugged", Path.GetRelativePath(_planner.Root, stray)), stray);
        var bookmark = await AddAsync();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal(ClipExtractionService.ClipFolderUnprovenMessage, failed.Clip!.Error);
        Assert.Equal([stray], Directory.GetFiles(_planner.Root, "*", SearchOption.AllDirectories));
        Assert.Equal(4, _runner.Runs);
        Assert.Null(_store.Get(golf.Id)!.Clip!.MissingSinceUtc);

        // The real folder back: its marker proves it, and the clip is made next to the others.
        Directory.Delete(_planner.Root, recursive: true);
        ClipFileSupport.PlugIn(_planner.Root);
        var made = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        Assert.True(File.Exists(made.Clip!.FilePath));
        Assert.True(File.Exists(golf.Clip!.FilePath));
    }

    [Fact]
    public async Task TheDefaultClipFolder_DeletedWithItsClips_IsMadeAgain_ItsClipsMarkedMissing_AndTheClipIsMade()
    {
        // Found in testing: data/bookmarks/clips deleted as a whole refused every user's new clips, as if it were an
        // unplugged drive. Inside the plugin's data folder, which is there, it is deleted: made again with its marker, its
        // clips marked missing (the lifecycle's clock), and the clip is made there.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));
        var service = await StartAsync(paths: BookmarkPaths.ForRoot(_directory.Path), clock: clock);
        var first = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        var second = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        Directory.Delete(_planner.Root, recursive: true);

        var made = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);

        Assert.True(File.Exists(made.Clip!.FilePath));
        Assert.True(ClipFileSupport.IsMarked(_planner.Root));
        Assert.Equal(_store.GetClipRootToken(_planner.Root), ClipFileSupport.TokenOf(_planner.Root));
        Assert.Equal(clock.GetUtcNow().UtcDateTime, _store.Get(first.Id)!.Clip!.MissingSinceUtc);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, _store.Get(second.Id)!.Clip!.MissingSinceUtc);
        Assert.Null(_store.Get(made.Id)!.Clip!.MissingSinceUtc);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.StartsWith("The default clip folder was gone (deleted with its 2 clips): made again with its marker file", StringComparison.Ordinal) && !e.Message.Contains(_planner.Root, StringComparison.OrdinalIgnoreCase));

        // Another folder away is still an unplugged drive: never made again.
        var other = new FakeClipPlanner(_otherDirectory) { NamedFiles = true };
        var elsewhere = await StartAsync(planner: other, paths: BookmarkPaths.ForRoot(_directory.Path), clock: clock);
        await MakeClipAsync(elsewhere, await AddAsync(), BookmarkClipMode.Fast);
        ClipFileSupport.Unplug(other.Root);
        var refused = await AddAsync();
        await elsewhere.EnqueueAsync(refused.Id, refused.UserId, BookmarkClipMode.Fast, null, Ct);
        Assert.Equal(ClipExtractionService.ClipFolderAwayMessage, (await WaitForStatusAsync(refused.Id, BookmarkClipStatus.Failed)).Clip!.Error);
        Assert.False(Directory.Exists(other.Root));
    }

    [Fact]
    public async Task AMarkerWithoutAToken_IsAdoptedByTheNextJob()
    {
        // Markers written before the token are taken for the folder once, and given one.
        var service = await StartAsync();
        await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        ClipFileSupport.MarkClipFolderWithoutToken(_planner.Root);
        await _store.UpdateDocumentAsync(d => d with { ClipRootTokens = new Dictionary<string, string>() }, Ct);
        Assert.Null(ClipFileSupport.TokenOf(_planner.Root));

        var made = await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);

        Assert.True(File.Exists(made.Clip!.FilePath));
        Assert.NotNull(ClipFileSupport.TokenOf(_planner.Root));
        Assert.Equal(_store.GetClipRootToken(_planner.Root), ClipFileSupport.TokenOf(_planner.Root));
    }

    [Fact]
    public async Task AClipFolderThatGoesAwayAsTheJobStarts_FailsTheJob_OnceItsPartialIsReserved_AndLeavesNoPartial()
    {
        // The folder is looked at once more after the job's .partial is reserved: one unplugged between the first look and the
        // reservation (which would make it again) has no marker, and no clip is written there.
        var service = await StartAsync();
        await MakeClipAsync(service, await AddAsync(), BookmarkClipMode.Fast);
        var reads = 0;
        _fileSystem.MarkerReadFailure = _ => Interlocked.Increment(ref reads) >= 3 ? new IOException("The device is not ready.") : null;
        var bookmark = await AddAsync();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal(ClipExtractionService.ClipFolderAwayMessage, failed.Clip!.Error);
        Assert.Equal(3, Volatile.Read(ref reads));
        Assert.Equal(1, _runner.Runs);
        Assert.Empty(Directory.GetFiles(_planner.Root, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AClipFolderThatCantBeRecorded_FailsTheJob_BeforeFfmpegRuns()
    {
        // 50 folders recorded already: the job fails with the store's 409 message, and nothing is written.
        await _store.UpdateDocumentAsync(
            d => d with { ClipRoots = Enumerable.Range(0, BookmarkStoreDocument.MaxClipRoots).Select(i => _directory.Combine("old", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList() },
            Ct);
        var service = await StartAsync();
        var bookmark = await AddAsync();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.StartsWith("Clips have been written to 50 different folders", failed.Clip!.Error, StringComparison.Ordinal);
        Assert.Equal(0, _runner.Runs);
        Assert.False(Directory.Exists(_planner.Root) && Directory.EnumerateFileSystemEntries(_planner.Root, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task ARemake_GetsAFreshName_AndDeletesThePreviousFile_OnceTheRecordPointsAtTheNewOne()
    {
        var service = await StartAsync();
        var bookmark = await AddAsync();
        var first = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);

        // The same range and mode: the canonical name is the previous clip's, so the remake gets " (2)".
        var second = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(first.Clip!.FilePath)!, Path.GetFileNameWithoutExtension(first.Clip.FilePath) + " (2).mkv"),
            second.Clip!.FilePath);
        await WaitUntilAsync(() => !File.Exists(first.Clip.FilePath));
        Assert.True(File.Exists(second.Clip.FilePath));
        Assert.Empty(_store.PendingFileDeletes);

        // The next remake takes the canonical name again, now that it is free.
        var third = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        Assert.Equal(first.Clip.FilePath, third.Clip!.FilePath);
        await WaitUntilAsync(() => !File.Exists(second.Clip.FilePath));
    }

    [Fact]
    public async Task ARemake_UnderTheNameOfAFileThatWentMissing_ReadsReady_InTheClipList_AtOnce()
    {
        // Found in testing: the remake of a "File missing" clip takes the name that is free again, and the worker moves the
        // finished clip there; the clip list kept its "missing" for up to 30 s after (renewed by the page's polls while the
        // job ran), and its Forget then deleted the new file. The list's clock never moves here.
        var states = new ClipStateCache(_store, _fileSystem, new ManualTimeProvider(DateTimeOffset.UtcNow));
        var list = new ClipQueryService(_store, states);
        var service = await StartAsync(states: states);
        var bookmark = await AddAsync();
        var first = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        File.Delete(first.Clip!.FilePath!);
        Assert.Equal(BookmarkClipState.Missing, Row(list, bookmark.Id).State);

        _runner.HoldUntilReleased();
        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        var making = Row(list, bookmark.Id);
        Assert.Equal((BookmarkClipState.InProgress, false), (making.Facts.State, making.Facts.FileExists));
        _runner.Release();
        await WaitUntilAsync(() => _store.Get(bookmark.Id)?.Clip is { Status: BookmarkClipStatus.Completed } clip && clip.RequestedAt != first.Clip.RequestedAt);

        var done = Row(list, bookmark.Id);
        Assert.Equal(first.Clip.FilePath, done.Clip.FilePath);
        Assert.Equal((BookmarkClipState.Ready, true), (done.Facts.State, done.Facts.FileExists));
        var summary = list.Summarize(list.Rows(bookmark.UserId));
        Assert.Equal((1, 0), (summary.Count, summary.Missing));
    }

    [Fact]
    public async Task ARemakeThatKeepsItsName_ReplacesItsOwnFile_WithNothingLeftToDelete()
    {
        // Bookmarks 1.2.1: a remake under its own file's name (the real planner's ReplacesOwnFile) writes its .partial and
        // replaces the file at the end, where it used to take " (2)".
        _planner.ReplacePreviousFile = true;
        var service = await StartAsync();
        var bookmark = await AddAsync();
        var first = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        await File.WriteAllTextAsync(first.Clip!.FilePath!, "the first clip", Ct);

        var second = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);

        Assert.Equal(first.Clip.FilePath, second.Clip!.FilePath);
        Assert.Equal("clip", await File.ReadAllTextAsync(second.Clip.FilePath!, Ct));
        Assert.Equal(BookmarkClipStatus.Completed, second.Clip.Status);
        Assert.False(File.Exists(second.Clip.FilePath + ".partial"));
        Assert.Empty(_store.PendingFileDeletes);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.Clip.FilePath)!, "*.mkv*"));
    }

    [Fact]
    public async Task ARemakeThatKeepsItsName_WhoseFileIsHeldOpen_CompletesUnderAFreshName_AndTheOldFileWaits()
    {
        _planner.ReplacePreviousFile = true;
        var service = await StartAsync();
        var bookmark = await AddAsync();
        var first = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        var old = first.Clip!.FilePath!;
        _fileSystem.HoldOpen(old);

        var second = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        await WaitUntilAsync(() => _store.PendingFileDeletes.Count == 1);

        Assert.Equal(Path.Combine(Path.GetDirectoryName(old)!, Path.GetFileNameWithoutExtension(old) + " (2).mkv"), second.Clip!.FilePath);
        Assert.Equal("clip", await File.ReadAllTextAsync(second.Clip.FilePath!, Ct));
        Assert.Equal(old, Assert.Single(_store.PendingFileDeletes).Path);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(old + ".partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARemake_WhileThePreviousClipIsHeldOpen_CompletesUnderAFreshName_AndTheOldFileWaits_UntilTheNextStart(bool keepsItsName)
    {
        // A remake while the old clip is held open completes under a fresh name, and the old path is in PendingFileDeletes
        // until the stream closes. The stream is a real one on NTFS, as a download or preview holds it.
        // Since Bookmarks 1.2.1 a remake under its own file's name tries to replace the file first (keepsItsName).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete a file open without delete sharing.");
        _planner.ReplacePreviousFile = keepsItsName;
        var service = await StartAsync(PhysicalClipFileSystem.Instance);
        var bookmark = await AddAsync();
        var first = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        var old = first.Clip!.FilePath!;

        Bookmark second;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            second = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
            await WaitUntilAsync(() => _store.PendingFileDeletes.Count == 1);

            Assert.NotEqual(old, second.Clip!.FilePath);
            Assert.Equal("clip", await File.ReadAllTextAsync(second.Clip.FilePath!, Ct));
            Assert.Equal(BookmarkClipStatus.Completed, second.Clip.Status);
            var pending = Assert.Single(_store.PendingFileDeletes);
            Assert.Equal(old, pending.Path);
            Assert.Equal(1, pending.Attempts);
            Assert.True(File.Exists(old));
            Assert.Contains(_logger.At(LogLevel.Information), e => e.Message == $"A clip file of bookmark {bookmark.Id} couldn't be deleted now (InUse); it is deleted later");

            // A start while it is still open keeps it waiting, one attempt more.
            await RestartAsync();
            await WaitUntilAsync(() => _store.PendingFileDeletes.Single().Attempts == 2);
            Assert.True(File.Exists(old));
        }

        // The stream is closed: the next start deletes it.
        await RestartAsync();
        await WaitUntilAsync(() => _store.PendingFileDeletes.Count == 0);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(second.Clip!.FilePath));
        Assert.Equal(second.Clip.FilePath, _store.Get(bookmark.Id)?.Clip?.FilePath);
    }

    [Fact]
    public async Task ARemake_WhosePreviousFileIsInAClipFolderThatIsntThere_ListsThatFile_UntilTheFolderIsBack()
    {
        // The previous clip is on a drive that is unplugged now (a clip folder of its own, recorded), and the remake
        // goes to the configured folder. On the real file system the old file read as missing, so it was never listed and
        // stayed on the drive, untracked, once the drive was back.
        var usb = _directory.Combine("usb");
        var old = _directory.CreateFile(Path.Combine("usb", "alice", "Heat (1995) - Bank job [00.01.00-00.01.30].mkv"), "old clip");
        await _store.AddClipRootAsync(usb, Ct);
        var bookmark = await AddAsync(new ClipInfo
        {
            Status = BookmarkClipStatus.Completed,
            Mode = BookmarkClipMode.Fast,
            StartTicks = 60 * TestData.OneSecond,
            EndTicks = 90 * TestData.OneSecond,
            RequestedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            FilePath = old,
            FileSize = 8
        });
        var service = await StartAsync(PhysicalClipFileSystem.Instance);

        // The drive goes once the startup clean-up has read the recorded clip folders. StartAsync returns while it still runs
        // in the background, and Windows refuses to move a folder while a handle below it is open: an Unplug during its
        // listing of usb failed with "Access to the path '…\usb' is denied" (5 of 60 runs of the Clips tests).
        await WaitForTheStartupCleanUpAsync(1);
        ClipFileSupport.Unplug(usb);

        var remade = await MakeClipAsync(service, bookmark, BookmarkClipMode.Fast);
        await WaitUntilAsync(() => _store.PendingFileDeletes.Count == 1);

        Assert.StartsWith(_planner.Root + Path.DirectorySeparatorChar, remade.Clip!.FilePath, StringComparison.Ordinal);
        var pending = Assert.Single(_store.PendingFileDeletes);
        Assert.Equal(old, pending.Path);
        Assert.Equal(1, pending.Attempts);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message == $"A clip file of bookmark {bookmark.Id} couldn't be deleted now (Failed); it is deleted later");

        // A start while the drive is still unplugged keeps it waiting, one attempt more.
        await RestartAsync();
        await WaitUntilAsync(() => _store.PendingFileDeletes.Single().Attempts == 2);

        // Plugged in again: the old file is there, and the next start deletes it.
        ClipFileSupport.PlugIn(usb);
        Assert.True(File.Exists(old));
        await RestartAsync();
        await WaitUntilAsync(() => _store.PendingFileDeletes.Count == 0);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(remade.Clip.FilePath));
        Assert.Equal(remade.Clip.FilePath, _store.Get(bookmark.Id)?.Clip?.FilePath);
    }

    [Fact]
    public async Task ADiscardedOutputInUse_WaitsInPendingFileDeletes()
    {
        // The bookmark was removed while its clip was made; the finished file is opened meanwhile (a library scan, say).
        var service = await StartAsync();
        _runner.HoldUntilReleased();
        var bookmark = await AddAsync();
        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        var output = _planner.OutputPathFor(_store.Get(bookmark.Id)!);
        _fileSystem.HoldOpen(output);

        await _store.RemoveAsync(bookmark.Id, Ct);
        _runner.Release();
        await WaitUntilAsync(() => _store.PendingFileDeletes.Count == 1);

        Assert.Equal(output, _store.PendingFileDeletes.Single().Path);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    [Fact]
    public async Task TheStartupCleanUp_RetriesPendingDeletes_BeforeInterruptedJobsResume()
    {
        var waiting = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) - Old [00.01.00-00.01.30].mp4"));
        await _store.AddClipRootAsync(_planner.Root, Ct);
        await _store.AddPendingFileDeleteAsync(waiting, Ct);
        var interrupted = await AddAsync(new ClipInfo
        {
            Status = BookmarkClipStatus.Running,
            Mode = BookmarkClipMode.Fast,
            StartTicks = 60 * TestData.OneSecond,
            EndTicks = 90 * TestData.OneSecond,
            RequestedAt = DateTime.UtcNow
        });

        await StartAsync();
        await WaitForStatusAsync(interrupted.Id, BookmarkClipStatus.Completed);

        Assert.Empty(_store.PendingFileDeletes);
        Assert.False(File.Exists(waiting));
        var messages = _logger.Entries.Select(e => e.Message).ToList();
        var retried = messages.FindIndex(m => m.StartsWith("Retried the clip files waiting to be deleted: 1 deleted", StringComparison.Ordinal));
        var resumed = messages.FindIndex(m => m.StartsWith("Resuming 1 clip jobs", StringComparison.Ordinal));
        Assert.InRange(retried, 0, resumed - 1);
    }

    [Fact]
    public async Task AJobACrashInterrupted_ResumesUnderItsOwnName_OnceTheJanitorDeletedItsStalePartial()
    {
        // A server crash mid-encode, the kill-and-restart simulation: the server died while the job ran, so the record
        // still says Running and its .partial is on disk. Before 1.1 the resumed job found the name taken by the stale file,
        // wrote "<name> (2)" and left the stale file for good.
        var interrupted = await AddAsync(new ClipInfo
        {
            Status = BookmarkClipStatus.Running,
            Mode = BookmarkClipMode.Fast,
            StartTicks = 60 * TestData.OneSecond,
            EndTicks = 90 * TestData.OneSecond,
            RequestedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await _store.AddClipRootAsync(_planner.Root, Ct);
        var output = _planner.OutputPathFor(_store.Get(interrupted.Id)!);
        var stale = _directory.CreateFile(Path.Combine("clips", Path.GetFileName(output) + ".partial"), "half a clip");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-5));

        await StartAsync(startedUtc: DateTime.UtcNow);
        var done = await WaitForStatusAsync(interrupted.Id, BookmarkClipStatus.Completed);

        Assert.Equal(output, done.Clip!.FilePath);
        Assert.Equal("clip", await File.ReadAllTextAsync(output, Ct));
        Assert.False(File.Exists(stale));
        Assert.Equal([output], ClipFileSupport.FilesBesidesMarker(_planner.Root, SearchOption.AllDirectories));
        var messages = _logger.Entries.Select(e => e.Message).ToList();
        var cleaned = messages.FindIndex(m => m.StartsWith("Cleaned up the unfinished clip files (.partial) of clip jobs from before the last shutdown: 1 deleted,", StringComparison.Ordinal));
        var resumed = messages.FindIndex(m => m.StartsWith("Resuming 1 clip jobs", StringComparison.Ordinal));
        Assert.InRange(cleaned, 0, resumed - 1);
    }

    [Fact]
    public async Task WithTheCleanUpOff_TheResumedJobTakesAnotherName_AndTheStalePartialStays()
    {
        // CleanUpPartialFilesOnStartup off: 1.0's behaviour.
        var interrupted = await AddAsync(new ClipInfo
        {
            Status = BookmarkClipStatus.Queued,
            Mode = BookmarkClipMode.Fast,
            StartTicks = 60 * TestData.OneSecond,
            EndTicks = 90 * TestData.OneSecond,
            RequestedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await _store.AddClipRootAsync(_planner.Root, Ct);
        var output = _planner.OutputPathFor(_store.Get(interrupted.Id)!);
        var stale = _directory.CreateFile(Path.Combine("clips", Path.GetFileName(output) + ".partial"), "half a clip");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-5));

        await StartAsync(configuration: new PluginConfiguration { CleanUpPartialFilesOnStartup = false }, startedUtc: DateTime.UtcNow);
        var done = await WaitForStatusAsync(interrupted.Id, BookmarkClipStatus.Completed);

        Assert.Equal(Path.Combine(_planner.Root, Path.GetFileNameWithoutExtension(output) + " (2).mkv"), done.Clip!.FilePath);
        Assert.True(File.Exists(stale));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.StartsWith("Cleaned up the unfinished clip files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheJanitor_LogsAStoreThatCantBeWritten_AndTheWorkerStillStarts()
    {
        var store = new Mock<IBookmarkStore>();
        store.SetupGet(s => s.ReadOnlyReason).Returns((BookmarkStoreReadOnlyReason?)null);
        store.SetupGet(s => s.ClipRoots).Returns([_planner.Root]);
        store.SetupGet(s => s.PendingFileDeletes).Returns([new PendingFileDelete { Path = Path.Combine(_planner.Root, "gone.mp4"), Attempts = 1 }]);
        store.Setup(s => s.GetAll()).Returns([]);
        store.Setup(s => s.UpdatePendingFileDeletesAsync(It.IsAny<Func<PendingFileDelete, PendingFileDelete?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(PluginException.Unavailable("The bookmark data couldn't be saved."));
        var logger = new ListLogger<PartialFileJanitor>();
        var janitor = ClipFileSupport.Janitor(store.Object, ClipFileSupport.Ops(store.Object), logger: logger);

        var result = await janitor.RunAtStartupAsync(Ct);

        Assert.Same(PendingFileDeleteRetry.None, result.PendingDeletes);
        Assert.Equal(new StalePartialCleanup(0, 0, 0, 0, 1), result.Partials);
        var error = Assert.Single(logger.At(LogLevel.Error));
        Assert.Equal("Could not retry the clip files waiting to be deleted", error.Message);
        Assert.IsType<PluginException>(error.Exception);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _runner.Release();
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        _store.Dispose();
        _directory.Dispose();
        _otherDirectory.Dispose();
    }

    // A start of the server: a new worker, with its startup clean-up. The janitor takes the process's start unless given one
    // (a test's .partial files are then newer, so kept), and logs with the worker; its file operations tell the clip list's
    // cache, when given one, about the files they change.
    private async Task<ClipExtractionService> StartAsync(
        IClipFileSystem? fileSystem = null,
        PluginConfiguration? configuration = null,
        DateTime? startedUtc = null,
        ClipStateCache? states = null,
        FakeClipPlanner? planner = null,
        BookmarkPaths? paths = null,
        TimeProvider? clock = null,
        ClipFileOps? files = null)
    {
        files ??= ClipFileSupport.Ops(_store, _logger, fileSystem ?? _fileSystem, states: states, paths: paths, clock: clock);
        configuration ??= new PluginConfiguration();
        var service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            planner ?? _planner,
            _runner,
            files,
            ClipFileSupport.Janitor(_store, files, configuration, startedUtc, _logger),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(configuration),
            _host.Object,
            _logger);
        _services.Add(service);
        await service.StartAsync(Ct);
        return service;
    }

    // A server restart: the running worker stops, a new one starts (with its startup clean-up).
    private async Task RestartAsync()
    {
        foreach (var running in _services)
        {
            await running.StopAsync(CancellationToken.None);
        }

        await StartAsync(PhysicalClipFileSystem.Instance);
    }

    // The startup clean-up of the given start (1 = the first) has listed every recorded clip folder that is there: it logs its
    // counts once it has, and holds none of them open after that.
    private Task WaitForTheStartupCleanUpAsync(int start)
        => WaitUntilAsync(() => _logger.Entries.Count(e => e.Message.StartsWith("Cleaned up the unfinished clip files (.partial)", StringComparison.Ordinal)) >= start);

    private async Task<Bookmark> AddAsync(ClipInfo? clip = null)
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid(), 60, 90) with { Name = "Bank job", Clip = clip };
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }

    private async Task<Bookmark> MakeClipAsync(ClipExtractionService service, Bookmark bookmark, BookmarkClipMode mode)
    {
        var before = _store.Get(bookmark.Id)?.Clip?.RequestedAt;
        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, mode, null, Ct);
        await WaitUntilAsync(() => _store.Get(bookmark.Id)?.Clip is { Status: BookmarkClipStatus.Completed } clip && clip.RequestedAt != before);
        return _store.Get(bookmark.Id)!;
    }

    // A record's row of the clip list, as one request reads it.
    private ClipRow Row(ClipQueryService list, Guid id) => Assert.Single(list.Rows([_store.Get(id)!]));

    private async Task<Bookmark> WaitForStatusAsync(Guid id, BookmarkClipStatus status)
    {
        await WaitUntilAsync(() => _store.Get(id)?.Clip?.Status == status);
        return _store.Get(id)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }
}
