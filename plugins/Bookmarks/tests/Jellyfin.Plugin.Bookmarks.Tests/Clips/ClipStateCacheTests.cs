using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The clip files' states the My Clips list judges from (whether a file exists, and the clip folder's answer, both
/// cached): the guard's verdict, worked out within one request's reading from the clip folder's
/// answer (once per folder and reading) and each step's attributes (the user's folder once for all its files), and kept 30 s
/// whole; a clip folder unplugged between two requests never read as files that are gone; a file Bookmarks moves into place
/// or deletes judged again at once, also when a reading was under way; an error kept like any verdict; a file's last write
/// time; the verdicts of paths no longer asked for dropped once there are many.
/// </summary>
public sealed class ClipStateCacheTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly Jellyfin.Plugin.Bookmarks.Storage.BookmarkStore _store;
    private readonly CountingFileSystem _files = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ClipStateCache _cache;

    public ClipStateCacheTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(Path.Combine(_root, "alice"));
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("data", "bookmarks.json"), _root);
        _cache = new ClipStateCache(_store, _files, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AReading_AsksTheClipFolderOnce_AndTheUsersFolderOnce_ForAllItsFiles_AndTheVerdictsAreKept30Seconds()
    {
        var paths = Enumerable.Range(0, 20).Select(i => _directory.CreateFile(Path.Combine("clips", "alice", $"Heat (1995) - {i} [00.01.00-00.01.30].mp4"))).ToList();

        var first = _cache.Read();
        Assert.All(paths, path => Assert.Equal(ClipPathVerdict.Exists, first.Check(path).Verdict));
        Assert.Equal(1, _files.Calls("folder", _root));
        Assert.Equal(1, _files.Calls("attributes", Path.Combine(_root, "alice")));
        Assert.All(paths, path => Assert.Equal(1, _files.Calls("attributes", path)));

        // Another request within the 30 s: the verdicts kept, nothing asked.
        var second = _cache.Read();
        Assert.All(paths, path => Assert.Equal(ClipPathVerdict.Exists, second.Check(path).Verdict));
        Assert.Equal(1 + 1 + 20, _files.TotalCalls);

        // After 30 s each is worked out again, from one new look at the folders.
        _clock.Advance(ClipStateCache.Lifetime);
        var third = _cache.Read();
        Assert.All(paths, path => Assert.Equal(ClipPathVerdict.Exists, third.Check(path).Verdict));
        Assert.Equal(2, _files.Calls("folder", _root));
        Assert.Equal(2, _files.Calls("attributes", Path.Combine(_root, "alice")));
        Assert.All(paths, path => Assert.Equal(2, _files.Calls("attributes", path)));
    }

    [Fact]
    public void AVerdict_IsKeptAsItWas_UntilItsTimeIsUp()
    {
        var path = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mp4"));
        Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(path).Verdict);

        File.Delete(path);
        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(path).Verdict);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ClipPathVerdict.Missing, _cache.Read().Check(path).Verdict);

        // The folder taken away: within the 30 s the file is still judged as before; then the folder is offline.
        ClipFileSupport.Unplug(_root);
        Assert.Equal(ClipPathVerdict.Missing, _cache.Read().Check(path).Verdict);
        _clock.Advance(ClipStateCache.Lifetime);
        Assert.Equal(ClipPathVerdict.FolderUnavailable, _cache.Read().Check(path).Verdict);
        ClipFileSupport.PlugIn(_root);
    }

    [Fact]
    public void AClipFolderUnpluggedBetweenTwoRequests_IsNeverReadAsFilesThatAreGone()
    {
        // A staggered timeline found in testing: the folder's answer from an earlier request ("there"), the folder unplugged, then
        // a file never asked about before. A verdict pairing that old answer with the new "nothing at the file" read Missing
        // (RootAvailable true) for up to 30 s, and the page offered Forget, Remake and selection for an offline row. Each
        // verdict is one look now.
        var asked = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) - Asked [00.01.00-00.01.30].mp4"));
        var later = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) - Later [00.02.00-00.02.30].mp4"));
        var alsoLater = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) - Also later [00.03.00-00.03.30].mp4"));
        Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(asked).Verdict);

        ClipFileSupport.Unplug(_root);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var reading = _cache.Read();
        var first = reading.Check(later);
        var second = reading.Check(alsoLater);

        Assert.Equal(ClipPathVerdict.FolderUnavailable, first.Verdict);
        Assert.False(first.IsInClipFolders);
        Assert.Equal(ClipPathVerdict.FolderUnavailable, second.Verdict);
        Assert.Equal(2, _files.Calls("folder", _root));

        // The clip judged before the unplug keeps its verdict (it was there) until its time is up, then is offline: never
        // missing in between.
        foreach (var seconds in new[] { 10, 14, 1 })
        {
            Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(asked).Verdict);
            _clock.Advance(TimeSpan.FromSeconds(seconds));
        }

        Assert.Equal(ClipPathVerdict.FolderUnavailable, _cache.Read().Check(asked).Verdict);
        ClipFileSupport.PlugIn(_root);
    }

    [Fact]
    public async Task AFileBookmarksMovesIntoPlace_OrDeletes_IsJudgedAgainAtOnce()
    {
        // Found in testing: a clip made again under the name of a file that had gone (the planner's name is free again) read
        // Missing for up to 30 s after it was done, because the verdict "missing" was kept; the page's Forget then deleted the
        // new file. ClipFileOps tells the cache about every file it moves into place or deletes.
        var files = ClipFileSupport.Ops(_store, fileSystem: _files, states: _cache);
        var path = Path.Combine(_root, "alice", "Heat (1995) - Bank job [00.01.00-00.01.30].mkv");
        Assert.Equal(ClipPathVerdict.Missing, _cache.Read().Check(path).Verdict);
        Assert.Equal(ClipPathVerdict.Missing, _cache.Read().Check(path).Verdict);
        var partial = _directory.CreateFile(Path.Combine("clips", "alice", Path.GetFileName(path) + ClipFileGuard.PartialExtension), "clip");

        var moved = await files.MoveAsync(partial, path, Guid.NewGuid(), Ct);

        Assert.Equal(ClipFileOutcome.Done, moved.Outcome);
        Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(path).Verdict);
        var time = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, time);
        Assert.Equal(time, _cache.Read().LastWriteTimeUtc(path));

        // Deleted by Bookmarks: missing at once, and its time forgotten.
        var deleted = await files.DeleteAsync(path, Guid.NewGuid(), Ct);
        Assert.Equal(ClipFileOutcome.Done, deleted.Outcome);
        Assert.Equal(ClipPathVerdict.Missing, _cache.Read().Check(path).Verdict);
        Assert.Null(_cache.Read().LastWriteTimeUtc(path));

        // A rename (Rename file goes through MoveAsync too): both paths judged again.
        var renamed = Path.Combine(_root, "alice", "Heat (1995) - Renamed [00.01.00-00.01.30].mkv");
        var source = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) - Old [00.01.00-00.01.30].mkv"));
        Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(source).Verdict);
        Assert.Equal(ClipPathVerdict.Missing, _cache.Read().Check(renamed).Verdict);
        Assert.Equal(ClipFileOutcome.Done, (await files.MoveAsync(source, renamed, Guid.NewGuid(), Ct)).Outcome);
        Assert.Equal(ClipPathVerdict.Missing, _cache.Read().Check(source).Verdict);
        Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(renamed).Verdict);
    }

    [Fact]
    public void AReadingUnderWay_WhenBookmarksChangesAClipFile_KeepsNothingItFound()
    {
        // A request asks about the path just before the worker moves the finished clip there: what it found ("nothing there")
        // is its answer, but it isn't kept, so the next request sees the file.
        var path = Path.Combine(_root, "alice", "Heat (1995) - Bank job [00.01.00-00.01.30].mkv");
        _files.OnAttributes = asked =>
        {
            if (string.Equals(asked, path, StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
            {
                // The answer is "nothing there"; the move and its Forget come right after it, before the reading keeps it.
                _files.OnAttributes = null;
                return () =>
                {
                    File.WriteAllText(path, "clip");
                    _cache.Forget(path);
                };
            }

            return null;
        };

        var during = _cache.Read().Check(path);
        var after = _cache.Read().Check(path);

        Assert.Equal(ClipPathVerdict.Missing, during.Verdict);
        Assert.Equal(ClipPathVerdict.Exists, after.Verdict);

        // A reading begun before a change keeps none of its verdicts, of any path: the next one asks again.
        var other = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) - Other [00.02.00-00.02.30].mkv"));
        var begun = _cache.Read();
        _cache.Forget(path);
        Assert.Equal(ClipPathVerdict.Exists, begun.Check(other).Verdict);
        Assert.Equal(ClipPathVerdict.Exists, _cache.Read().Check(other).Verdict);
        Assert.Equal(2, _files.Calls("attributes", other));
    }

    [Fact]
    public void AnError_IsAVerdictLikeAnyOther_KeptAndReadAsUnreadable()
    {
        var path = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mp4"));
        _files.AttributesFailure = p => p.EndsWith(".mp4", StringComparison.Ordinal) ? new IOException("The device is not ready.") : null;

        var first = _cache.Read().Check(path);
        var second = _cache.Read().Check(path);

        Assert.Equal(ClipPathVerdict.Unreadable, first.Verdict);
        Assert.Equal(ClipPathVerdict.Unreadable, second.Verdict);
        Assert.IsType<IOException>(second.Error);
        Assert.Equal(1, _files.Calls("attributes", path));
    }

    [Fact]
    public void ThePathsTheGuardRefuses_AreRefusedWithoutAskingTheFileSystemOfThem()
    {
        var reading = _cache.Read();
        Assert.Equal(ClipPathVerdict.NotAClipFile, reading.Check(Path.Combine(_root, "alice", "notes.txt")).Verdict);
        Assert.Equal(ClipPathVerdict.OutsideClipFolders, reading.Check(_directory.Combine("elsewhere", "Heat [00.01.00-00.01.30].mp4")).Verdict);
        Assert.Equal(ClipPathVerdict.NotAPath, reading.Check(" ").Verdict);
        Assert.Equal(ClipPathVerdict.NotAPath, reading.Check(null).Verdict);
        Assert.Equal(0, _files.TotalCalls);
    }

    [Fact]
    public void ALastWriteTime_IsKept30Seconds()
    {
        var path = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mp4"));
        var first = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, first);

        Assert.Equal(first, _cache.Read().LastWriteTimeUtc(path));
        File.SetLastWriteTimeUtc(path, first.AddDays(1));
        Assert.Equal(first, _cache.Read().LastWriteTimeUtc(path));

        _clock.Advance(ClipStateCache.Lifetime);
        Assert.Equal(first.AddDays(1), _cache.Read().LastWriteTimeUtc(path));
        Assert.Null(_cache.Read().LastWriteTimeUtc(Path.Combine(_root, "alice", "gone [00.01.00-00.01.30].mp4")));
    }

    [Fact]
    public void TheVerdictsOfPathsNoLongerAskedFor_AreDropped_OnceThereAreMany()
    {
        // A file system in memory: every path a missing file in the user's folder, so each check keeps one verdict.
        var memory = new MemoryFileSystem(_root);
        var cache = new ClipStateCache(_store, memory, _clock);
        var reading = cache.Read();
        for (var i = 0; i < ClipStateCache.SweepAbove; i++)
        {
            reading.Check(Path.Combine(_root, "alice", $"c{i} [00.01.00-00.01.30].mp4"));
        }

        _clock.Advance(ClipStateCache.Lifetime);
        cache.Read().Check(Path.Combine(_root, "alice", "one more [00.01.00-00.01.30].mp4"));

        // The 10,000 expired verdicts went with the sweep; asking again asks the file system again.
        var before = memory.Calls;
        cache.Read().Check(Path.Combine(_root, "alice", "c0 [00.01.00-00.01.30].mp4"));
        Assert.True(memory.Calls > before);
        Assert.True(memory.Kept(cache) <= 2, $"{memory.Kept(cache)} verdicts and times kept");
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    /// <summary>The real file system, counting the guard's calls by path, with a hook after an attributes call.</summary>
    private sealed class CountingFileSystem : IClipFileSystem
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

        public Func<string, Exception?>? AttributesFailure { get; set; }

        /// <summary>Gets or sets what to do right after an attributes call has its answer, given the path.</summary>
        public Func<string, Action?>? OnAttributes { get; set; }

        public int TotalCalls => _calls.Values.Sum();

        public int Calls(string kind, string path) => _calls.GetValueOrDefault(kind + ":" + path);

        public FileAttributes? GetAttributes(string path)
        {
            _calls.AddOrUpdate("attributes:" + path, 1, (_, n) => n + 1);
            if (AttributesFailure?.Invoke(path) is { } failure)
            {
                throw failure;
            }

            var after = OnAttributes?.Invoke(path);
            var answer = PhysicalClipFileSystem.Instance.GetAttributes(path);
            after?.Invoke();
            return answer;
        }

        public bool FolderExists(string path)
        {
            _calls.AddOrUpdate("folder:" + path, 1, (_, n) => n + 1);
            return PhysicalClipFileSystem.Instance.FolderExists(path);
        }

        public DateTime? GetLastWriteTimeUtc(string path)
        {
            _calls.AddOrUpdate("time:" + path, 1, (_, n) => n + 1);
            return PhysicalClipFileSystem.Instance.GetLastWriteTimeUtc(path);
        }

        public void DeleteFile(string path) => PhysicalClipFileSystem.Instance.DeleteFile(path);

        public void MoveFile(string source, string destination) => PhysicalClipFileSystem.Instance.MoveFile(source, destination);
        public void ReplaceFile(string source, string destination) => PhysicalClipFileSystem.Instance.ReplaceFile(source, destination);

        public IEnumerable<ClipFolderFile> ListClipFolder(string folder) => throw new NotSupportedException();
    }

    /// <summary>A clip folder with an empty user folder, in memory.</summary>
    private sealed class MemoryFileSystem : IClipFileSystem
    {
        private readonly string _root;
        private int _calls;

        public MemoryFileSystem(string root)
        {
            _root = root;
        }

        public int Calls => Volatile.Read(ref _calls);

        // What the cache keeps: read through its private dictionaries' counts, which a test of a sweep is about.
        public int Kept(ClipStateCache cache)
            => new[] { "_verdicts", "_writeTimes" }
                .Sum(name => ((System.Collections.ICollection)typeof(ClipStateCache).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cache)!).Count);

        public FileAttributes? GetAttributes(string path)
        {
            Interlocked.Increment(ref _calls);
            return string.Equals(path, Path.Combine(_root, "alice"), StringComparison.OrdinalIgnoreCase) ? FileAttributes.Directory : null;
        }

        public bool FolderExists(string path)
        {
            Interlocked.Increment(ref _calls);
            return string.Equals(path, _root, StringComparison.OrdinalIgnoreCase);
        }

        public DateTime? GetLastWriteTimeUtc(string path) => null;

        public void DeleteFile(string path) => throw new NotSupportedException();

        public void MoveFile(string source, string destination) => throw new NotSupportedException();
        public void ReplaceFile(string source, string destination) => throw new NotSupportedException();

        public IEnumerable<ClipFolderFile> ListClipFolder(string folder) => throw new NotSupportedException();
    }
}
