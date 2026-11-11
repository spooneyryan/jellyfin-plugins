using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The stale <c>.partial</c> clean-up at startup:
/// on real files in temporary clip folders, only files named as clips being written (<see cref="ClipNamePattern"/>), in a
/// recorded clip folder or a folder directly inside it, last written before the server started, are deleted; nothing while
/// the data is read-only or the setting is off; a clip folder that isn't there, a folder that can't be listed and a file in
/// use are left for the next start; no file name reaches the log above Debug.
/// </summary>
public sealed class PartialFileJanitorTests : IDisposable
{
    private const string Stale = "Heat (1995) - Secret heist [00.01.00-00.01.30].mkv.partial";

    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly ListLogger _logger = new();
    private readonly ListLogger _fileLogger = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private BookmarkStore? _store;

    public PartialFileJanitorTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyStalePartialsOfClipsBeingWritten_InTheClipFolderAndOneLevelDown_AreDeleted()
    {
        var store = Store(_root);
        string[] deleted =
        [
            Old(Path.Combine("clips", Stale)),                                                                       // in the clip folder itself
            Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30] (2).mp4.partial")),                  // a per-user folder
            Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30] (0123456789abcdef0123456789abcdef).mkv.partial")),
            Old(Path.Combine("clips", "bob_", "Clip [100.00.00-100.00.10].mp4.partial")),
            Hidden(Old(Path.Combine("clips", "alice", "Hidden [00.00.01-00.00.02].mkv.partial")))                     // the listing skips reparse points only
        ];
        string[] kept =
        [
            Old(Path.Combine("clips", "alice", "movie.mkv.partial")),                                               // another program's
            Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].avi.partial")),
            Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].MKV.partial")),
            Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mkv.partial.old")),
            Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mkv")),                            // a finished clip
            Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mkv.partial.txt")),
            Old(Path.Combine("clips", "alice", "deeper", "Heat (1995) [00.01.00-00.01.30].mkv.partial")),          // two levels down
            Old(Path.Combine("clips", "alice", "deeper", "still", Stale)),
            Old(Path.Combine("clips2", Stale)),                                                                      // a sibling sharing the prefix
            Old(Path.Combine("library", "Heat (1995)", Stale)),                                                      // outside every clip folder
            New(Path.Combine("clips", "alice", "Heat (1995) [00.02.00-00.02.30].mkv.partial"))                     // written since the start
        ];
        Directory.CreateDirectory(Path.Combine(_root, "alice", "Heat (1995) [00.03.00-00.03.30].mkv.partial"));      // a folder of that name

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(Deleted: 5, NotDeleted: 0, Newer: 1, Folders: 1, FoldersNotThere: 0), run.Partials);
        Assert.All(deleted, f => Assert.False(File.Exists(f), f));
        Assert.All(kept, f => Assert.True(File.Exists(f), f));
        Assert.True(Directory.Exists(Path.Combine(_root, "alice", "Heat (1995) [00.03.00-00.03.30].mkv.partial")));
        Assert.Equal(deleted.Order(StringComparer.OrdinalIgnoreCase), _fileSystem.Deletes.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Same(PendingFileDeleteRetry.None, run.PendingDeletes);
    }

    [Fact]
    public async Task AFileLastWrittenAtTheStartOrLater_IsKept()
    {
        var store = Store(_root);
        var atStart = Touch(_directory.CreateFile(Path.Combine("clips", "alice", "A [00.00.01-00.00.02].mkv.partial")), _started);
        var justBefore = Touch(_directory.CreateFile(Path.Combine("clips", "alice", "B [00.00.01-00.00.02].mkv.partial")), _started.AddMilliseconds(-1));

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(1, 0, 1, 1, 0), run.Partials);
        Assert.True(File.Exists(atStart));
        Assert.False(File.Exists(justBefore));
    }

    [Fact]
    public async Task TheLog_CountsAtInformation_AndNamesFilesAtDebugOnly()
    {
        // Privacy: a clip file's name holds its bookmark's name or (before 1.2.0) its note.
        var store = Store(_root);
        Old(Path.Combine("clips", "alice", Stale));
        New(Path.Combine("clips", "alice", "Heat (1995) - Secret plan [00.02.00-00.02.30].mkv.partial"));

        await Janitor(store).RunAtStartupAsync(Ct);

        var line = Assert.Single(_logger.At(LogLevel.Information));
        Assert.Equal(
            "Cleaned up the unfinished clip files (.partial) of clip jobs from before the last shutdown: 1 deleted, 0 could not be deleted, 1 written since the server started kept, in 1 clip folders (0 not there)",
            line.Message);
        Assert.DoesNotContain(_logger.Entries.Concat(_fileLogger.Entries), e => e.Level >= LogLevel.Information && e.Message.Contains("Secret", StringComparison.Ordinal));
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Message.Contains("Secret heist", StringComparison.Ordinal));
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Message.Contains("Secret plan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryRecordedClipFolder_IsCleaned_EachFileOnce()
    {
        // Two recorded folders, one inside the other (a clip folder set to a per-user folder once): each is looked at to its
        // own depth, and a file under both is deleted and counted once.
        var usb = _directory.Combine("usb");
        var store = Store(_root, Path.Combine(_root, "alice"), usb);
        var inAlice = Old(Path.Combine("clips", "alice", Stale));
        var belowAlice = Old(Path.Combine("clips", "alice", "old", Stale));
        var tooDeep = Old(Path.Combine("clips", "alice", "old", "older", Stale));
        var onUsb = Old(Path.Combine("usb", "bob", Stale));

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(3, 0, 0, 3, 0), run.Partials);
        Assert.False(File.Exists(inAlice));
        Assert.False(File.Exists(belowAlice));
        Assert.False(File.Exists(onUsb));
        Assert.True(File.Exists(tooDeep));
        Assert.Single(_fileSystem.Deletes, d => string.Equals(d, inAlice, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AClipFolderThatIsntThere_IsLeftAlone_AndTheOthersAreCleaned()
    {
        // An unplugged drive. Its files are fine where the drive is, and the next start that finds it cleans it.
        var usb = _directory.Combine("usb");
        var onUsb = Old(Path.Combine("usb", "alice", Stale));
        var store = Store(_root, usb);
        var local = Old(Path.Combine("clips", "alice", Stale));
        ClipFileSupport.Unplug(usb);

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(1, 0, 0, 1, 1), run.Partials);
        Assert.False(File.Exists(local));

        ClipFileSupport.PlugIn(usb);
        Assert.True(File.Exists(onUsb));
        var next = await Janitor(store).RunAtStartupAsync(Ct);
        Assert.Equal(new StalePartialCleanup(1, 0, 0, 2, 0), next.Partials);
        Assert.False(File.Exists(onUsb));
    }

    [Fact]
    public async Task AClipFolderThatCantBeListed_IsLookedAtNextTime_AndTheOthersStillAre()
    {
        var usb = _directory.Combine("usb");
        Directory.CreateDirectory(usb);
        var store = Store(usb, _root);
        var onUsb = Old(Path.Combine("usb", "alice", Stale));
        var local = Old(Path.Combine("clips", "alice", Stale));
        _fileSystem.ListFailure = path => string.Equals(path, usb, StringComparison.OrdinalIgnoreCase)
            ? new IOException($"The device is not ready: '{onUsb}'")
            : null;

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(1, 0, 0, 1, 1), run.Partials);
        Assert.True(File.Exists(onUsb));
        Assert.False(File.Exists(local));
        var warning = Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Equal("Could not look for unfinished clip files in a clip folder (IOException); it is looked at again at the next start", warning.Message);
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Exception is IOException);
    }

    [Fact]
    public async Task AStalePartialInUse_IsKept_AndDeletedAtTheNextStart()
    {
        var store = Store(_root);
        var held = Old(Path.Combine("clips", "alice", Stale));
        _fileSystem.HoldOpen(held);

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(0, 1, 0, 1, 0), run.Partials);
        Assert.True(File.Exists(held));
        Assert.Equal(ClipFileOps.Attempts, _fileSystem.Deletes.Count);
        Assert.Contains(_fileLogger.At(LogLevel.Information), e => e.Message == "Could not delete a clip file of bookmark (none): it is in use (3 attempts)");
        Assert.Empty(store.PendingFileDeletes);

        _fileSystem.Release();
        var next = await Janitor(store).RunAtStartupAsync(Ct);
        Assert.Equal(new StalePartialCleanup(1, 0, 0, 1, 0), next.Partials);
        Assert.False(File.Exists(held));
    }

    [Fact]
    public async Task AStalePartialHeldOpenOnWindows_IsKept_UntilItIsClosed()
    {
        // On real files: a handle without delete sharing (an ffmpeg an earlier run left running, an
        // antivirus scan).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing modes.");
        var store = Store(_root);
        var held = Old(Path.Combine("clips", "alice", Stale));
        var janitor = ClipFileSupport.Janitor(store, ClipFileSupport.Ops(store, _fileLogger), startedUtc: _started, logger: _logger);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var run = await janitor.RunAtStartupAsync(Ct);
            Assert.Equal(new StalePartialCleanup(0, 1, 0, 1, 0), run.Partials);
            Assert.True(File.Exists(held));
        }

        var next = await janitor.RunAtStartupAsync(Ct);
        Assert.Equal(new StalePartialCleanup(1, 0, 0, 1, 0), next.Partials);
        Assert.False(File.Exists(held));
    }

    [Fact]
    public async Task AJunctionBelowTheClipFolder_IsNotFollowed()
    {
        // A junction in a per-user folder's place leads outside the clip folders. The listing skips it, and
        // the guard would refuse it anyway.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junctions.");
        var store = Store(_root);
        var outside = Old(Path.Combine("elsewhere", Stale));
        var throughAFolder = Old(Path.Combine("elsewhere", "alice", Stale));
        MakeJunction(Path.Combine(_root, "linked"), _directory.Combine("elsewhere"));
        MakeJunction(Path.Combine(_root, "alice"), _directory.Combine("elsewhere", "alice"));

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(0, 0, 0, 1, 0), run.Partials);
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(throughAFolder));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task AClipFolderThatIsItselfAJunction_IsCleaned()
    {
        // The clip folder may be a link (an admin's folder on another drive); what lies below it may not.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junctions.");
        var target = _directory.Combine("other-drive");
        var stale = Old(Path.Combine("other-drive", "alice", Stale));
        var linked = _directory.Combine("linked-clips");
        MakeJunction(linked, target);
        var store = Store(linked);

        var run = await Janitor(store).RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(1, 0, 0, 1, 0), run.Partials);
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task TurnedOff_EveryPartialStays_AndTheFilesWaitingToBeDeletedAreStillRetried()
    {
        var store = Store(_root);
        var stale = Old(Path.Combine("clips", "alice", Stale));
        var waiting = Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mkv"));
        await store.AddPendingFileDeleteAsync(waiting, Ct);

        var run = await Janitor(store, new PluginConfiguration { CleanUpPartialFilesOnStartup = false }).RunAtStartupAsync(Ct);

        Assert.Null(run.Partials);
        Assert.True(File.Exists(stale));
        Assert.Equal(1, run.PendingDeletes.Deleted);
        Assert.False(File.Exists(waiting));
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Message.Contains("CleanUpPartialFilesOnStartup is off", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.True(new PluginConfiguration().CleanUpPartialFilesOnStartup);
    }

    [Fact]
    public async Task ANewerSchemasData_KeepsEveryFile()
    {
        // The .partial janitor is skipped while the data is read-only; here the newer file records the clip
        // folder, so only the gate keeps the janitor out.
        var file = _directory.Combine("data", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { Version = 3, ClipRoots = new[] { _root }, Bookmarks = Array.Empty<object>() }), Ct);
        _store = BookmarkStore.Create(file, NullLogger.Instance);
        var stale = Old(Path.Combine("clips", "alice", Stale));
        Assert.Equal(BookmarkStoreReadOnlyReason.NewerVersion, _store.ReadOnlyReason);
        Assert.Equal([_root], _store.ClipRoots);
        var gateLogger = new ListLogger<BackgroundWriteGate>();
        var files = ClipFileSupport.Ops(_store, _fileLogger, _fileSystem);
        var janitor = new PartialFileJanitor(
            files,
            _store,
            TestData.ConfigurationProvider(),
            new BackgroundWriteGate(_store, gateLogger),
            _logger,
            _started);

        var run = await janitor.RunAtStartupAsync(Ct);

        Assert.Same(JanitorRun.Idle, run);
        Assert.True(File.Exists(stale));
        Assert.Empty(_fileSystem.Deletes);
        Assert.Contains(gateLogger.At(LogLevel.Warning), e => e.Message.StartsWith("The bookmark data is read-only (NewerVersion), so PartialFileJanitor is idle", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BookmarkStoreReadOnlyReason.NewerVersion)]
    [InlineData(BookmarkStoreReadOnlyReason.MigrationFailed)]
    public async Task ReadOnlyData_KeepsTheJanitorOutOfEveryClipFolder(BookmarkStoreReadOnlyReason reason)
    {
        var stale = Old(Path.Combine("clips", "alice", Stale));
        var store = new Mock<IBookmarkStore>();
        store.SetupGet(s => s.ReadOnlyReason).Returns(reason);
        store.SetupGet(s => s.ClipRoots).Returns([_root]);
        var janitor = ClipFileSupport.Janitor(store.Object, ClipFileSupport.Ops(store.Object, _fileLogger, _fileSystem), startedUtc: _started, logger: _logger);

        var run = await janitor.RunAtStartupAsync(Ct);

        Assert.Same(JanitorRun.Idle, run);
        Assert.True(File.Exists(stale));
        store.VerifyGet(s => s.ClipRoots, Times.Never);
        store.VerifyGet(s => s.PendingFileDeletes, Times.Never);
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task DeleteStalePartial_RefusesAnyOtherName_EvenWhenAskedDirectly()
    {
        var store = Store(_root);
        var files = ClipFileSupport.Ops(store, _fileLogger, _fileSystem);
        var notOurs = Old(Path.Combine("clips", "alice", "movie.mkv.partial"));
        var finished = Old(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mkv"));
        var outside = Old(Path.Combine("library", Stale));

        var results = new[]
        {
            await files.DeleteStalePartialAsync(notOurs, Ct),
            await files.DeleteStalePartialAsync(finished, Ct),
            await files.DeleteStalePartialAsync(outside, Ct)
        };

        Assert.All(results, r => Assert.Equal(ClipFileOutcome.Refused, r.Outcome));
        Assert.Equal([ClipPathVerdict.NotAClipFile, ClipPathVerdict.NotAClipFile, ClipPathVerdict.OutsideClipFolders], results.Select(r => r.Check.Verdict));
        Assert.True(File.Exists(notOurs) && File.Exists(finished) && File.Exists(outside));
        Assert.Empty(_fileSystem.Deletes);
        Assert.Equal(3, _fileLogger.At(LogLevel.Warning).Count());
    }

    [Fact]
    public void TheRunsStart_IsWhenTheJanitorIsCreated_NotTheProcesssStart()
    {
        // Jellyfin 10.11 restarts in the same process (Program.cs, _restartOnShutdown), with new services: the janitor of
        // the new run is created at its start, so an earlier run's leftover is stale even though the process is the same.
        var store = Store(_root);
        var before = DateTime.UtcNow;
        var janitor = new PartialFileJanitor(
            ClipFileSupport.Ops(store),
            store,
            TestData.ConfigurationProvider(),
            new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
            NullLogger<PartialFileJanitor>.Instance);
        var after = DateTime.UtcNow;

        Assert.InRange(janitor.StartedUtc, before, after);
        Assert.Equal(DateTimeKind.Utc, janitor.StartedUtc.Kind);
        Assert.True(janitor.StartedUtc > ClipFileSupport.TestProcessStartUtc);
    }

    [Fact]
    public async Task AnEarlierRunsLeftover_IsStale_AfterARestartInTheSameProcess()
    {
        // A .partial written after the process started, by the run before a restart in the same process (the dashboard's
        // Restart) whose own delete failed: the new run's janitor deletes it.
        var store = Store(_root);
        var afterTheProcessStarted = ClipFileSupport.TestProcessStartUtc + ((DateTime.UtcNow - ClipFileSupport.TestProcessStartUtc) / 2);
        var leftover = Touch(_directory.CreateFile(Path.Combine("clips", "alice", Stale), "partial"), afterTheProcessStarted);
        Assert.True(File.GetLastWriteTimeUtc(leftover) > ClipFileSupport.TestProcessStartUtc);
        var janitor = new PartialFileJanitor(
            ClipFileSupport.Ops(store, _fileLogger, _fileSystem),
            store,
            TestData.ConfigurationProvider(),
            new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
            NullLogger<PartialFileJanitor>.Instance);

        var run = await janitor.RunAtStartupAsync(Ct);

        Assert.Equal(new StalePartialCleanup(1, 0, 0, 1, 0), run.Partials);
        Assert.False(File.Exists(leftover));
    }

    public void Dispose()
    {
        _store?.Dispose();
        _directory.Dispose();
    }

    private static string Touch(string file, DateTime lastWriteUtc)
    {
        File.SetLastWriteTimeUtc(file, lastWriteUtc);
        return file;
    }

    private static string Hidden(string file)
    {
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
        return file;
    }

    // A junction made with mklink /J; the test is skipped when that fails.
    private static void MakeJunction(string junction, string target)
    {
        Directory.CreateDirectory(target);
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mklink.WaitForExit();
        Assert.SkipUnless(mklink.ExitCode == 0 && Directory.Exists(junction), "mklink /J failed.");
    }

    // A file written an hour before the server started: left by an earlier run.
    private string Old(string relative) => Touch(_directory.CreateFile(relative, "partial"), _started.AddHours(-1));

    // A file written since the server started.
    private string New(string relative) => Touch(_directory.CreateFile(relative, "partial"), _started.AddMinutes(1));

    private BookmarkStore Store(params string[] clipRoots)
    {
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("data", "bookmarks.json"), clipRoots);
        return _store;
    }

    private PartialFileJanitor Janitor(IBookmarkStore store, PluginConfiguration? configuration = null)
        => ClipFileSupport.Janitor(store, ClipFileSupport.Ops(store, _fileLogger, _fileSystem), configuration, _started, _logger);
}
