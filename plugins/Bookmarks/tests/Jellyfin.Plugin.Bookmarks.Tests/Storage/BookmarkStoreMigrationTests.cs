using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Storage.Migrations;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// The 1→2 upgrade of <c>bookmarks.json</c>: the file Bookmarks 1.0.0 wrote, every rule of the step, and
/// the data 1.0 could have written that the other fixtures don't show. The kit's conformance suite adds the generic
/// checks (the <c>.v1-*</c> copy, the immediate save, a failed step).
/// </summary>
public sealed class BookmarkStoreMigrationTests : IDisposable
{
    private const string SandboxClips = @"C:\src\jellyfin-plugins\.sandbox\default\data\data\bookmarks\clips";

    private static readonly Guid Admin = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid Viewer = new("ff71b896-f205-45ff-936a-c2393f6091f6");
    private static readonly Guid Viewer2 = new("0038ee0f-9abc-4d55-9ef5-abcf8520c9bf");

    private readonly TempDirectory _directory = new();

    private string StorePath => _directory.Combine("bookmarks", "bookmarks.json");

    [Fact]
    public void Bookmarks100File_IsUpgraded_EveryRecordKeepsItsValues()
    {
        var original = WriteFile(StoreFixtures.Read(StoreFixtures.Bookmarks100));
        var names = new Dictionary<Guid, string> { [Admin] = "admin", [Viewer] = "viewer" };

        using var store = CreateStore(new BookmarkStoreMigration1To2(SandboxClips, () => null, id => names.GetValueOrDefault(id)));

        Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        Assert.Equal(1, store.Documents.MigratedFrom);
        Assert.False(store.IsReadOnly);
        Assert.Equal(10, store.GetAll().Count);
        Assert.Equal(8, store.GetLive(Admin, null).Count);
        Assert.Single(store.GetLive(Viewer, null));
        Assert.Single(store.GetLive(Viewer2, null));

        // Every 1.0 note here is short and on one line, except the two-line one.
        Assert.Equal(
            new Dictionary<string, (string? Name, string? Note)>
            {
                ["93ed04fa-39fc-462f-9449-6365e4cb1cb2"] = ("webcheck", null),
                ["5f7ef9bc-14bc-4e9a-a9c6-07c0b46a9210"] = ("Bank job - the \"reflection\" in the window", null),
                ["2b12464b-6f44-46a2-9eb2-c8b89caf60bd"] = (null, "Line one\nLine two"),
                ["bff5bbea-1ebe-4669-9bc3-b5ae14fcbc6a"] = ("Amélie, café scène ✂ 日本語", null),
                ["c4d65a56-3c29-4c94-b049-c51c01bd40f6"] = (null, null),
                ["41f2477c-3533-468a-a0f0-591efcc3153a"] = (null, null),
                ["dba803fe-6070-4905-86fc-807b75d6f602"] = ("edited note", null),
                ["0b12c492-e988-44b7-b1d7-54b6d0721618"] = ("clip deleted", null),
                ["72863457-d86e-4215-bbc4-cebb8e3b9c5f"] = ("viewer's note", null),
                ["1a0ab894-21ba-4872-8508-c7005526ce6f"] = (null, null),
            },
            store.GetAll().ToDictionary(b => b.Id.ToString(), b => (b.Name, b.Note)));

        // Every 1.0 field keeps its value, and the identity is built from ItemType, ItemName and ItemProviderIds.
        AssertEvery10ValueKept(original, store);

        Assert.Equal("900001", store.Get(new Guid("2b12464b-6f44-46a2-9eb2-c8b89caf60bd"))!.Identity.ProviderIds["Tmdb"]);
        Assert.Equal("Episode", store.Get(new Guid("41f2477c-3533-468a-a0f0-591efcc3153a"))!.Identity.Kind);
        Assert.Equal("Video", store.Get(new Guid("dba803fe-6070-4905-86fc-807b75d6f602"))!.Identity.Kind);

        // The document: both clips lie under the default folder, the users this server knows are named, nothing else yet.
        var document = store.Documents.Current;
        Assert.Equal([SandboxClips], document.ClipRoots);
        Assert.Equal(2, document.KnownUsers.Count);
        Assert.Equal("admin", document.KnownUsers.NameOf(Admin));
        Assert.Equal("viewer", document.KnownUsers.NameOf(Viewer));
        Assert.Null(document.KnownUsers.NameOf(Viewer2));
        Assert.Empty(document.UserPreferences);
        Assert.Empty(document.ImportLedger);
        Assert.Empty(document.PendingFileDeletes);
        Assert.Null(document.MoveJournalId);
        Assert.Equal(0, document.LastMoveSeq);
        Assert.Null(document.Extra);

        // The file 1.0.0 wrote is kept twice, as .v1-<ts> and .bak, byte for byte; the store file is schema 2.
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Folder, "bookmarks.json.v1-*"))));
        Assert.Equal(original, File.ReadAllBytes(StorePath + ".bak"));
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllBytes(StorePath))!["Version"]!);
    }

    [Fact]
    public void Bookmarks10DevelopmentBuildsFile_IsUpgraded_EveryRecordKeepsItsValues()
    {
        // A real sandbox v1 file: what the 1.0 development build wrote in its own sandbox before the
        // release. Older than 1.0.0's shape in places: finished clips without FileStartTicks/FileEndTicks, a cancelled
        // remake that kept its earlier file, and a cancelled job with no file at all. Every value must survive.
        var original = WriteFile(StoreFixtures.Read(StoreFixtures.Bookmarks10DevSandbox));
        var devAdmin = new Guid("bdba1ab0-b417-4388-bdef-05e1a9010226");
        var devViewer = new Guid("816fd8d2-d654-4a6e-8f77-b3fed4cbe646");

        using var store = CreateStore(new BookmarkStoreMigration1To2(_directory.Combine("clips"), () => null, id => id == devAdmin ? "admin" : null));

        Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        Assert.Equal(StoreFixtures.Bookmarks10DevSandboxRecords, store.GetAll().Count);
        Assert.Equal(13, store.GetLive(devAdmin, null).Count);
        Assert.Single(store.GetLive(devViewer, null));
        AssertEvery10ValueKept(original, store);

        // Every note there is short and on one line, so each becomes the name; bookmarks without one stay unnamed.
        Assert.Equal(
            ["After back", "Enter key save", "Fast copy", "Made in the editor", "Point of interest", "Restart test", "Whole thing"],
            store.GetAll().Where(b => b.Name is not null).Select(b => b.Name!).Order(StringComparer.Ordinal));
        Assert.All(store.GetAll(), b => Assert.Null(b.Note));

        // The clips as that build left them: ranges of the files unknown, the cancelled remake's file still recorded.
        var fastCopy = store.Get(new Guid("d3b40a3b-d6bb-4ef3-8bbd-c7f495df7de2"))!.Clip!;
        Assert.Equal((BookmarkClipStatus.Completed, BookmarkClipMode.Fast, 3591129L, (long?)null, (long?)null), (fastCopy.Status, fastCopy.Mode, fastCopy.FileSize!.Value, fastCopy.FileStartTicks, fastCopy.FileEndTicks));
        var restart = store.Get(new Guid("9c86d496-db38-4e4f-85e1-147dd8b76088"))!.Clip!;
        Assert.Equal(BookmarkClipStatus.Cancelled, restart.Status);
        Assert.EndsWith("Test Pattern (2020) - Restart test [00.00.00-00.02.59].mp4", restart.FilePath, StringComparison.Ordinal);
        Assert.Null(store.Get(new Guid("a3004b04-14dd-42a4-9993-ea21963103b2"))!.Clip!.FilePath);

        // The DTOs of those clips: a file without a known range maps as 1.0 mapped it.
        var dto = Jellyfin.Plugin.Bookmarks.Services.BookmarkDtoMapper.ToDto(store.Get(new Guid("d3b40a3b-d6bb-4ef3-8bbd-c7f495df7de2"))!, null);
        Assert.Equal("Fast copy", dto.Name);
        Assert.Equal(BookmarkClipStatus.Completed, dto.Clip!.Status);

        // Its clip folder lies outside this server's default one, so it is recorded; the known owner is named.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                [Path.GetFullPath(_directory.Combine("clips")), @"C:\src\jellyfin-plugin-bookmarks\.sandbox\jellyfin\data\bookmarks\clips\admin"],
                store.ClipRoots);
        }

        Assert.Equal("admin", Assert.Single(store.KnownUsers).Value);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Folder, "bookmarks.json.v1-*"))));
        Assert.Equal(original, File.ReadAllBytes(StorePath + ".bak"));
    }

    [Fact]
    public void Bookmarks100File_WithClipsOutsideTheDefaultFolder_RecordsTheirFolder()
    {
        WriteFile(StoreFixtures.Read(StoreFixtures.Bookmarks100));

        using var store = CreateStore(new BookmarkStoreMigration1To2(_directory.Combine("clips"), () => null, _ => null));

        // The default folder, then the folder of every clip file outside it (here the sandbox's per-user one).
        Assert.Equal([Path.GetFullPath(_directory.Combine("clips")), SandboxClips + @"\admin"], store.ClipRoots);
        Assert.Empty(store.KnownUsers);
    }

    [Fact]
    public void Bookmarks100File_ThroughThePluginsOwnFactory_UsesItsPathsSettingsAndUsers()
    {
        // BookmarkStoreMigration1To2.Create, as PluginServiceRegistrator builds it.
        WriteFile(StoreFixtures.Read(StoreFixtures.Bookmarks100));
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(p => p.DataPath).Returns(_directory.Path);
        var paths = new BookmarkPaths(new KitPaths(applicationPaths.Object, BookmarksKit.Options));
        var configured = _directory.Combine("library clips");
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(Admin)).Returns(new User("admin", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Admin });
        users.Setup(m => m.GetUserById(Viewer)).Throws(new InvalidOperationException("The user cache isn't ready."));
        var logger = new ListLogger();

        using var store = BookmarkStore.Create(
            paths.StoreFile,
            BookmarkStore.Migrations(BookmarkStoreMigration1To2.Create(paths, TestData.ConfigurationProvider(new PluginConfiguration { ClipOutputPath = "  " + configured + "  " }), () => users.Object, logger)),
            logger);

        Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        Assert.Equal(Path.Combine(_directory.Path, "bookmarks", "bookmarks.json"), paths.StoreFile);
        Assert.Equal([Path.Combine(_directory.Path, "bookmarks", "clips"), configured, SandboxClips + @"\admin"], store.ClipRoots);
        Assert.Equal("admin", store.KnownUsers.NameOf(Admin));
        Assert.Single(store.KnownUsers);

        // A user the server couldn't be asked about is left out with a Warning; the upgrade still succeeds.
        Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.Contains(Viewer.ToString(), StringComparison.Ordinal));
        users.Verify(m => m.GetUserById(Guid.Empty), Times.Never);
    }

    [Theory]
    [InlineData("Bank job", "Bank job", null)]
    [InlineData("  Bank job  ", "Bank job", null)]
    [InlineData("Bank\tjob", "Bank job", null)]
    [InlineData("Bank   job", "Bank job", null)]
    [InlineData("Line one\nLine two", null, "Line one\nLine two")]
    [InlineData("Line one\r\n", "Line one", null)]
    [InlineData("One\rtwo", null, "One\rtwo")]
    [InlineData("   ", null, "   ")]
    [InlineData("\u0007", null, "\u0007")]
    [InlineData("Cafe\u0301", "Café", null)]
    public void NoteRules_ShortSingleLineNotesBecomeNames(string note, string? name, string? keptNote)
    {
        var migrated = MigrateOne(new JsonObject { ["Note"] = note });

        Assert.Equal(name, migrated.Name);
        Assert.Equal(keptNote, migrated.Note);
    }

    [Fact]
    public void NoteRules_100CharactersBecomeAName_101StayANote()
    {
        var hundred = new string('x', 100);
        var hundredAndOne = new string('y', 101);

        Assert.Equal(hundred, MigrateOne(new JsonObject { ["Note"] = hundred }).Name);
        var kept = MigrateOne(new JsonObject { ["Note"] = hundredAndOne });
        Assert.Null(kept.Name);
        Assert.Equal(hundredAndOne, kept.Note);
    }

    [Fact]
    public void Identity_DefaultsForMissingOrOddItemFields()
    {
        // A record 1.0 could only have got by a hand edit: no ItemType, null or odd provider ids.
        var missing = MigrateOne(new JsonObject());
        Assert.Equal("Video", missing.Identity.Kind);
        Assert.Empty(missing.Identity.ProviderIds);

        var odd = MigrateOne(new JsonObject
        {
            ["ItemType"] = "  ",
            ["ItemProviderIds"] = new JsonObject { ["Tmdb"] = 949, ["Imdb"] = "tt0113277", ["Tvdb"] = null, ["Bad"] = new JsonArray(), [" "] = "x" }
        });
        Assert.Equal("Video", odd.Identity.Kind);
        Assert.Equal(new Dictionary<string, string> { ["Imdb"] = "tt0113277", ["Tmdb"] = "949" }, odd.Identity.ProviderIds.OrderBy(p => p.Key).ToDictionary());

        var notAnObject = MigrateOne(new JsonObject { ["ItemProviderIds"] = "Tmdb=949" });
        Assert.Empty(notAnObject.Identity.ProviderIds);
    }

    [Fact]
    public void FieldsSchema2Defines_AreReplacedOnA10Record_AndUnknownFieldsKept()
    {
        // Hand-added fields with schema 2's names can't clash with the upgrade; other unknown fields stay (Extra).
        var migrated = MigrateOne(new JsonObject
        {
            ["Note"] = "Bank job",
            ["Name"] = 42,
            ["Identity"] = "odd",
            ["Origin"] = "Exploded",
            ["DeletedUtc"] = "not a date",
            ["RelinkStrength"] = "Sideways",
            ["FutureField"] = new JsonObject { ["Kept"] = true },
            ["Clip"] = new JsonObject { ["Status"] = "Completed", ["EstimatedBytes"] = "lots", ["MissingSinceUtc"] = 1, ["FutureClipField"] = 1 }
        });

        Assert.Equal("Bank job", migrated.Name);
        Assert.Equal(BookmarkOrigin.Native, migrated.Origin);
        Assert.Null(migrated.DeletedUtc);
        Assert.Null(migrated.RelinkStrength);
        Assert.True(migrated.Extra!.ContainsKey("FutureField"));
        Assert.Null(migrated.Clip!.EstimatedBytes);
        Assert.True(migrated.Clip.Extra!.ContainsKey("FutureClipField"));
    }

    [Fact]
    public void DocumentFields_AreSetAfresh_AndUnknownOnesKept()
    {
        var root = new JsonObject
        {
            ["Version"] = 1,
            ["ClipRoots"] = 42,
            ["KnownUsers"] = "odd",
            ["MoveJournalId"] = "not a guid",
            ["LastMoveSeq"] = "x",
            ["FutureDocumentField"] = new JsonArray(1, 2),
            ["Bookmarks"] = new JsonArray()
        };
        WriteFile(Encoding.UTF8.GetBytes(root.ToJsonString()));

        using var store = CreateStore(new BookmarkStoreMigration1To2(_directory.Combine("clips"), () => null, _ => null));

        Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        Assert.Equal([Path.GetFullPath(_directory.Combine("clips"))], store.ClipRoots);
        Assert.Null(store.Documents.Current.MoveJournalId);
        Assert.Equal(0, store.Documents.Current.LastMoveSeq);
        Assert.True(store.Documents.Current.Extra!.ContainsKey("FutureDocumentField"));
    }

    [Fact]
    public void AFileWithoutAVersion_IsSchema1_AndIsUpgraded()
    {
        // A missing Version is 1 (the conformance suite skips its own check once a store is past version 1).
        var root = Records(new JsonObject { ["Note"] = "Bank job" });
        root.Remove("Version");
        var bytes = WriteFile(Encoding.UTF8.GetBytes(root.ToJsonString()));

        using var store = CreateStore(new BookmarkStoreMigration1To2(_directory.Combine("clips"), () => null, _ => null));

        Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        Assert.Equal(1, store.Documents.MigratedFrom);
        Assert.Equal("Bank job", Assert.Single(store.GetAll()).Name);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Folder, "bookmarks.json.v1-*"))));
    }

    [Fact]
    public void AFileWithoutBookmarks_BecomesAnEmptySchema2Document()
    {
        WriteFile(Encoding.UTF8.GetBytes("""{ "Version": 1 }"""));

        using var store = CreateStore(new BookmarkStoreMigration1To2(_directory.Combine("clips"), () => null, _ => null));

        Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        Assert.Empty(store.GetAll());
        Assert.Single(store.ClipRoots);
    }

    [Fact]
    public void BookmarksThatArentAList_FailTheUpgrade_AndLeaveTheFileAlone()
    {
        // Never the corrupt-file path; the store is read-only and the file byte-identical.
        var bytes = WriteFile(Encoding.UTF8.GetBytes("""{ "Version": 1, "Bookmarks": { "not": "a list" } }"""));

        using var store = CreateStore(new BookmarkStoreMigration1To2(_directory.Combine("clips"), () => null, _ => null));

        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Documents.Status);
        Assert.True(store.IsReadOnly);
        Assert.Equal(bytes, File.ReadAllBytes(StorePath));
        Assert.Empty(Directory.GetFiles(Folder, "*.corrupt-*"));
    }

    [Theory]
    [InlineData(@"D:\Clips\admin\a.mkv", @"D:\Clips\admin")]
    [InlineData(@"C:\data\clips\admin\a.mkv", null)]
    [InlineData(@"C:\data\clips\a.mkv", null)]
    [InlineData(@"C:\data\clips2\admin\a.mkv", @"C:\data\clips2\admin")]
    [InlineData(@"C:\library\clips\a.mkv", null)]
    [InlineData(@"C:\LIBRARY\Clips\user\a.mkv", null)]
    public void ClipRoots_AFolderIsAddedOnlyWhenNoKnownFolderHoldsIt(string clipFile, string? added)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

        var migrated = Migrate(Records(new JsonObject { ["Clip"] = new JsonObject { ["FilePath"] = clipFile } }), @"C:\data\clips", @"C:\library\clips\");

        string[] expected = added is null ? [@"C:\data\clips", @"C:\library\clips"] : [@"C:\data\clips", @"C:\library\clips", added];
        Assert.Equal(expected, migrated.ClipRoots);
    }

    [Fact]
    public void ClipRoots_KeepTheShortestFolder_OnceIgnoringCase_AndSkipUnusablePaths()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

        var migrated = Migrate(
            Records(
                Clip(@"D:\Clips\admin\a.mkv"),
                Clip(@"D:\clips\ADMIN\b.mkv"),
                Clip(@"D:\Clips\c.mkv"),
                Clip(@"E:\Other\x\y\z.mkv"),
                Clip("  "),
                Clip("bad\0path.mkv")),
            @"C:\data\clips",
            null);

        Assert.Equal([@"C:\data\clips", @"D:\Clips", @"E:\Other\x\y"], migrated.ClipRoots);
    }

    [Fact]
    public void ClipRoots_AreCappedAt50_WithAWarning()
    {
        var logger = new ListLogger();
        var records = Records(Enumerable.Range(0, 60).Select(i => Clip(Path.Combine(_directory.Path, "folder" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), "a.mkv"))).ToArray());

        var migrated = Migrate(records, _directory.Combine("clips"), null, logger);

        Assert.Equal(BookmarkStoreDocument.MaxClipRoots, migrated.ClipRoots.Count);
        Assert.Equal(Path.GetFullPath(_directory.Combine("clips")), migrated.ClipRoots[0]);
        Assert.Single(logger.At(LogLevel.Warning));
    }

    [Fact]
    public void AnOlderExportsData_TakesItsClipRootsFromItsOwnClipFiles_AndNeverAddsARootToTheStore()
    {
        // The kit runs this step on an export envelope's Data too (JsonDocumentStore.ReadData), so the upgraded
        // export holds a folder for every clip file it names, wherever that points. The target keeps its
        // own ClipRoots. Reading the data adds none here; the import adapter must not take the export's either.
        WriteFile(Encoding.UTF8.GetBytes(Records(Clip(_directory.Combine("clips", "admin", "a.mkv"))).ToJsonString()));
        using var store = CreateStore(new BookmarkStoreMigration1To2(_directory.Combine("clips"), () => null, _ => null));
        string[] own = [Path.GetFullPath(_directory.Combine("clips"))];
        Assert.Equal(own, store.ClipRoots);
        var bytes = File.ReadAllBytes(StorePath);
        var outside = Path.GetFullPath(_directory.Combine("not a clip folder"));

        var incoming = store.Documents.ReadData(Records(Clip(Path.Combine(outside, "a.mkv"))), 1);

        Assert.Contains(outside, incoming.ClipRoots);
        Assert.Equal(own, store.ClipRoots);
        Assert.Equal(own, store.Documents.Current.ClipRoots);
        Assert.Equal(bytes, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public void ConfiguredFolderThatCantBeRead_LeavesTheDefaultOnly_WithAWarning()
    {
        var logger = new ListLogger();

        var migrated = MigrateReading(Records(), _directory.Combine("clips"), () => throw new InvalidOperationException("No plugin instance."), logger);

        Assert.Equal([Path.GetFullPath(_directory.Combine("clips"))], migrated.ClipRoots);
        Assert.Single(logger.At(LogLevel.Warning));
    }

    [Fact]
    public void Migrate_LogsCountsOnly_NeverNamesOrNotes()
    {
        // Names and notes never reach logs at Information or above.
        var logger = new ListLogger();

        Migrate(Records(new JsonObject { ["Note"] = "secret plan" }), _directory.Combine("clips"), null, logger);

        var line = Assert.Single(logger.At(LogLevel.Information));
        Assert.Contains("1 bookmarks, 1 notes became names", line.Message, StringComparison.Ordinal);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("secret", e.Message, StringComparison.Ordinal));
    }

    public void Dispose() => _directory.Dispose();

    // Every field of every 1.0 record keeps its value; the identity comes from ItemType, ItemName and ItemProviderIds.
    private static void AssertEvery10ValueKept(byte[] original, BookmarkStore store)
    {
        var v1 = JsonNode.Parse(original)!["Bookmarks"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal(v1.Count, store.GetAll().Count);
        foreach (var record in v1)
        {
            var migrated = store.Get(Guid.Parse((string)record["Id"]!))!;
            Assert.Equal(Guid.Parse((string)record["UserId"]!), migrated.UserId);
            Assert.Equal(Guid.Parse((string)record["ItemId"]!), migrated.ItemId);
            Assert.Equal((string?)record["MediaSourceId"], migrated.MediaSourceId);
            Assert.Equal((int?)record["AudioStreamIndex"], migrated.AudioStreamIndex);
            Assert.Equal((long)record["StartTicks"]!, migrated.StartTicks);
            Assert.Equal((long?)record["EndTicks"], migrated.EndTicks);
            Assert.Equal((string)record["ItemName"]!, migrated.ItemName);
            Assert.Equal((DateTime)record["CreatedAt"]!, migrated.CreatedAt);
            Assert.Equal((DateTime)record["UpdatedAt"]!, migrated.UpdatedAt);
            Assert.Equal(DateTimeKind.Utc, migrated.CreatedAt.Kind);

            Assert.Equal(migrated.ItemId, migrated.Identity.ItemId);
            Assert.Equal((string)record["ItemType"]!, migrated.Identity.Kind);
            Assert.Equal(migrated.ItemName, migrated.Identity.Name);
            Assert.Equal(
                record["ItemProviderIds"]!.AsObject().Select(p => KeyValuePair.Create(p.Key, (string)p.Value!)).OrderBy(p => p.Key),
                migrated.Identity.ProviderIds.OrderBy(p => p.Key));
            Assert.Null(migrated.Identity.Year);
            Assert.Null(migrated.Identity.RunTimeTicks);
            Assert.Null(migrated.Identity.Path);

            Assert.Equal(BookmarkOrigin.Native, migrated.Origin);
            Assert.Equal(migrated.CreatedAt, migrated.AttachedUtc);
            Assert.Null(migrated.DetachedUtc);
            Assert.Null(migrated.RelinkedUtc);
            Assert.Null(migrated.RelinkStrength);
            Assert.Null(migrated.DeletedUtc);
            Assert.Null(migrated.Extra);
            Assert.True(BookmarkRecordRules.HasLibraryItem(migrated));

            if (record["Clip"] is JsonObject clip)
            {
                var expected = JsonSerializer.Deserialize<ClipInfo>(clip.ToJsonString(), JsonStoreFormat.Options);
                Assert.Equal(expected, migrated.Clip);
                Assert.Null(migrated.Clip!.EstimatedBytes);
                Assert.Null(migrated.Clip.MissingSinceUtc);
            }
            else
            {
                Assert.Null(migrated.Clip);
            }
        }
    }

    private static JsonObject Clip(string path) => new() { ["Clip"] = new JsonObject { ["FilePath"] = path } };

    // A 1.0 file around records, each given the fields every 1.0 record has unless the record sets them.
    private static JsonObject Records(params JsonObject[] records)
    {
        var list = new JsonArray();
        foreach (var record in records)
        {
            record["Id"] ??= Guid.NewGuid().ToString();
            record["UserId"] ??= Admin.ToString();
            record["ItemId"] ??= Guid.NewGuid().ToString();
            record["StartTicks"] ??= 0;
            record["ItemName"] ??= "Heat (1995)";
            record["CreatedAt"] ??= "2026-09-29T10:00:00Z";
            record["UpdatedAt"] ??= "2026-09-29T10:00:00Z";
            list.Add(record);
        }

        return new JsonObject { ["Version"] = 1, ["Bookmarks"] = list };
    }

    private string Folder => Path.GetDirectoryName(StorePath)!;

    private Bookmark MigrateOne(JsonObject record)
    {
        var document = Migrate(Records(record), _directory.Combine("clips"), null);
        return Assert.Single(document.Bookmarks);
    }

    private static BookmarkStoreDocument Migrate(JsonObject root, string defaultRoot, string? configuredRoot, ILogger? logger = null)
        => MigrateReading(root, defaultRoot, () => configuredRoot, logger);

    private static BookmarkStoreDocument MigrateReading(JsonObject root, string defaultRoot, Func<string?> configuredRoot, ILogger? logger = null)
    {
        var migration = new BookmarkStoreMigration1To2(defaultRoot, configuredRoot, _ => null, logger);
        migration.Apply(root);
        root["Version"] = 2;
        return root.Deserialize<BookmarkStoreDocument>(JsonStoreFormat.Options)!;
    }

    private BookmarkStore CreateStore(BookmarkStoreMigration1To2 migration) => BookmarkStore.Create(StorePath, BookmarkStore.Migrations(migration), NullLogger.Instance);

    private byte[] WriteFile(byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(StorePath, bytes);
        return bytes;
    }
}
