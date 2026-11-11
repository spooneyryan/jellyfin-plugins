using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// Bookmarks' keyed store on the kit's JsonDocumentStore at schema 2. The kit's conformance suite
/// runs in <see cref="BookmarkStoreConformanceTests"/>, the 1→2 upgrade in <see cref="BookmarkStoreMigrationTests"/>.
/// </summary>
public sealed class BookmarkStoreTests : IDisposable
{
    private static readonly Guid Alice = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid Bob = new("ff71b896-f205-45ff-936a-c2393f6091f6");

    private readonly TempDirectory _directory = new();

    private string StorePath => _directory.Combine("bookmarks", "bookmarks.json");

    [Fact]
    public async Task AddAsync_PersistsEverySchema2FieldAcrossInstances()
    {
        var bookmark = TestData.CreateBookmark(Alice) with
        {
            Name = "Bank job",
            Note = "Watch the reflection",
            MediaSourceId = "abc",
            AudioStreamIndex = 2,
            Identity = new IdentitySnapshot
            {
                ItemId = Guid.NewGuid(),
                Kind = "Episode",
                Name = "The Detail",
                Year = 2002,
                ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "1" },
                SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "79126" },
                SeriesName = "The Wire",
                Season = 1,
                Episode = 2,
                SeriesId = Guid.NewGuid(),
                SeasonId = Guid.NewGuid(),
                RunTimeTicks = 3_000 * TestData.OneSecond,
                Path = @"Q:\TV\The Wire\S01E02.mkv"
            },
            Origin = BookmarkOrigin.JellyfinEnhanced,
            DetachedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            RelinkedUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            RelinkStrength = MatchStrength.Strong,
            DeletedUtc = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc),
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed,
                Mode = BookmarkClipMode.Fast,
                FilePath = "x.mkv",
                FileSize = 42,
                MissingSinceUtc = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc),
                EstimatedBytes = 4096
            }
        };

        using (var store = CreateStore())
        {
            await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
        }

        using var reloaded = CreateStore();
        var loaded = reloaded.Get(bookmark.Id)!;
        Assert.Equal(bookmark.Clip, loaded.Clip);
        Assert.Equal(bookmark.Identity, loaded.Identity);
        Assert.Equal(bookmark with { Clip = null, Identity = BookmarkRecordRules.NoIdentity }, loaded with { Clip = null, Identity = BookmarkRecordRules.NoIdentity });
        Assert.Equal(@"Q:\TV\The Wire\S01E02.mkv", loaded.Identity.Path);
        Assert.Equal(StoreStatus.Ok, reloaded.Documents.Status);
    }

    [Fact]
    public async Task File_UsesReadableEnumNames_AndSchema2()
    {
        using var store = CreateStore();
        await store.AddAsync(
            TestData.CreateBookmark(Alice) with
            {
                Origin = BookmarkOrigin.JellyfinEnhanced,
                RelinkedUtc = DateTime.UtcNow,
                RelinkStrength = MatchStrength.Strong,
                Clip = new ClipInfo { Status = BookmarkClipStatus.Running }
            },
            TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(StorePath, TestContext.Current.CancellationToken);
        Assert.Contains("\"Version\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"Running\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Origin\": \"JellyfinEnhanced\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RelinkStrength\": \"Strong\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Document_WritesEverySchema2Field_InThePlansOrder()
    {
        using var store = CreateStore();
        await store.AddAsync(TestData.CreateBookmark(Alice), TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken))!.AsObject();

        // MoveJournalId is null until the first catch-up, so it is left out.
        Assert.Equal(
            ["Version", "ClipRoots", "UserPreferences", "ImportLedger", "KnownUsers", "LastMoveSeq", "PendingFileDeletes", "Bookmarks"],
            root.Select(p => p.Key));
        var record = root["Bookmarks"]![0]!.AsObject();
        Assert.Equal(
            ["Id", "UserId", "ItemId", "StartTicks", "EndTicks", "ItemName", "Identity", "Origin", "AttachedUtc", "CreatedAt", "UpdatedAt"],
            record.Select(p => p.Key));
    }

    [Fact]
    public async Task GetLive_FiltersByUserAndItem_AndLeavesTombstonesOut()
    {
        var movie = Guid.NewGuid();
        using var store = CreateStore();
        var live = TestData.CreateBookmark(Alice, itemId: movie);
        var tombstone = TestData.CreateBookmark(Alice, itemId: movie) with
        {
            DeletedUtc = DateTime.UtcNow,
            Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = "kept.mkv" }
        };
        await store.AddAsync(live, TestContext.Current.CancellationToken);
        await store.AddAsync(tombstone, TestContext.Current.CancellationToken);
        await store.AddAsync(TestData.CreateBookmark(Alice), TestContext.Current.CancellationToken);
        await store.AddAsync(TestData.CreateBookmark(Bob, itemId: movie), TestContext.Current.CancellationToken);

        Assert.Equal(2, store.GetLive(Alice, null).Count);
        Assert.Equal(live.Id, Assert.Single(store.GetLive(Alice, movie)).Id);
        Assert.Single(store.GetLive(Bob, null));
        Assert.Empty(store.GetLive(Guid.NewGuid(), null));
        Assert.Equal(4, store.GetAll().Count);
        Assert.Same(tombstone, store.Get(tombstone.Id));
    }

    [Fact]
    public async Task GetClipRecords_IncludesTombstones_ForOneUserOrAll()
    {
        using var store = CreateStore();
        var clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = "a.mkv" };
        var withClip = TestData.CreateBookmark(Alice) with { Clip = clip };
        var tombstone = TestData.CreateBookmark(Alice) with { DeletedUtc = DateTime.UtcNow, Clip = clip };
        var bobsClip = TestData.CreateBookmark(Bob) with { Clip = clip };
        foreach (var bookmark in new[] { withClip, tombstone, bobsClip, TestData.CreateBookmark(Alice) })
        {
            await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
        }

        Assert.Equal(new[] { withClip.Id, tombstone.Id }.Order(), store.GetClipRecords(Alice).Select(b => b.Id).Order());
        Assert.Equal(3, store.GetClipRecords(null).Count);
    }

    [Fact]
    public async Task AddAsync_DuplicateId_Throws()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        using var store = CreateStore();
        await store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(bookmark, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writes_ByTheOwner_RecordTheOwnersName_InTheSameWrite()
    {
        // KnownUsers is refreshed on writes; only the owner's own write names it.
        var alice = TestData.CreateUser("alice");
        var bookmark = TestData.CreateBookmark(alice.Id);
        using var store = CreateStore();

        await store.AddAsync(TestData.CreateBookmark(Bob), TestContext.Current.CancellationToken, writer: alice);
        Assert.Empty(store.KnownUsers);

        await store.AddAsync(bookmark, TestContext.Current.CancellationToken, writer: alice);
        Assert.Equal("alice", store.KnownUsers.NameOf(alice.Id));

        alice.Username = "alice2";
        await store.UpdateAsync(bookmark.Id, b => b with { Note = "x" }, TestContext.Current.CancellationToken, writer: alice);

        using var reloaded = CreateStore();
        Assert.Equal("alice2", reloaded.KnownUsers.NameOf(alice.Id));
        var json = await File.ReadAllTextAsync(StorePath, TestContext.Current.CancellationToken);
        Assert.Contains("\"" + alice.Id.ToString("N") + "\": \"alice2\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_AppliesAndPersists()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        using (var store = CreateStore())
        {
            await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
            var updated = await store.UpdateAsync(bookmark.Id, b => b with { Name = "changed" }, TestContext.Current.CancellationToken);
            Assert.Equal("changed", updated?.Name);
        }

        using var reloaded = CreateStore();
        Assert.Equal("changed", reloaded.Get(bookmark.Id)?.Name);
    }

    [Fact]
    public async Task UpdateAsync_ReturningSameInstance_SkipsWrite()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        using var store = CreateStore();
        await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
        File.Delete(StorePath);

        var result = await store.UpdateAsync(bookmark.Id, b => b, TestContext.Current.CancellationToken, writer: TestData.CreateUser());

        Assert.Same(bookmark, result);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task UpdateAsync_ChangingId_Throws()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        using var store = CreateStore();
        await store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(bookmark.Id, b => b with { Id = Guid.NewGuid() }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_Missing_ReturnsNull()
    {
        using var store = CreateStore();

        Assert.Null(await store.UpdateAsync(Guid.NewGuid(), b => b with { Note = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveAsync_RemovesAndPersists()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        using (var store = CreateStore())
        {
            await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
            Assert.Same(bookmark, await store.RemoveAsync(bookmark.Id, TestContext.Current.CancellationToken));
            Assert.Null(await store.RemoveAsync(bookmark.Id, TestContext.Current.CancellationToken));
        }

        using var reloaded = CreateStore();
        Assert.Null(reloaded.Get(bookmark.Id));
    }

    [Fact]
    public async Task UpdateOrRemoveAsync_ReplacesOrRemoves_InOneWrite_AndSaysWhich()
    {
        // The one write of a delete that keeps a tombstone or removes the record, decided under the lock.
        var alice = TestData.CreateUser("alice");
        var kept = TestData.CreateBookmark(alice.Id);
        var removed = TestData.CreateBookmark(alice.Id);
        using (var store = CreateStore())
        {
            await store.AddAsync(kept, TestContext.Current.CancellationToken);
            await store.AddAsync(removed, TestContext.Current.CancellationToken);

            var replaced = await store.UpdateOrRemoveAsync(kept.Id, b => b with { DeletedUtc = DateTime.UtcNow }, TestContext.Current.CancellationToken, alice);
            Assert.Same(kept, replaced.Before);
            Assert.NotNull(replaced.After?.DeletedUtc);
            Assert.True(replaced.Found);
            Assert.True(replaced.Changed);
            Assert.False(replaced.Removed);
            Assert.Same(replaced.After, store.Get(kept.Id));
            Assert.Equal("alice", store.KnownUsers.NameOf(alice.Id));

            var gone = await store.UpdateOrRemoveAsync(removed.Id, _ => null, TestContext.Current.CancellationToken);
            Assert.Same(removed, gone.Before);
            Assert.Null(gone.After);
            Assert.True(gone.Removed);
            Assert.True(gone.Changed);
            Assert.Null(store.Get(removed.Id));
        }

        using var reloaded = CreateStore();
        Assert.NotNull(reloaded.Get(kept.Id)?.DeletedUtc);
        Assert.Null(reloaded.Get(removed.Id));
    }

    [Fact]
    public async Task UpdateOrRemoveAsync_SameInstance_OrAMissingId_WritesNothing()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        using var store = CreateStore();
        await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
        File.Delete(StorePath);

        var unchanged = await store.UpdateOrRemoveAsync(bookmark.Id, b => b, TestContext.Current.CancellationToken);
        var missing = await store.UpdateOrRemoveAsync(Guid.NewGuid(), _ => throw new InvalidOperationException("not called"), TestContext.Current.CancellationToken);

        Assert.Same(bookmark, unchanged.Before);
        Assert.Same(bookmark, unchanged.After);
        Assert.False(unchanged.Changed);
        Assert.False(unchanged.Removed);
        Assert.False(missing.Found);
        Assert.False(missing.Changed);
        Assert.Null(missing.After);
        Assert.False(File.Exists(StorePath));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateOrRemoveAsync(bookmark.Id, b => b with { Id = Guid.NewGuid() }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(bookmark.Id, _ => null!, TestContext.Current.CancellationToken));
        Assert.Same(bookmark, store.Get(bookmark.Id));
    }

    [Fact]
    public async Task ConcurrentWrites_AreAllPersisted()
    {
        var userId = Guid.NewGuid();
        using (var store = CreateStore())
        {
            await Task.WhenAll(Enumerable.Range(0, 50).Select(
                _ => store.AddAsync(TestData.CreateBookmark(userId), TestContext.Current.CancellationToken)));
        }

        using var reloaded = CreateStore();
        Assert.Equal(50, reloaded.GetLive(userId, null).Count);
    }

    [Fact]
    public async Task CorruptFile_WithNothingInItsPlace_IsLeftAsItIs_AndTheDataReadOnly_UntilAnAdminStartsEmpty()
    {
        // Until Bookmarks 1.2 the file was moved aside and the store started empty on its own; now only an admin does that.
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, "{ not json", TestContext.Current.CancellationToken);

        using var store = CreateStore();

        Assert.Empty(store.GetAll());
        Assert.True(store.IsReadOnly);
        Assert.Equal(BookmarkStoreReadOnlyReason.DataUnreadable, store.ReadOnlyReason);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(StorePath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "bookmarks.json.corrupt-*"));

        await store.Documents.RecoverAsync(Path.Combine(Path.GetDirectoryName(StorePath)!, "snapshots"), null, startEmpty: true, TestContext.Current.CancellationToken);
        Assert.False(store.IsReadOnly);
        Assert.Empty(store.GetAll());
        var corrupt = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "bookmarks.json.corrupt-*"));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(corrupt, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptFile_IsRecoveredFromTheBackup()
    {
        var first = TestData.CreateBookmark(Guid.NewGuid());
        var second = TestData.CreateBookmark(Guid.NewGuid());
        using (var store = CreateStore())
        {
            await store.AddAsync(first, TestContext.Current.CancellationToken);
            await store.AddAsync(second, TestContext.Current.CancellationToken);
        }

        // The backup holds the state before the last write.
        Assert.True(File.Exists(StorePath + ".bak"));
        await File.WriteAllTextAsync(StorePath, "\0\0\0", TestContext.Current.CancellationToken);

        using var reloaded = CreateStore();

        Assert.NotNull(reloaded.Get(first.Id));
        Assert.Null(reloaded.Get(second.Id));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "bookmarks.json.corrupt-*"));
    }

    [Fact]
    public void MissingFile_StartsEmpty()
    {
        using var store = CreateStore();

        Assert.Empty(store.GetAll());
        Assert.Empty(store.ClipRoots);
        Assert.Empty(store.ImportLedger);
        Assert.Empty(store.KnownUsers);
        Assert.Empty(store.PendingFileDeletes);
        Assert.Null(store.Documents.Current.MoveJournalId);
        Assert.Equal(0, store.Documents.Current.LastMoveSeq);
    }

    [Fact]
    public void Schema2Fixture_LoadsEveryField()
    {
        WriteFile(StoreFixtures.Read(StoreFixtures.Schema2));

        using var store = CreateStore();
        var document = store.Documents.Current;

        Assert.Equal(StoreStatus.Ok, store.Documents.Status);
        Assert.Equal(StoreFixtures.Schema2Records, store.GetAll().Count);
        Assert.Equal([@"C:\ProgramData\Jellyfin\Server\data\bookmarks\clips", @"D:\Clips"], store.ClipRoots);
        Assert.Equal(new Guid("6d0e1c9f-0a4b-4e57-9c1e-2b3a4c5d6e7f"), document.MoveJournalId);
        Assert.Equal(17, document.LastMoveSeq);
        Assert.Equal("alice", store.KnownUsers.NameOf(Alice));
        Assert.Equal("bob", store.KnownUsers.NameOf(Bob));
        Assert.True(store.GetPreferences(Alice)!.AskForName);
        Assert.Null(store.GetPreferences(Bob)!.AskForName);
        Assert.True(store.GetPreferences(Bob)!.PauseWhileNaming);
        var ledger = Assert.Single(store.ImportLedger);
        Assert.Equal("Imported", ledger.Value.Outcome);
        Assert.Equal(3, Assert.Single(store.PendingFileDeletes).Attempts);

        var byName = store.GetAll().ToDictionary(b => b.Name ?? b.Note ?? b.ItemName);
        Assert.Equal(BookmarkOrigin.Native, byName["Bank job"].Origin);
        Assert.Equal(7_000_000, byName["Bank job"].Clip!.EstimatedBytes);
        Assert.Equal("Heat", byName["Bank job"].Identity.Name);
        Assert.Equal(BookmarkOrigin.JellyfinEnhanced, byName["Imported from JE"].Origin);
        Assert.NotNull(byName["Imported from JE"].DetachedUtc);
        Assert.Equal(MatchStrength.Strong, byName["Relinked"].RelinkStrength);
        Assert.NotNull(byName["Kept clip"].DeletedUtc);
        Assert.NotNull(byName["Kept clip"].Clip!.MissingSinceUtc);
        Assert.Equal(BookmarkOrigin.Adopted, byName["Adopted clip"].Origin);
        Assert.Equal(Guid.Empty, byName["Adopted clip"].ItemId);
        Assert.False(BookmarkRecordRules.HasLibraryItem(byName["Adopted clip"]));
        Assert.Null(byName["A note that stays a note\non two lines"].Name);

        // Fields of a later version survive on the document, a record, its clip and its identity.
        Assert.True(document.Extra!.ContainsKey("FutureDocumentField"));
        Assert.True(byName["Bank job"].Extra!.ContainsKey("FutureRecordField"));
        Assert.True(byName["Bank job"].Clip!.Extra!.ContainsKey("FutureClipField"));
        Assert.True(byName["Bank job"].Identity.Extra!.ContainsKey("FutureIdentityField"));
    }

    [Fact]
    public async Task Schema2Fixture_IsExactlyWhatThisVersionWrites()
    {
        // The format is pinned: the fixture read and written again gives the same bytes, and so does a write that adds
        // a bookmark and removes it again.
        var original = StoreFixtures.Read(StoreFixtures.Schema2);
        WriteFile(original);
        var bookmark = TestData.CreateBookmark(Alice);

        using (var store = CreateStore())
        {
            Assert.Equal(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(store.Documents.Current, JsonStoreFormat.Options)));
            await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
            await store.RemoveAsync(bookmark.Id, TestContext.Current.CancellationToken);
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownBookmarkFields_SurviveAChangeToThatBookmark()
    {
        // Bookmark.Extra keeps fields this version doesn't know, also through the with-copies of an update.
        var bookmark = TestData.CreateBookmark(Alice);
        using (var store = CreateStore())
        {
            await store.AddAsync(bookmark, TestContext.Current.CancellationToken);
        }

        var root = JsonNode.Parse(await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken))!.AsObject();
        root["Bookmarks"]![0]!["FutureField"] = JsonNode.Parse("""{ "Kept": [1, "two"] }""");
        await File.WriteAllBytesAsync(StorePath, JsonSerializer.SerializeToUtf8Bytes(root, JsonStoreFormat.Options), TestContext.Current.CancellationToken);

        using (var store = CreateStore())
        {
            Assert.True(store.Get(bookmark.Id)!.Extra!.ContainsKey("FutureField"));
            await store.UpdateAsync(bookmark.Id, b => b with { Note = "edited again" }, TestContext.Current.CancellationToken);
        }

        var written = JsonNode.Parse(await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken))!;
        var saved = written["Bookmarks"]![0]!;
        Assert.Equal("edited again", (string?)saved["Note"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "Kept": [1, "two"] }"""), saved["FutureField"]));
    }

    [Fact]
    public async Task HandEditedFile_WithARepeatedId_KeepsTheLast_AndTheNextWriteDropsTheOthers()
    {
        // As in 1.0: the last record with an id wins.
        var first = TestData.CreateBookmark(Guid.NewGuid()) with { Note = "first" };
        var last = first with { Note = "last" };
        WriteDocument(BookmarkStoreDocument.CreateEmpty() with { Bookmarks = [first, last] });

        using var store = CreateStore();
        Assert.Equal("last", Assert.Single(store.GetAll()).Note);
        await store.AddAsync(TestData.CreateBookmark(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Documents.Current.Bookmarks.Count);
        Assert.Equal("last", store.Get(first.Id)!.Note);
    }

    [Fact]
    public async Task FileWithNullCollections_LoadsEmpty()
    {
        WriteFile(Encoding.UTF8.GetBytes(
            """{ "Version": 2, "ClipRoots": null, "UserPreferences": null, "ImportLedger": null, "KnownUsers": null, "PendingFileDeletes": null, "Bookmarks": null }"""));

        using var store = CreateStore();

        Assert.Equal(StoreStatus.Ok, store.Documents.Status);
        Assert.Empty(store.GetAll());
        Assert.Empty(store.ClipRoots);
        Assert.Empty(store.ImportLedger);
        Assert.Empty(store.KnownUsers);
        Assert.Empty(store.PendingFileDeletes);
        Assert.Null(store.GetPreferences(Alice));
        await store.AddAsync(TestData.CreateBookmark(Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void RecordWithoutAnIdentity_GetsAnEmptyOne()
    {
        // DTO mapping never meets a null Identity, whether the field is missing or null.
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        WriteFile(Encoding.UTF8.GetBytes(
            $$"""{ "Version": 2, "Bookmarks": [ { "Id": "{{id1}}", "UserId": "{{Alice}}", "ItemId": "{{Guid.NewGuid()}}" }, { "Id": "{{id2}}", "Identity": null } ] }"""));

        using var store = CreateStore();

        Assert.Same(BookmarkRecordRules.NoIdentity, store.Get(id1)!.Identity);
        Assert.Same(BookmarkRecordRules.NoIdentity, store.Get(id2)!.Identity);
        Assert.Equal(string.Empty, store.Get(id1)!.Identity.Kind);
        Assert.Equal(BookmarkOrigin.Native, store.Get(id1)!.Origin);
    }

    [Fact]
    public void Store_IsTheKitStoreAtVersion2_WithTheOneMigration()
    {
        using var store = CreateStore();

        Assert.Equal(2, store.Documents.CurrentVersion);
        Assert.Equal("bookmarks", store.Documents.StoreName);
        Assert.Equal(Path.GetFullPath(StorePath), store.Documents.FilePath);
        Assert.Same(JsonStoreFormat.Options, store.Documents.SerializerOptions);
        var migration = Assert.Single(BookmarkStore.Migrations(BookmarkStore.StandaloneMigration(StorePath)));
        Assert.Equal(1, migration.FromVersion);
    }

    [Theory]
    [InlineData(@"C:\clips\", @"C:\clips")]
    [InlineData(@"  C:\clips  ", @"C:\clips")]
    [InlineData(@"C:\clips\..\clips\admin\..", @"C:\clips")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\server\share\clips\", @"\\server\share\clips")]
    public async Task AddClipRootAsync_StoresTheNormalisedFolder(string given, string stored)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");
        using var store = CreateStore();

        Assert.True(await store.AddClipRootAsync(given, TestContext.Current.CancellationToken));

        Assert.Equal([stored], store.ClipRoots);
    }

    [Fact]
    public async Task AddClipRootAsync_AFolderAlreadyRecorded_WritesNothing()
    {
        var folder = _directory.Combine("clips");
        using var store = CreateStore();
        Assert.True(await store.AddClipRootAsync(folder, TestContext.Current.CancellationToken));
        var written = await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken);

        Assert.False(await store.AddClipRootAsync(folder + Path.DirectorySeparatorChar, TestContext.Current.CancellationToken));
        if (OperatingSystem.IsWindows())
        {
            Assert.False(await store.AddClipRootAsync(folder.ToUpperInvariant(), TestContext.Current.CancellationToken));
        }

        Assert.Equal(written, await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(StorePath + ".bak"));
        Assert.Single(store.ClipRoots);
    }

    [Fact]
    public async Task AddClipRootAsync_KeepsAtMost50Folders_AndRefusesABlankPath()
    {
        using var store = CreateStore();
        for (var i = 0; i < BookmarkStoreDocument.MaxClipRoots; i++)
        {
            await store.AddClipRootAsync(_directory.Combine("clips" + i), TestContext.Current.CancellationToken);
        }

        var full = await Assert.ThrowsAsync<PluginException>(() => store.AddClipRootAsync(_directory.Combine("one more"), TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Conflict, full.Kind);
        Assert.False(await store.AddClipRootAsync(_directory.Combine("clips0"), TestContext.Current.CancellationToken));
        Assert.Equal(BookmarkStoreDocument.MaxClipRoots, store.ClipRoots.Count);

        var blank = await Assert.ThrowsAsync<PluginException>(() => store.AddClipRootAsync("  ", TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Invalid, blank.Kind);
    }

    [Fact]
    public async Task Preferences_AreKeptPerUser_UnderNFormatIds()
    {
        using (var store = CreateStore())
        {
            await store.SetPreferencesAsync(Alice, new BookmarkUserPreferences { AskForName = true }, TestContext.Current.CancellationToken);
            await store.SetPreferencesAsync(Bob, new BookmarkUserPreferences { PauseWhileNaming = true }, TestContext.Current.CancellationToken);
        }

        var json = await File.ReadAllTextAsync(StorePath, TestContext.Current.CancellationToken);
        Assert.Contains("\"" + Alice.ToString("N") + "\": {", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Alice.ToString("D"), json, StringComparison.Ordinal);

        using var reloaded = CreateStore();
        Assert.Equal(new BookmarkUserPreferences { AskForName = true }, reloaded.GetPreferences(Alice));
        Assert.Equal(new BookmarkUserPreferences { PauseWhileNaming = true }, reloaded.GetPreferences(Bob));
        Assert.Null(reloaded.GetPreferences(Guid.NewGuid()));

        // The same values write nothing; null goes back to the server defaults.
        File.Delete(StorePath);
        await reloaded.SetPreferencesAsync(Alice, new BookmarkUserPreferences { AskForName = true }, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(StorePath));
        await reloaded.SetPreferencesAsync(Alice, null, TestContext.Current.CancellationToken);
        Assert.Null(reloaded.GetPreferences(Alice));
        Assert.NotNull(reloaded.GetPreferences(Bob));
    }

    [Fact]
    public async Task Preferences_KeepTheFieldsALaterVersionStored()
    {
        WriteFile(Encoding.UTF8.GetBytes(
            $$"""{ "Version": 2, "UserPreferences": { "{{Alice:N}}": { "AskForName": false, "FutureOption": "kept" } } }"""));

        using (var store = CreateStore())
        {
            await store.SetPreferencesAsync(Alice, new BookmarkUserPreferences { AskForName = true }, TestContext.Current.CancellationToken);
            Assert.True(store.GetPreferences(Alice)!.AskForName);
        }

        var saved = JsonNode.Parse(await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken))!["UserPreferences"]![Alice.ToString("N")]!;
        Assert.True((bool)saved["AskForName"]!);
        Assert.Equal("kept", (string?)saved["FutureOption"]);
    }

    [Fact]
    public void Preferences_KeyedByAnyGuidFormat_AreRead_AndOthersDropped()
    {
        WriteFile(Encoding.UTF8.GetBytes(
            $$"""{ "Version": 2, "UserPreferences": { "{{Alice:D}}": { "AskForName": false }, "not-a-user": { "AskForName": true }, "{{Bob:N}}": null } }"""));

        using var store = CreateStore();

        Assert.False(store.GetPreferences(Alice)!.AskForName);
        Assert.Single(store.Documents.Current.UserPreferences);
    }

    [Fact]
    public async Task AJellyfinEnhancedImport_AddsAndReplacesRecords_WithTheLedgerAndItsRun_InOneWrite()
    {
        // The importer applies its plan in one UpdateDocumentAsync (the store's import method was folded into it).
        var existing = TestData.CreateBookmark(Alice) with { Name = "before" };
        using var store = CreateStore();
        await store.AddAsync(existing, TestContext.Current.CancellationToken);
        var beforeImport = await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken);

        var added = TestData.CreateBookmark(Alice) with { Origin = BookmarkOrigin.JellyfinEnhanced, Name = "from JE" };
        var imported = new BookmarkImportLedgerEntry { BookmarkId = added.Id, ImportedAt = DateTime.UtcNow, Outcome = "Imported", RecordUpdatedAt = added.UpdatedAt };
        var plan = new JeImportPlan(
            [],
            [],
            0,
            [added],
            [existing with { Name = "after" }],
            new Dictionary<string, BookmarkImportLedgerEntry> { ["je:b:Bm_2"] = imported, ["je:a:Bm_1"] = imported with { Outcome = "Duplicate", BookmarkId = Guid.Empty } },
            new Dictionary<string, BookmarkImportRun> { ["je:b"] = new() { AppliedUtc = DateTime.UtcNow, Found = 2, Imported = 1 } },
            new Dictionary<Guid, string> { [Alice] = "alice" });
        await store.UpdateDocumentAsync(plan.ApplyTo, TestContext.Current.CancellationToken);

        Assert.Equal(2, store.GetAll().Count);
        Assert.Equal("after", store.Get(existing.Id)!.Name);
        Assert.Equal(BookmarkOrigin.JellyfinEnhanced, store.Get(added.Id)!.Origin);
        Assert.Equal(["je:a:Bm_1", "je:b:Bm_2"], store.ImportLedger.Keys);
        Assert.Equal(added.UpdatedAt, store.ImportLedger["je:b:Bm_2"].RecordUpdatedAt);
        Assert.Equal(1, Assert.Single(store.ImportRuns).Value.Imported);
        Assert.Equal("alice", store.KnownUsers.NameOf(Alice));

        // One write: the backup is the file from before the import; a plan that writes nothing writes nothing.
        Assert.Equal(beforeImport, await File.ReadAllBytesAsync(StorePath + ".bak", TestContext.Current.CancellationToken));
        var document = store.Documents.Current;
        var empty = new JeImportPlan([], [], 0, [], [], new Dictionary<string, BookmarkImportLedgerEntry>(), new Dictionary<string, BookmarkImportRun>(), new Dictionary<Guid, string>());
        Assert.False(empty.Writes);
        Assert.Same(document, empty.ApplyTo(document));

        // The run and the ledger's new field survive a round trip through the file.
        using var reread = CreateStore();
        Assert.Equal(added.UpdatedAt, reread.ImportLedger["je:b:Bm_2"].RecordUpdatedAt);
        Assert.Equal(2, reread.ImportRuns["je:b"].Found);
    }

    [Fact]
    public async Task RefreshKnownUsersAsync_NamesStoredUsers_KeepsUnknownOnes_DropsUsersWithoutRecords()
    {
        var gone = Guid.NewGuid();
        var formerOwner = Guid.NewGuid();
        using var store = CreateStore();
        await store.AddAsync(TestData.CreateBookmark(Alice), TestContext.Current.CancellationToken);
        await store.AddAsync(TestData.CreateBookmark(gone) with { DeletedUtc = DateTime.UtcNow, Clip = new ClipInfo { FilePath = "x.mkv" } }, TestContext.Current.CancellationToken);
        await store.UpdateDocumentAsync(
            d => d with { KnownUsers = d.KnownUsers.With(Alice, "old").With(gone, "gone user").With(formerOwner, "former") },
            TestContext.Current.CancellationToken);

        Assert.True(await store.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [Alice] = "alice", [Bob] = "bob" }, TestContext.Current.CancellationToken));

        Assert.Equal("alice", store.KnownUsers.NameOf(Alice));
        Assert.Equal("gone user", store.KnownUsers.NameOf(gone));
        Assert.Null(store.KnownUsers.NameOf(formerOwner));
        Assert.Null(store.KnownUsers.NameOf(Bob));
        Assert.False(await store.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [Alice] = "alice" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PendingFileDeletes_AreListedOncePerPath_CountAttempts_AndStopAt1000()
    {
        using var store = CreateStore();
        Assert.True(await store.AddPendingFileDeleteAsync(@"C:\clips\a.mkv", TestContext.Current.CancellationToken));
        Assert.True(await store.AddPendingFileDeleteAsync(OperatingSystem.IsWindows() ? @"C:\CLIPS\A.mkv" : @"C:\clips\a.mkv", TestContext.Current.CancellationToken));

        var entry = Assert.Single(store.PendingFileDeletes);
        Assert.Equal(2, entry.Attempts);
        Assert.Equal(@"C:\clips\a.mkv", entry.Path);

        await store.UpdateDocumentAsync(
            d => d with { PendingFileDeletes = [.. Enumerable.Range(0, BookmarkStoreDocument.MaxPendingFileDeletes).Select(i => new PendingFileDelete { Path = @"C:\clips\" + i + ".mkv", Attempts = 1 })] },
            TestContext.Current.CancellationToken);
        Assert.False(await store.AddPendingFileDeleteAsync(@"C:\clips\one more.mkv", TestContext.Current.CancellationToken));
        Assert.True(await store.AddPendingFileDeleteAsync(@"C:\clips\5.mkv", TestContext.Current.CancellationToken));
        Assert.Equal(BookmarkStoreDocument.MaxPendingFileDeletes, store.PendingFileDeletes.Count);
    }

    [Fact]
    public async Task UpdatePendingFileDeletesAsync_DropsGoneFiles_AndKeepsTheRest()
    {
        using var store = CreateStore();
        await store.AddPendingFileDeleteAsync(@"C:\clips\gone.mkv", TestContext.Current.CancellationToken);
        await store.AddPendingFileDeleteAsync(@"C:\clips\locked.mkv", TestContext.Current.CancellationToken);

        var left = await store.UpdatePendingFileDeletesAsync(
            e => e.Path.EndsWith("gone.mkv", StringComparison.Ordinal) ? null : e with { Attempts = e.Attempts + 1 },
            TestContext.Current.CancellationToken);

        var locked = Assert.Single(left);
        Assert.Equal(2, locked.Attempts);
        using var reloaded = CreateStore();
        Assert.Equal(locked, Assert.Single(reloaded.PendingFileDeletes));
    }

    [Fact]
    public async Task UpdateDocumentAsync_OrdersRecords_KeepsTheLastOfAnId_AndRefusesTooManyClipFolders()
    {
        var older = TestData.CreateBookmark(Alice) with { CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var newer = TestData.CreateBookmark(Alice) with { CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
        using var store = CreateStore();

        await store.UpdateDocumentAsync(d => d with { Bookmarks = [newer, older, newer with { Name = "last" }], MoveJournalId = Guid.NewGuid(), LastMoveSeq = 5 }, TestContext.Current.CancellationToken);

        Assert.Equal([older.Id, newer.Id], store.Documents.Current.Bookmarks.Select(b => b.Id));
        Assert.Equal("last", store.Get(newer.Id)!.Name);
        Assert.Equal(5, store.Documents.Current.LastMoveSeq);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateDocumentAsync(
            d => d with { ClipRoots = [.. Enumerable.Range(0, BookmarkStoreDocument.MaxClipRoots + 1).Select(i => @"C:\clips" + i)] },
            TestContext.Current.CancellationToken));
        Assert.Empty(store.ClipRoots);
    }

    [Fact]
    public async Task ReadOnlyStore_RefusesEveryWrite_AndLeavesTheFileAlone()
    {
        // A newer schema: every write is 503, including the document fields.
        var newer = Encoding.UTF8.GetBytes("""{ "Version": 3, "Bookmarks": [] }""");
        WriteFile(newer);
        using var store = CreateStore();

        Assert.True(store.IsReadOnly);
        foreach (var write in new Func<Task>[]
        {
            () => store.AddAsync(TestData.CreateBookmark(Alice), TestContext.Current.CancellationToken),
            () => store.AddClipRootAsync(_directory.Combine("clips"), TestContext.Current.CancellationToken),
            () => store.SetPreferencesAsync(Alice, new BookmarkUserPreferences(), TestContext.Current.CancellationToken),
            () => store.AddPendingFileDeleteAsync(@"C:\x.mkv", TestContext.Current.CancellationToken),
            () => store.RefreshKnownUsersAsync(new Dictionary<Guid, string>(), TestContext.Current.CancellationToken),
            () => store.UpdateDocumentAsync(d => d with { LastMoveSeq = 1 }, TestContext.Current.CancellationToken)
        })
        {
            Assert.Equal(PluginErrorKind.Unavailable, (await Assert.ThrowsAsync<PluginException>(write)).Kind);
        }

        Assert.Equal(newer, await File.ReadAllBytesAsync(StorePath, TestContext.Current.CancellationToken));
    }

    public void Dispose() => _directory.Dispose();

    private BookmarkStore CreateStore() => BookmarkStore.Create(StorePath, NullLogger.Instance);

    private void WriteFile(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllBytes(StorePath, bytes);
    }

    private void WriteDocument(BookmarkStoreDocument document) => WriteFile(JsonSerializer.SerializeToUtf8Bytes(document, JsonStoreFormat.Options));
}
