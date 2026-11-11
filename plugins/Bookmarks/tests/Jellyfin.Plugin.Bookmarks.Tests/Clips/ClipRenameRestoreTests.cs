using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A restore after a clip's rename: a snapshot from before the rename names the clip's old path,
/// which its file has left; the record names the renamed file again (from the data before an admin's restore, else from the
/// renames' history: the restore at load, a clip folder that was away), also through a chain of two renames, and never names a
/// file another record names, one waiting to be deleted, or another file at the renamed path. The admin's route and the
/// imports are in <c>Api/AdminToolsApiTests</c>.
/// </summary>
public sealed class ClipRenameRestoreTests : IDisposable
{
    private readonly ClipListFixture _fixture = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly ListLogger _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _fixture.Directory.Combine("data", "bookmarks.json");

    private string SnapshotFolder => _fixture.Directory.Combine("data", "snapshots");

    [Fact]
    public async Task TheRestoreAtLoad_OfASnapshotFromBeforeTwoRenames_NamesTheRenamedFile()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "First" }, Ct);
        await _fixture.Store.Documents.SnapshotAsync(SnapshotFolder, 7, Ct);
        var files = Files();
        await RenameAsync(files, record.Id, "Second");
        var renamed = await RenameAsync(files, record.Id, "Third");
        Assert.Equal(2, files.History!.Entries.Count);

        // bookmarks.json (and its .bak) lost: the kit restores the newest snapshot at load.
        DeleteStore();
        using var restored = BookmarkStore.Create(StoreFile, _logger);
        var stored = restored.Get(record.Id)!;

        Assert.Equal(StoreStatus.RestoredFromSnapshot, restored.Documents.Status);
        Assert.Equal(renamed, stored.Clip!.FilePath);
        Assert.True(File.Exists(stored.Clip.FilePath));
        Assert.Equal("First", stored.Name);
        Assert.Contains(_logger.Entries, l => l.Level == LogLevel.Information && l.Message.StartsWith("1 restored or imported clips named a path their file had left", StringComparison.Ordinal));

        // Saved so: the next start reads it named.
        using var reread = BookmarkStore.Create(StoreFile, NullLogger.Instance);
        Assert.Equal(renamed, reread.Get(record.Id)!.Clip!.FilePath);
    }

    [Fact]
    public async Task AnAdminsRestore_TakesThePathTheDataHadBefore_ElseTheHistorys()
    {
        // What RestoreSnapshotAsync writes (BookmarkAdminTools.AfterRestore, then the relink), on the documents themselves.
        var kept = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Kept" }, Ct);
        var gone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Gone later", Start = 120, End = 150 }, Ct);
        var snapshot = _fixture.Store.Documents.Current;
        var files = Files();
        var keptNow = await RenameAsync(files, kept.Id, "Kept renamed");
        var goneNow = await RenameAsync(files, gone.Id, "Gone renamed");

        // The second record isn't in the data before the restore (deleted with its clip kept, then purged as a record): only
        // the history knows where its file went.
        await _fixture.Store.UpdateDocumentAsync(d => d with { Bookmarks = d.Bookmarks.Where(b => b.Id != gone.Id).ToList() }, Ct);
        var before = _fixture.Store.Documents.Current;

        var written = files.RelinkRenamed(BookmarkAdminTools.AfterRestore(snapshot, before, DateTime.UtcNow, out _), before, out var relinked);

        Assert.Equal(2, relinked);
        Assert.Equal(keptNow, written.Bookmarks.Single(b => b.Id == kept.Id).Clip!.FilePath);
        Assert.Equal(goneNow, written.Bookmarks.Single(b => b.Id == gone.Id).Clip!.FilePath);
    }

    [Fact]
    public async Task ARelink_NeverNamesAFileAnotherRecordNames_NorOneWaitingToBeDeleted_NorAnotherFileAtThatPath()
    {
        var shared = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Shared" }, Ct);
        var waiting = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Waiting", Start = 120, End = 150 }, Ct);
        var replaced = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Replaced", Start = 200, End = 230 }, Ct);
        var snapshot = _fixture.Store.Documents.Current;
        var files = Files();
        var sharedNow = await RenameAsync(files, shared.Id, "Shared renamed");
        var waitingNow = await RenameAsync(files, waiting.Id, "Waiting renamed");
        var replacedNow = await RenameAsync(files, replaced.Id, "Replaced renamed");

        // Another record of the snapshot names the first one's renamed path; the second's is waiting to be deleted; the third's
        // holds another file now (another size).
        var other = _fixture.Make(_alice.Id, new ClipSpec { Name = "Other", FileName = Path.GetFileName(sharedNow), OnDisk = false, Start = 300, End = 330 });
        var restored = snapshot with
        {
            Bookmarks = [.. snapshot.Bookmarks, other],
            PendingFileDeletes = [new PendingFileDelete { Path = waitingNow, SinceUtc = DateTime.UtcNow, Attempts = 1 }]
        };
        File.Delete(replacedNow);
        await File.WriteAllTextAsync(replacedNow, "another file, of another size", Ct);

        var written = files.RelinkRenamed(restored, null, out var relinked);

        Assert.Equal(0, relinked);
        Assert.Same(restored, written);
    }

    [Fact]
    public async Task ARestoreAtLoadWhileTheClipFolderIsAway_IsPutRightOnceItIsBack_AtTheNextStart()
    {
        var usb = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(usb);
        await _fixture.Store.AddClipRootAsync(usb, Ct);
        await ClipFileSupport.MarkClipFolderAsync(_fixture.Store, usb);
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "On the stick", Folder = Path.Combine(usb, "alice") }, Ct);
        await _fixture.Store.Documents.SnapshotAsync(SnapshotFolder, 7, Ct);
        var renamed = await RenameAsync(Files(), record.Id, "Renamed");
        ClipFileSupport.Unplug(usb);
        DeleteStore();

        using var restored = BookmarkStore.Create(StoreFile, _logger);
        Assert.Equal(record.Clip!.FilePath, restored.Get(record.Id)!.Clip!.FilePath);

        ClipFileSupport.PlugIn(usb);
        var files = ClipFileSupport.Ops(restored);
        var run = await ClipFileSupport.Janitor(restored, files).RunAtStartupAsync(Ct);

        Assert.Equal(1, run.Relinked);
        Assert.Equal(renamed, restored.Get(record.Id)!.Clip!.FilePath);
        Assert.Equal(0, await files.RelinkRenamedAsync(BackgroundWriter.Maintenance, Ct));
    }

    [Fact]
    public async Task TheHistory_KeepsItsRenamesByCountOnly_HoweverOld_AndEachOnce()
    {
        // No retention by days (the missing-clip retention, 30 days by default, was shorter
        // than the snapshots and exports a restore starts from), only the newest MaxEntries.
        var history = new ClipRenameHistory(_fixture.Directory.Combine("data"), PhysicalClipFileSystem.Instance, NullLogger.Instance);
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        history.Add(new ClipRenameEntry { BookmarkId = Guid.NewGuid(), From = @"C:\clips\a.mkv", To = @"C:\clips\b.mkv" }, now.AddDays(-400));
        history.Add(new ClipRenameEntry { BookmarkId = Guid.NewGuid(), From = @"C:\clips\c.mkv", To = @"C:\clips\d.mkv" }, now.AddDays(-2));
        var twice = new ClipRenameEntry { BookmarkId = Guid.NewGuid(), From = @"C:\clips\e.mkv", To = @"C:\clips\f.mkv" };
        history.Add(twice, now);
        history.Add(twice, now);

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal(0, history.Prune());
        Assert.Equal(3, new ClipRenameHistory(_fixture.Directory.Combine("data"), PhysicalClipFileSystem.Instance, NullLogger.Instance).Entries.Count);

        // Over its size (a file with more, as a later version might leave it), the oldest go first.
        var many = history.Entries.Concat(Enumerable.Range(0, ClipRenameHistory.MaxEntries).Select(i => new ClipRenameHistoryEntry
        {
            BookmarkId = Guid.NewGuid(),
            From = @"C:\clips\g" + i + ".mkv",
            To = @"C:\clips\h" + i + ".mkv",
            FinishedUtc = now.AddMinutes(i)
        })).ToList();
        await File.WriteAllTextAsync(history.FilePath, System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Renames = many }), Ct);
        Assert.Equal(3, history.Prune());
        var kept = new ClipRenameHistory(_fixture.Directory.Combine("data"), PhysicalClipFileSystem.Instance, NullLogger.Instance).Entries;
        Assert.Equal(ClipRenameHistory.MaxEntries, kept.Count);
        Assert.DoesNotContain(kept, e => e.From == @"C:\clips\a.mkv");
    }

    public void Dispose() => _fixture.Dispose();

    private ClipFileOps Files() => ClipFileSupport.Ops(_fixture.Store, states: _fixture.States);

    // The real rename (ClipFileRenamer), name and file: the record's new path.
    private async Task<string> RenameAsync(ClipFileOps files, Guid id, string name)
    {
        var renamed = await new ClipFileRenamer(_fixture.Store, files, NullLogger.Instance).RenameAsync(_alice, id, changeName: true, name, renameFile: true, ifUpdatedAt: null, Ct);
        Assert.True(File.Exists(renamed.Clip!.FilePath));
        return renamed.Clip.FilePath!;
    }

    private void DeleteStore()
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(StoreFile)!, "bookmarks.json*"))
        {
            File.Delete(file);
        }
    }
}
