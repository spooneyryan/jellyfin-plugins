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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Clip rename and restores (with two cases found by mutation testing): a restored or imported record never shares a
/// clip file with another record. A record whose file was renamed away and whose old name another clip took since (another
/// user's, perhaps) is named after its renamed file again, through a chain that goes on past a name used again; one that
/// can't be keeps no file (Failed, <see cref="ClipRenameRelink.LetGoMessage"/>) and the other keeps its own. A record whose
/// own file is there is never relinked; the "earlier" rule stays within the clip's folder; a clip made after the rename is
/// its file's own; and the relink of the data as it is (startup, daily) judges no stranger.
/// </summary>
public sealed class ClipRenameRelinkTests : IDisposable
{
    private readonly ClipListFixture _fixture = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARestoreAfterARename_WhoseOldNameAnotherUsersClipTook_NamesTheRenamedFile_NeverTheOthers()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var snapshot = _fixture.Store.Documents.Current;
        var files = Files();
        var oldPath = record.Clip!.FilePath!;
        var renamed = await RenameAsync(files, _alice, record.Id, "Vault");

        // Bob's clip, made later, under the name Alice's rename freed (written as the worker writes it: the file, then its
        // record). The restore below forgets Bob's record (the snapshot is older), so only the history tells the files apart.
        await File.WriteAllTextAsync(oldPath, "bob's clip, another file", Ct);
        var bobs = _fixture.Make(_bob.Id, new ClipSpec { Name = "Bob's", OnDisk = false }) with { Clip = record.Clip with { FilePath = oldPath, CompletedAt = DateTime.UtcNow } };
        await _fixture.Store.AddAsync(bobs, Ct);
        var before = _fixture.Store.Documents.Current;

        var written = files.RelinkRestored(BookmarkAdminTools.AfterRestore(snapshot, before, DateTime.UtcNow, out _), before, out var relinked, out var letGo);

        Assert.Equal((1, 0), (relinked, letGo));
        Assert.Equal(renamed, written.Bookmarks.Single(b => b.Id == record.Id).Clip!.FilePath);

        // The restore at load (no data before): the same from the history alone.
        var atLoad = ClipRenameRelink.Apply(snapshot, null, files.History!.Entries, PhysicalClipFileSystem.Instance, NullLogger.Instance, restored: true, out relinked, out letGo);
        Assert.Equal((1, 0), (relinked, letGo));
        Assert.Equal(renamed, atLoad.Bookmarks.Single(b => b.Id == record.Id).Clip!.FilePath);
    }

    [Fact]
    public async Task AnImportMerge_OfARecordNamingAFileAnExistingRecordOwns_RelinksIt_OrLetsItGo_NeverSharing()
    {
        // Alice's clip renamed after the export; Bob's clip took the old name since. The export's record names the old path.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var exported = _fixture.Store.Get(record.Id)!;
        var files = Files();
        var oldPath = record.Clip!.FilePath!;
        var renamed = await RenameAsync(files, _alice, record.Id, "Vault");
        await File.WriteAllTextAsync(oldPath, "bob's clip", Ct);
        var bobs = _fixture.Make(_bob.Id, new ClipSpec { Name = "Bob's", OnDisk = false }) with { Clip = record.Clip with { FilePath = oldPath, CompletedAt = DateTime.UtcNow } };
        await _fixture.Store.AddAsync(bobs, Ct);
        var current = _fixture.Store.Documents.Current;

        // As the merge writes it: the export's (newer) record in place of the current one, Bob's kept.
        var merged = current with { Bookmarks = current.Bookmarks.Select(b => b.Id == record.Id ? exported with { UpdatedAt = DateTime.UtcNow } : b).ToList() };
        var written = files.RelinkRestored(merged, current, out var relinked, out var letGo);
        Assert.Equal((1, 0), (relinked, letGo));
        Assert.Equal(renamed, written.Bookmarks.Single(b => b.Id == record.Id).Clip!.FilePath);
        Assert.Equal(oldPath, written.Bookmarks.Single(b => b.Id == bobs.Id).Clip!.FilePath);

        // An export of another server's (a record this server never had, naming Bob's path): no rename to follow, so it lets
        // go of the file; Bob's record keeps it.
        var stranger = _fixture.Make(_alice.Id, new ClipSpec { Name = "From elsewhere", OnDisk = false }) with { Clip = bobs.Clip! };
        var withStranger = current with { Bookmarks = [.. current.Bookmarks, stranger] };
        written = files.RelinkRestored(withStranger, current, out relinked, out letGo);
        Assert.Equal((0, 1), (relinked, letGo));
        var let = written.Bookmarks.Single(b => b.Id == stranger.Id).Clip!;
        Assert.Equal((BookmarkClipStatus.Failed, ClipRenameRelink.LetGoMessage, (string?)null, (long?)null), (let.Status, let.Error, let.FilePath, let.FileSize));
        Assert.Equal(oldPath, written.Bookmarks.Single(b => b.Id == bobs.Id).Clip!.FilePath);
        Assert.Equal(1, written.Bookmarks.Count(b => b.Clip?.FilePath is { } p && ClipRootPaths.Comparer.Equals(p, oldPath)));
    }

    [Fact]
    public async Task TheChain_GoesOnPastANameUsedAgain_ToTheRenamedFile()
    {
        // Renamed twice; another clip took the middle name since. The restored record names the first path, which is gone.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var snapshot = _fixture.Store.Documents.Current;
        var files = Files();
        var middle = await RenameAsync(files, _alice, record.Id, "Vault");
        var last = await RenameAsync(files, _alice, record.Id, "Safe");
        await File.WriteAllTextAsync(middle, "another clip under the freed middle name", Ct);

        var written = files.RelinkRestored(snapshot, null, out var relinked, out _);

        Assert.Equal(1, relinked);
        Assert.Equal(last, written.Bookmarks.Single(b => b.Id == record.Id).Clip!.FilePath);
    }

    [Fact]
    public async Task ARecordWhoseOwnFileIsThere_IsNeverRelinked()
    {
        // Found by mutation testing: the data before names another file of the record in its folder, and the history a rename from
        // its path, but its own file is there (renamed back since): it keeps it.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var snapshot = _fixture.Store.Documents.Current;
        var files = Files();
        var vault = await RenameAsync(files, _alice, record.Id, "Vault");
        var back = await RenameAsync(files, _alice, record.Id, "Bank job");
        Assert.Equal(record.Clip!.FilePath, back);
        await File.WriteAllTextAsync(vault, "another file under the middle name", Ct);
        var before = _fixture.Store.Documents.Current;
        before = before with { Bookmarks = before.Bookmarks.Select(b => b.Id == record.Id ? b with { Clip = b.Clip! with { FilePath = vault } } : b).ToList() };

        foreach (var restored in new[] { true, false })
        {
            var written = ClipRenameRelink.Apply(snapshot, before, files.History!.Entries, PhysicalClipFileSystem.Instance, NullLogger.Instance, restored, out var relinked, out var letGo);
            Assert.Equal((0, 0), (relinked, letGo));
            Assert.Same(snapshot, written);
        }
    }

    [Fact]
    public async Task TheEarlierRule_StaysWithinTheClipsFolder()
    {
        // Found by mutation testing: the data before names a file of the record that is there, in another folder; the restored
        // record's file is gone. It isn't taken: a clip file is renamed within its own folder only.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var snapshot = _fixture.Store.Documents.Current;
        File.Delete(record.Clip!.FilePath!);
        var elsewhere = _fixture.Make(_alice.Id, new ClipSpec { Name = "Bank job", UserFolder = "another" });
        var before = _fixture.Store.Documents.Current;
        before = before with { Bookmarks = before.Bookmarks.Select(b => b.Id == record.Id ? b with { Clip = elsewhere.Clip } : b).ToList() };
        Assert.True(File.Exists(elsewhere.Clip!.FilePath));

        var written = Files().RelinkRestored(snapshot, before, out var relinked, out var letGo);

        Assert.Equal((0, 0), (relinked, letGo));
        Assert.Same(snapshot, written);
    }

    [Fact]
    public async Task AClipMadeAfterTheRename_IsItsFilesOwn_AndTheRelinkOfTheDataAsItIsJudgesNoStranger()
    {
        // A remake that got the name back after a rename of the clip's earlier file (in use, so still waiting to be deleted):
        // the history's rename from that name recorded the earlier file, yet the record's clip is newer than it.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var files = Files();
        var oldPath = record.Clip!.FilePath!;
        await RenameAsync(files, _alice, record.Id, "Vault");
        await File.WriteAllTextAsync(oldPath, "the remade clip", Ct);
        await _fixture.Store.UpdateAsync(record.Id, b => b with { Clip = b.Clip! with { FilePath = oldPath, CompletedAt = DateTime.UtcNow.AddSeconds(1) } }, Ct);
        var document = _fixture.Store.Documents.Current;

        var restored = files.RelinkRestored(document, null, out var relinked, out var letGo);
        Assert.Equal((0, 0), (relinked, letGo));
        Assert.Same(document, restored);

        // The same record with a clip from before the rename is a stranger to the file there now, in a restore only.
        var older = document with { Bookmarks = document.Bookmarks.Select(b => b.Id == record.Id ? b with { Clip = b.Clip! with { CompletedAt = DateTime.UtcNow.AddDays(-1) } } : b).ToList() };
        Assert.Same(older, ClipRenameRelink.Apply(older, null, files.History!.Entries, PhysicalClipFileSystem.Instance, NullLogger.Instance, restored: false, out relinked, out letGo));
        Assert.Equal((0, 0), (relinked, letGo));
        var written = ClipRenameRelink.Apply(older, null, files.History!.Entries, PhysicalClipFileSystem.Instance, NullLogger.Instance, restored: true, out relinked, out letGo);
        Assert.Equal(1, relinked);
        Assert.NotEqual(oldPath, written.Bookmarks.Single(b => b.Id == record.Id).Clip!.FilePath);
        Assert.Equal(0, await files.RelinkRenamedAsync(BackgroundWriter.Maintenance, Ct));
        Assert.Equal(oldPath, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
    }

    public void Dispose() => _fixture.Dispose();

    private ClipFileOps Files() => ClipFileSupport.Ops(_fixture.Store, states: _fixture.States);

    // The real rename (ClipFileRenamer), name and file: the record's new path.
    private async Task<string> RenameAsync(ClipFileOps files, User user, Guid id, string name)
    {
        var renamed = await new ClipFileRenamer(_fixture.Store, files, NullLogger.Instance).RenameAsync(user, id, changeName: true, name, renameFile: true, ifUpdatedAt: null, Ct);
        Assert.True(File.Exists(renamed.Clip!.FilePath));
        return renamed.Clip.FilePath!;
    }
}
