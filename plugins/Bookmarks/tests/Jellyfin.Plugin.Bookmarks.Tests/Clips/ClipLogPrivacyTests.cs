using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Names and notes are never logged at Information or above, only ids and counts. A clip file's name holds its bookmark's
/// name, or (before 1.2.0) its note, so the clip worker and <see cref="ClipFileOps"/> log a clip file's path at Debug
/// only, and a failure's message names the file as <see cref="ClipFiles.LogPlaceholder"/> (every file operation is
/// <see cref="ClipFileOps"/>'s). The planner here names files as the real one does.
/// </summary>
public sealed class ClipLogPrivacyTests : IAsyncLifetime
{
    private const string SecretName = "Secret heist";
    private const string SecretNote = "Private note: the reflection";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // What a name or note looks like in a file name too: ':' becomes " -" (ClipFileNaming.Sanitize).
    private static readonly string[] _secrets = [SecretName, "Private note", "the reflection", "Confidential"];

    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly FakeClipPlanner _planner;
    private readonly FakeFfmpegRunner _runner = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly ListLogger<ClipExtractionService> _logger = new();
    private readonly ListLogger _fileLogger = new();
    private readonly ClipExtractionService _service;

    public ClipLogPrivacyTests()
    {
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        _planner = new FakeClipPlanner(_directory) { NamedFiles = true };
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            _planner,
            _runner,
            ClipFileSupport.Ops(_store, _fileLogger),
            ClipFileSupport.Janitor(_store, ClipFileSupport.Ops(_store, _fileLogger)),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            _host.Object,
            _logger);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryJobOutcome_LogsNoNameOrNote_AtInformationOrAbove_AndThePathsAtDebug()
    {
        await _service.StartAsync(Ct);

        // A named bookmark: a clip, then a remake of another range, whose completion deletes the first file.
        var named = await AddAsync(TestData.CreateBookmark(Guid.NewGuid()) with { Name = SecretName });
        var first = await MakeClipAsync(named, BookmarkClipMode.Fast);
        Assert.Contains(SecretName, first.Clip!.FilePath, StringComparison.Ordinal);
        await _store.UpdateAsync(named.Id, b => b with { StartTicks = b.StartTicks + TestData.OneSecond }, Ct);
        var remade = await MakeClipAsync(named, BookmarkClipMode.Accurate);
        Assert.NotEqual(first.Clip.FilePath, remade.Clip!.FilePath);
        await WaitUntilAsync(() => !File.Exists(first.Clip.FilePath));

        // A bookmark with a note only: ffmpeg fails, and its error output names the .partial file, which since 1.2.0 holds no
        // note (the video's name and the range alone).
        _runner.FailureFor = output => new ClipExtractionException($"ffmpeg failed with exit code 1: file:{output}: No space left on device");
        var noted = await AddAsync(TestData.CreateBookmark(Guid.NewGuid()) with { Note = SecretNote });
        await _service.EnqueueAsync(noted.Id, noted.UserId, BookmarkClipMode.Accurate, null, Ct);
        await WaitUntilAsync(() => _store.Get(noted.Id)?.Clip?.Status == BookmarkClipStatus.Failed);

        // An I/O failure, whose message names the file too.
        _runner.FailureFor = output => new IOException($"The process cannot access the file '{output}' because it is being used by another process.");
        var locked = await AddAsync(TestData.CreateBookmark(Guid.NewGuid()) with { Name = "Confidential" });
        await _service.EnqueueAsync(locked.Id, locked.UserId, BookmarkClipMode.Fast, null, Ct);
        await WaitUntilAsync(() => _store.Get(locked.Id)?.Clip?.Status == BookmarkClipStatus.Failed);
        _runner.FailureFor = null;

        // A cancelled job.
        _runner.HoldUntilReleased();
        await _service.EnqueueAsync(named.Id, named.UserId, BookmarkClipMode.Fast, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        await _service.CancelAsync(named.Id, Ct);
        await WaitUntilAsync(() => _store.Get(named.Id)?.Clip?.Status == BookmarkClipStatus.Cancelled);

        AssertNoSecretAtInformationOrAbove();
        var information = Messages(LogLevel.Information);
        Assert.Contains($"Extracting Fast clip 00:00:30 long for bookmark {named.Id}", information);
        Assert.Contains($"Clip for bookmark {named.Id} written (4 bytes)", information);
        Assert.Contains($"Clip for bookmark {named.Id} cancelled", information);
        var warnings = Messages(LogLevel.Warning);
        Assert.Contains($"Clip for bookmark {noted.Id} failed: ffmpeg failed with exit code 1: file:<clip file>.partial: No space left on device", warnings);
        Assert.Contains($"Clip for bookmark {locked.Id} failed: The process cannot access the file '<clip file>.partial' because it is being used by another process.", warnings);

        // The paths, and the failures whole, are still there at Debug.
        var debug = _logger.At(LogLevel.Debug).ToList();
        Assert.Contains(debug, e => e.Message.Contains(remade.Clip.FilePath!, StringComparison.Ordinal) && e.Message.Contains("written to", StringComparison.Ordinal));
        Assert.Contains(debug, e => e.Message.Contains(first.Clip.FilePath!, StringComparison.Ordinal) && e.Message.Contains("goes to", StringComparison.Ordinal));
        Assert.Contains(debug, e => e.Exception is ClipExtractionException ex && ex.Message.Contains(".partial: No space left on device", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => (e.Exception?.Message ?? string.Empty).Contains("the reflection", StringComparison.Ordinal) || e.Message.Contains("the reflection", StringComparison.Ordinal));
        Assert.Contains(debug, e => e.Exception is IOException ex && ex.Message.Contains("Confidential", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJobWrittenUnderATombstonesPath_LogsNoNameOrNote_AtInformationOrAbove()
    {
        // CompleteAsync's tombstone branch: the kept file was gone when the remake was planned, so the remake got its
        // name (a remake never takes an existing file's), and the tombstone now records the new file.
        await _service.StartAsync(Ct);
        var bookmark = await AddAsync(TestData.CreateBookmark(Guid.NewGuid()) with { Name = SecretName });
        var kept = await MakeClipAsync(bookmark, BookmarkClipMode.Fast);
        File.Delete(kept.Clip!.FilePath!);
        _runner.IgnoreCancellation = true;
        _runner.HoldUntilReleased();
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        await _store.UpdateAsync(bookmark.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);

        _runner.Release();
        await WaitUntilAsync(() => _service.GetProgress(bookmark.Id) is null);
        await WaitUntilAsync(() => Messages(LogLevel.Information).Any(m => m.Contains("replaced the clip file", StringComparison.Ordinal)));

        AssertNoSecretAtInformationOrAbove();
        Assert.Contains(
            $"Clip for bookmark {bookmark.Id} replaced the clip file its deleted bookmark keeps (4 bytes); the kept clip is this one now",
            Messages(LogLevel.Information));
        Assert.Equal(kept.Clip.FilePath, _store.Get(bookmark.Id)?.Clip?.FilePath);
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Message.Contains(kept.Clip.FilePath!, StringComparison.Ordinal) && e.Message.Contains("keeps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileOperationsThatFail_NameTheBookmarkAndNotTheFile_AndLogThePathAtDebug()
    {
        // A delete that fails with an error naming the file (as .NET's do), a file in use, a path outside the clip folders
        // and a pending delete dropped for it: ClipFileOps names the bookmark, never the file, at Information or above.
        await _store.AddClipRootAsync(_directory.Path, Ct);
        var logger = new ListLogger();
        var fileSystem = new FakeClipFileSystem();
        var files = ClipFileSupport.Ops(_store, logger, fileSystem);
        var id = Guid.NewGuid();
        var path = _directory.CreateFile("Heat (1995) - Secret heist [00.01.00-00.01.30].mp4");
        var held = _directory.CreateFile("Heat (1995) - Confidential [00.01.00-00.01.30].mkv");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "Heat (1995) - Secret heist [00.01.00-00.01.30].mp4");
        fileSystem.DeleteFailure = (p, _) => p == path ? new IOException($"Access to the path '{p}' is denied by a lock.", 5) : null;
        fileSystem.HoldOpen(held);

        var failed = await files.DeleteAsync(path, id, Ct);
        var inUse = await files.DeleteAsync(held, id, Ct);
        var refused = await files.DeleteAsync(outside, id, Ct);
        await _store.AddPendingFileDeleteAsync(outside, Ct);
        await files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, Ct);

        Assert.Equal(ClipFileOutcome.Failed, failed.Outcome);
        Assert.Equal(ClipFileOutcome.InUse, inUse.Outcome);
        Assert.Equal(ClipFileOutcome.Refused, refused.Outcome);
        Assert.Equal(
            [
                $"Could not delete a clip file of bookmark {id}: Access to the path '<clip file>' is denied by a lock.",
                $"Refused to delete a clip file of bookmark {id}: OutsideClipFolders; nothing was touched",
                "Dropped a file waiting to be deleted: it isn't a clip file in the clip folders (OutsideClipFolders); nothing was deleted"
            ],
            logger.At(LogLevel.Warning).Select(e => e.Message));
        Assert.Contains($"Could not delete a clip file of bookmark {id}: it is in use (3 attempts)", logger.At(LogLevel.Information).Select(e => e.Message));
        foreach (var entry in logger.Entries.Where(e => e.Level >= LogLevel.Information))
        {
            Assert.Null(entry.Exception);
            Assert.All(_secrets, secret => Assert.DoesNotContain(secret, entry.Message, StringComparison.OrdinalIgnoreCase));
        }

        var debug = logger.At(LogLevel.Debug).ToList();
        Assert.Contains(debug, e => e.Message.Contains(path, StringComparison.Ordinal) && e.Exception is IOException);
        Assert.Contains(debug, e => e.Message.Contains(held, StringComparison.Ordinal) && e.Exception is IOException);
        Assert.Contains(debug, e => e.Message.Contains(outside, StringComparison.Ordinal) && e.Message.StartsWith("Refused", StringComparison.Ordinal));
        Assert.Contains(debug, e => e.Message.Contains(outside, StringComparison.Ordinal) && e.Message.StartsWith("Dropped", StringComparison.Ordinal));
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(held));
    }

    [Fact]
    public void RedactForLog_ReplacesTheClipFilesPathsAndNames_AndNothingElse()
    {
        var output = @"C:\clips\alice\Heat (1995) - Secret heist [00.01.00-00.01.30].mp4";
        var temp = output + ".partial";

        Assert.Equal(
            @"ffmpeg failed with exit code 1: file:<clip file>.partial: No space left on device (input Q:\Video\Heat (1995)\Heat.mkv, folder C:\clips\alice)",
            ClipFiles.RedactForLog($@"ffmpeg failed with exit code 1: file:{temp}: No space left on device (input Q:\Video\Heat (1995)\Heat.mkv, folder C:\clips\alice)", output, temp));
        Assert.Equal(
            "could not write <clip file>.partial, nor <clip file>",
            ClipFiles.RedactForLog("could not write HEAT (1995) - SECRET HEIST [00.01.00-00.01.30].MP4.partial, nor " + output.ToUpperInvariant(), output, temp));
        Assert.Equal("nothing to hide", ClipFiles.RedactForLog("nothing to hide", null, string.Empty));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _runner.Release();
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _store.Dispose();
        _directory.Dispose();
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

    private void AssertNoSecretAtInformationOrAbove()
    {
        var entries = _logger.Entries.Concat(_fileLogger.Entries).Where(e => e.Level >= LogLevel.Information).ToList();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            foreach (var secret in _secrets)
            {
                Assert.DoesNotContain(secret, entry.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, entry.Exception?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private string[] Messages(LogLevel level) => _logger.At(level).Select(e => e.Message).ToArray();

    private async Task<Bookmark> AddAsync(Bookmark bookmark)
    {
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }

    // Queues a clip of the bookmark's range and waits for it (a remake: until the file is a new one).
    private async Task<Bookmark> MakeClipAsync(Bookmark bookmark, BookmarkClipMode mode)
    {
        var before = _store.Get(bookmark.Id)?.Clip?.RequestedAt;
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, mode, null, Ct);
        await WaitUntilAsync(() => _store.Get(bookmark.Id)?.Clip is { Status: BookmarkClipStatus.Completed } clip && clip.RequestedAt != before);
        return _store.Get(bookmark.Id)!;
    }
}
