using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Storage.Migrations;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// The read-only store: the two failed-migration fixtures on real 1.0 data, the upgrade's refusal of real 1.0 files
/// it can't convert, and the <c>Version=3</c> fixture. Each leaves <c>bookmarks.json</c> and its <c>.v1-*</c> copy byte for
/// byte as they were, quarantines nothing, never reads the <c>.bak</c>, answers every write with 503, and keeps every
/// background writer idle (<see cref="BackgroundWriteGate"/>) and the snapshots unwritten. The kit's conformance suite runs
/// its own generic versions of the two failures on this store too.
/// </summary>
public sealed class BookmarkStoreReadOnlyTests : IDisposable
{
    private static readonly Guid Admin = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid Viewer = new("ff71b896-f205-45ff-936a-c2393f6091f6");

    private readonly TempDirectory _directory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => _directory.Combine("bookmarks");

    private string StorePath => Path.Combine(Folder, "bookmarks.json");

    private string BackupPath => StorePath + ".bak";

    private string Snapshots => Path.Combine(Folder, "snapshots");

    [Fact]
    public async Task FailedMigration_WhenTheStepThrows_AfterChangingTheRecords_ChangesNothing()
    {
        // Fixture (a): the real 1→2 step runs on the file Bookmarks 1.0.0 wrote, changes every record in memory,
        // and then throws. None of its changes may reach the disk, and the store must not fall back to the .bak.
        var original = WriteStoreFile(StoreFixtures.Read(StoreFixtures.Bookmarks100));
        var backup = WriteBackup();
        var logger = new ListLogger();
        var step = new BrokenStep(Real(), _ => throw new InvalidOperationException("The step failed half-way."));

        using (var store = CreateStore(step, logger))
        {
            AssertFailed(store, "the migration from version 1 to 2 failed");
            await AssertInertAsync(store, BookmarkStoreReadOnlyReason.MigrationFailed);
            Assert.Contains(logger.At(LogLevel.Error), e => e.Exception is InvalidOperationException && e.Message.Contains("the migration from version 1 to 2 failed", StringComparison.Ordinal));
        }

        AssertUntouched(original, backup);

        // Every later start fails the same way and reuses the one copy; with a step that works (the fixed or previous
        // version), the same file upgrades, so nothing was lost.
        using (var again = CreateStore(step))
        {
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, again.Documents.Status);
        }

        AssertUntouched(original, backup);
        using var fixedStore = CreateStore(Real());
        Assert.Equal(StoreStatus.Migrated, fixedStore.Documents.Status);
        Assert.Equal(10, fixedStore.GetAll().Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Folder, "bookmarks.json.v1-*")), Ct));
    }

    [Theory]
    [InlineData("an Origin this version doesn't know")]
    [InlineData("an Origin that isn't text")]
    [InlineData("an Identity that is text")]
    [InlineData("an Identity that is a list")]
    [InlineData("provider ids that are a list")]
    [InlineData("an identity item id that isn't a GUID")]
    [InlineData("a creation time that isn't a time")]
    public async Task FailedMigration_WhenTheUpgradedDataDoesNotDeserialize_ChangesNothing(string broken)
    {
        // Fixture (b): the step "succeeds" but leaves JSON that schema 2 can't read (a bad Origin, the wrong
        // Identity shape, …), as a bug in the step would.
        var original = WriteStoreFile(StoreFixtures.Read(StoreFixtures.Bookmarks100));
        var backup = WriteBackup();
        var step = new BrokenStep(Real(), root =>
        {
            var record = root["Bookmarks"]!.AsArray()[3]!.AsObject();
            switch (broken)
            {
                case "an Origin this version doesn't know": record["Origin"] = "Exploded"; break;
                case "an Origin that isn't text": record["Origin"] = new JsonObject(); break;
                case "an Identity that is text": record["Identity"] = "Heat (1995)"; break;
                case "an Identity that is a list": record["Identity"] = new JsonArray("Heat"); break;
                case "provider ids that are a list": record["Identity"]!["ProviderIds"] = new JsonArray("Tmdb", "949"); break;
                case "an identity item id that isn't a GUID": record["Identity"]!["ItemId"] = "not a guid"; break;
                case "a creation time that isn't a time": record["CreatedAt"] = "yesterday"; break;
                default: throw new ArgumentOutOfRangeException(nameof(broken));
            }
        });

        using (var store = CreateStore(step))
        {
            AssertFailed(store, "reading the upgraded data failed");
            await AssertInertAsync(store, BookmarkStoreReadOnlyReason.MigrationFailed);
        }

        AssertUntouched(original, backup);
    }

    public static TheoryData<string> RefusedFiles => new()
    {
        "a property written twice",
        "a clip status 1.0 doesn't know",
        "Bookmarks that aren't a list",
        "a record that isn't an object",
    };

    [Theory]
    [MemberData(nameof(RefusedFiles))]
    public async Task Real10Files_TheUpgradeCantConvert_AreLeftAlone_AndTheStoreIsReadOnly(string what)
    {
        // With the real step: a 1.0 file that can't be converted faithfully is never half-converted.
        var bytes = what switch
        {
            "a property written twice" => StoreFixtures.Read(StoreFixtures.Bookmarks100DuplicateKey),
            "a clip status 1.0 doesn't know" => Edit(StoreFixtures.Read(StoreFixtures.Bookmarks100), root => root["Bookmarks"]![1]!["Clip"]!["Status"] = "Paused"),
            "Bookmarks that aren't a list" => Encoding.UTF8.GetBytes("""{ "Version": 1, "Bookmarks": { "not": "a list" } }"""),
            "a record that isn't an object" => Encoding.UTF8.GetBytes("""{ "Version": 1, "Bookmarks": [ "Bank job" ] }"""),
            _ => throw new ArgumentOutOfRangeException(nameof(what))
        };
        var original = WriteStoreFile(bytes);
        var backup = WriteBackup();

        using (var store = CreateStore(Real()))
        {
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Documents.Status);
            Assert.Empty(store.GetAll());
            await AssertInertAsync(store, BookmarkStoreReadOnlyReason.MigrationFailed);
        }

        AssertUntouched(original, backup);
    }

    [Fact]
    public void DuplicateKeyFixture_Is10sData_WhichSystemTextJsonReads_LastValueWins()
    {
        // The fixture is a file 1.0 itself still reads: its store deserializes schema 1 directly with System.Text.Json,
        // which keeps a repeated property's last value. Only the upgrade, which parses the file as a tree, refuses it.
        var bytes = StoreFixtures.Read(StoreFixtures.Bookmarks100DuplicateKey);

        var file = JsonSerializer.Deserialize<Version1File>(bytes, JsonStoreFormat.Options)!;

        Assert.Equal(10, file.Bookmarks.Count);
        Assert.Equal("viewer's note", file.Bookmarks.Single(b => b.UserId == Viewer).Note);
        Assert.Throws<ArgumentException>(() => JsonNode.Parse(bytes)!.AsObject()["Bookmarks"]!.AsArray().Select(n => n!.AsObject().Count).ToList());
    }

    [Fact]
    public async Task Version3Fixture_IsShownReadOnly_AndNeverWritten()
    {
        // A Version=3 file is read-only: a newer Bookmarks' file is listed as far as this version can read it, its
        // unknown fields and clip jobs as they are, and nothing ever writes it.
        var original = WriteStoreFile(StoreFixtures.Read(StoreFixtures.Schema3));
        var backup = WriteBackup();
        var logger = new ListLogger();

        using (var store = CreateStore(Real(), logger))
        {
            Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Documents.Status);
            Assert.Equal(3, store.Documents.LoadedVersion);
            Assert.Equal(10, store.GetAll().Count);
            Assert.Equal(8, store.GetLive(Admin, null).Count);
            Assert.Single(store.GetLive(Viewer, null));
            Assert.Equal(BookmarkClipStatus.Queued, store.Get(new Guid("5f7ef9bc-14bc-4e9a-a9c6-07c0b46a9210"))!.Clip!.Status);
            Assert.Equal(BookmarkClipStatus.Running, store.Get(new Guid("bff5bbea-1ebe-4669-9bc3-b5ae14fcbc6a"))!.Clip!.Status);
            Assert.True(store.Get(new Guid("5f7ef9bc-14bc-4e9a-a9c6-07c0b46a9210"))!.Extra!.ContainsKey("FutureRecordField"));
            Assert.True(store.Documents.Current.Extra!.ContainsKey("FutureDocumentField"));
            Assert.Equal("admin", store.KnownUsers.NameOf(Admin));

            await AssertInertAsync(store, BookmarkStoreReadOnlyReason.NewerVersion);

            // Export still works: the file's own data at its version, for that version to import.
            var export = store.Documents.ExportData();
            Assert.Equal(3, export.SchemaVersion);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), export.Data));

            var diagnostics = KitDiagnostics.DescribeStore(store.Documents);
            Assert.Equal("ReadOnlyNewerVersion", diagnostics["Status"]);
            Assert.Equal(true, diagnostics["ReadOnly"]);
            Assert.Contains("schema 3; this version reads up to 2", (string)diagnostics["Message"]!, StringComparison.Ordinal);
            Assert.Equal(0, diagnostics["KeptFileCount"]);
            Assert.Single(logger.At(LogLevel.Error));
        }

        using (var restarted = CreateStore(Real()))
        {
            Assert.Equal(StoreStatus.ReadOnlyNewerVersion, restarted.Documents.Status);
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(StorePath, Ct));
        Assert.Equal(backup, await File.ReadAllBytesAsync(BackupPath, Ct));
        Assert.Equal(["bookmarks.json", "bookmarks.json.bak"], Directory.GetFiles(Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MissingFile_BesideOnlyANewerBookmarksSnapshot_IsReadOnly_DataMissing_AndInert()
    {
        // bookmarks.json gone with no .bak, and the only snapshot is a newer Bookmarks' (a downgrade, then the file lost):
        // nothing can be restored, so nothing is written, and the snapshot is left exactly as it is.
        Directory.CreateDirectory(Snapshots);
        var snapshot = Path.Combine(Snapshots, "bookmarks-20261001-031000.json");
        await File.WriteAllBytesAsync(snapshot, StoreFixtures.Read(StoreFixtures.Schema3), Ct);
        var logger = new ListLogger();

        using (var store = CreateStore(Real(), logger))
        {
            Assert.Equal(StoreStatus.ReadOnlyDataMissing, store.Documents.Status);
            Assert.Empty(store.GetAll());
            await AssertInertAsync(store, BookmarkStoreReadOnlyReason.DataMissing);

            var export = Assert.Throws<PluginException>(store.Documents.ExportData);
            Assert.Equal(StoreMessages.ExportDataMissing, export.Message);
            var diagnostics = KitDiagnostics.DescribeStore(store.Documents);
            Assert.Equal("ReadOnlyDataMissing", diagnostics["Status"]);
            Assert.Equal(true, diagnostics["ReadOnly"]);
            Assert.Contains("bookmarks-20261001-031000.json (schema 3, from a newer plugin version)", (string)diagnostics["Message"]!, StringComparison.Ordinal);
            Assert.Single(logger.At(LogLevel.Error));
        }

        Assert.Equal(StoreFixtures.Read(StoreFixtures.Schema3), await File.ReadAllBytesAsync(snapshot, Ct));
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task UnreadableFile_WithNothingToTakeItsPlace_IsReadOnly_DataUnreadable_AndInert_UntilAnAdminRecovers()
    {
        // Bookmarks 1.2: an unreadable bookmarks.json beside no snapshot holding bookmarks (here: a newer Bookmarks' only) is
        // never started empty on its own: read-only, the file and the snapshot left as they are, every writer idle. An admin
        // starting it empty opens the gate at once, and a writer waiting for it goes on.
        var broken = "{ \"Version\": 2, \"Bookmarks\": 7 }"u8.ToArray();
        WriteStoreFile(broken);
        Directory.CreateDirectory(Snapshots);
        var snapshot = Path.Combine(Snapshots, "bookmarks-20261001-031000.json");
        await File.WriteAllBytesAsync(snapshot, StoreFixtures.Read(StoreFixtures.Schema3), Ct);
        var logger = new ListLogger();

        using var store = CreateStore(Real(), logger);
        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Documents.Status);
        await AssertInertAsync(store, BookmarkStoreReadOnlyReason.DataUnreadable);
        var diagnostics = KitDiagnostics.DescribeStore(store.Documents);
        Assert.Equal("ReadOnlyDataUnreadable", diagnostics["Status"]);
        Assert.Contains("bookmarks-20261001-031000.json (schema 3, from a newer plugin version)", (string)diagnostics["Message"]!, StringComparison.Ordinal);
        Assert.Equal(StoreMessages.ExportDataUnreadable, Assert.Throws<PluginException>(store.Documents.ExportData).Message);
        Assert.Equal(broken, await File.ReadAllBytesAsync(StorePath, Ct));

        var gate = new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance);
        var open = gate.WaitUntilOpenAsync(Ct);
        Assert.False(open.IsCompleted);
        await store.Documents.RecoverAsync(Snapshots, null, startEmpty: true, Ct);
        await open.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.All(Enum.GetValues<BackgroundWriter>(), writer => Assert.True(gate.MayWrite(writer)));
        Assert.Equal(broken, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Folder, "bookmarks.json.corrupt-*")), Ct));
        Assert.Equal(StoreFixtures.Read(StoreFixtures.Schema3), await File.ReadAllBytesAsync(snapshot, Ct));
    }

    [Fact]
    public async Task MissingFile_BesideSnapshotsOfItsBookmarks_IsRestored_WithTheBanner_AndTheGateOpen()
    {
        // A backup of snapshots/ alone put back: the newest snapshot that holds bookmarks comes back, saved at
        // once, and the settings page's banner (Diagnostics' Message) says from which one.
        Directory.CreateDirectory(Snapshots);
        await File.WriteAllBytesAsync(Path.Combine(Snapshots, "bookmarks-20261001-031000.json"), StoreFixtures.Read(StoreFixtures.Bookmarks100), Ct);
        await File.WriteAllBytesAsync(Path.Combine(Snapshots, "bookmarks-20261002-031000.json"), StoreFixtures.Read(StoreFixtures.Schema2), Ct);
        await File.WriteAllBytesAsync(Path.Combine(Snapshots, "bookmarks-20261003-031000.json"), JsonSerializer.SerializeToUtf8Bytes(BookmarkStoreDocument.CreateEmpty(), JsonStoreFormat.Options), Ct);
        var logger = new ListLogger();

        using var store = CreateStore(Real(), logger);

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Documents.Status);
        Assert.False(store.IsReadOnly);
        Assert.Null(store.ReadOnlyReason);
        Assert.Equal(StoreFixtures.Schema2Records, store.GetAll().Count);
        var diagnostics = KitDiagnostics.DescribeStore(store.Documents);
        Assert.Equal("RestoredFromSnapshot", diagnostics["Status"]);
        Assert.StartsWith("The data file was missing and there was no backup, so the data was restored from the snapshot bookmarks-20261002-031000.json taken 2026-10-02 03:10 UTC", (string)diagnostics["Message"]!, StringComparison.Ordinal);
        Assert.EndsWith("Passed over first: bookmarks-20261003-031000.json (holds no data).", (string)diagnostics["Message"]!, StringComparison.Ordinal);
        Assert.NotNull(diagnostics["KeepSnapshotsBeforeUtc"]);
        Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.StartsWith("Restored ", StringComparison.Ordinal) && e.Message.Contains("bookmarks-20261002-031000.json taken 2026-10-02 03:10:00 UTC", StringComparison.Ordinal));

        var gate = new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance);
        Assert.All(Enum.GetValues<BackgroundWriter>(), writer => Assert.True(gate.MayWrite(writer)));
        using var reader = CreateStore(Real());
        Assert.Equal(StoreStatus.Ok, reader.Documents.Status);
        Assert.Equal(StoreFixtures.Schema2Records, reader.GetAll().Count);
    }

    [Fact]
    public void EveryReadOnlyStatusOfTheKitStore_HasAReason_SoTheGateClosesForIt()
    {
        foreach (var status in Enum.GetValues<StoreStatus>())
        {
            Assert.True(status.ToString().StartsWith("ReadOnly", StringComparison.Ordinal) == BookmarkStore.ReadOnlyReasonOf(status).HasValue, status.ToString());
        }
    }

    [Fact]
    public async Task WritableStore_OpensTheGate_ForEveryWriter_WithoutALogLine()
    {
        WriteStoreFile(StoreFixtures.Read(StoreFixtures.Schema2));
        using var store = CreateStore(Real());
        var logger = new ListLogger<BackgroundWriteGate>();
        var gate = new BackgroundWriteGate(store, logger);

        Assert.All(Enum.GetValues<BackgroundWriter>(), writer => Assert.True(gate.MayWrite(writer)));
        Assert.Null(store.ReadOnlyReason);
        Assert.Empty(logger.Entries);
        Assert.NotNull(await store.Documents.SnapshotAsync(Snapshots, 7, Ct));
    }

    [Fact]
    public void UnreadableStoreFile_IsNotReadOnly_TheGateThrows503_AndAWriterTriesAgainLater()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file locking.");

        // A locked file (antivirus, a backup tool) isn't read-only data: the store answers 503 until it can read it.
        var original = WriteStoreFile(StoreFixtures.Read(StoreFixtures.Bookmarks100));
        using var store = CreateStore(Real());
        var gate = new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance);

        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<PluginException>(() => gate.MayWrite(BackgroundWriter.ClipWorker));
            Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
        }

        Assert.True(gate.MayWrite(BackgroundWriter.ClipWorker));
        Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Folder, "bookmarks.json.v1-*"))));
    }

    public void Dispose() => _directory.Dispose();

    // Every write the store offers: each must be refused with the status's 503 message.
    private static IEnumerable<(string Name, Func<Task> Write)> Writes(BookmarkStore store, string folder)
    {
        var record = TestData.CreateBookmark(Admin);
        var existing = store.GetAll().Select(b => b.Id).FirstOrDefault(Guid.NewGuid());
        yield return ("AddAsync", () => store.AddAsync(record, Ct));
        yield return ("UpdateAsync", () => store.UpdateAsync(existing, b => b with { Name = "changed" }, Ct));
        yield return ("RemoveAsync", () => store.RemoveAsync(existing, Ct));
        yield return ("UpdateDocumentAsync", () => store.UpdateDocumentAsync(d => d with { LastMoveSeq = d.LastMoveSeq + 1 }, Ct));
        yield return ("AddClipRootAsync", () => store.AddClipRootAsync(Path.Combine(folder, "new clip folder"), Ct));
        yield return ("SetPreferencesAsync", () => store.SetPreferencesAsync(Admin, new BookmarkUserPreferences { AskForName = true }, Ct));
        yield return ("Jellyfin Enhanced import", () => store.UpdateDocumentAsync(d => new JeImportPlan([], [], 0, [record], [], new Dictionary<string, BookmarkImportLedgerEntry> { ["je:x:Bm_1"] = new() { BookmarkId = record.Id, Outcome = "Imported" } }, new Dictionary<string, BookmarkImportRun>(), new Dictionary<Guid, string>()).ApplyTo(d), Ct));
        yield return ("RefreshKnownUsersAsync", () => store.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [Admin] = "renamed admin", [Viewer] = "renamed viewer" }, Ct));
        yield return ("AddPendingFileDeleteAsync", () => store.AddPendingFileDeleteAsync(Path.Combine(folder, "old.mkv"), Ct));
        yield return ("UpdatePendingFileDeletesAsync", () => store.UpdatePendingFileDeletesAsync(_ => null, Ct));
        yield return ("Documents.UpdateAsync", () => store.Documents.UpdateAsync(d => d with { LastMoveSeq = 7 }, Ct));
    }

    private static byte[] Edit(byte[] bytes, Action<JsonObject> change)
    {
        var root = JsonNode.Parse(bytes)!.AsObject();
        change(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static void AssertFailed(BookmarkStore store, string failedStep)
    {
        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Documents.Status);
        Assert.Equal(1, store.Documents.LoadedVersion);
        Assert.Null(store.Documents.MigratedFrom);
        Assert.Contains(failedStep, store.Documents.StatusMessage, StringComparison.Ordinal);

        // The empty stand-in: not the .bak, not a half-upgraded document.
        Assert.Empty(store.GetAll());
        Assert.Empty(store.ClipRoots);
        Assert.Empty(store.KnownUsers);

        // Export has nothing faithful to give; the unchanged file and its copy are the backup.
        var export = Assert.Throws<PluginException>(store.Documents.ExportData);
        Assert.Equal(PluginErrorKind.Unavailable, export.Kind);
        Assert.Equal(StoreMessages.ExportMigrationFailed, export.Message);

        // Diagnostics: the status, the store's own sentence and the copy to go back with.
        var diagnostics = KitDiagnostics.DescribeStore(store.Documents);
        Assert.Equal("ReadOnlyMigrationFailed", diagnostics["Status"]);
        Assert.Equal(true, diagnostics["ReadOnly"]);
        Assert.Equal(1, diagnostics["Version"]);
        Assert.Contains("nothing was changed", (string)diagnostics["Message"]!, StringComparison.Ordinal);
        var copy = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(diagnostics["KeptFiles"]));
        Assert.Equal("PreUpgradeCopy", copy["Kind"]);
        Assert.Equal(1, copy["SchemaVersion"]);
        Assert.StartsWith("bookmarks.json.v1-", (string)copy["Name"]!, StringComparison.Ordinal);
    }

    private async Task AssertInertAsync(BookmarkStore store, BookmarkStoreReadOnlyReason reason)
    {
        Assert.True(store.IsReadOnly);
        Assert.Equal(reason, store.ReadOnlyReason);

        // Every write is a 503 with the status's message.
        var message = reason switch
        {
            BookmarkStoreReadOnlyReason.NewerVersion => StoreMessages.NewerVersion,
            BookmarkStoreReadOnlyReason.DataMissing => StoreMessages.DataMissing,
            BookmarkStoreReadOnlyReason.DataUnreadable => StoreMessages.DataUnreadable,
            _ => StoreMessages.MigrationFailed
        };
        foreach (var (name, write) in Writes(store, _directory.Path))
        {
            var ex = await Assert.ThrowsAsync<PluginException>(write);
            Assert.True(ex.Kind == PluginErrorKind.Unavailable && ex.Message == message, $"{name}: {ex.Kind} {ex.Message}");
        }

        // The clip worker, the .partial janitor, the identity service and the maintenance task stay idle: each asks the
        // gate, which says no for the rest of the process and logs it once per writer, as a Warning.
        var logger = new ListLogger<BackgroundWriteGate>();
        var gate = new BackgroundWriteGate(store, logger);
        foreach (var writer in Enum.GetValues<BackgroundWriter>())
        {
            Assert.False(gate.MayWrite(writer));
            Assert.False(gate.MayWrite(writer));
        }

        Assert.Equal(Enum.GetValues<BackgroundWriter>().Length, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.All(Enum.GetValues<BackgroundWriter>(), w => Assert.Contains(logger.Entries, e => e.Message.Contains($"read-only ({reason}), so {w} is idle", StringComparison.Ordinal)));

        // The snapshot step writes nothing, so empty or partial snapshots never rotate the good ones out.
        var before = SnapshotFiles();
        Assert.Null(await store.Documents.SnapshotAsync(Snapshots, 7, Ct));
        Assert.Null(await store.Documents.PreOpSnapshotAsync(Snapshots, SnapshotOperations.Other, Ct));
        Assert.Equal(before, SnapshotFiles());
        Assert.Null(store.Documents.LastSnapshotUtc);
    }

    private void AssertUntouched(byte[] original, byte[] backup)
    {
        Assert.Equal(original, File.ReadAllBytes(StorePath));
        Assert.Equal(backup, File.ReadAllBytes(BackupPath));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Folder, "bookmarks.json.v1-*"))));
        Assert.Empty(Directory.GetFiles(Folder, "*.corrupt-*"));
        Assert.False(File.Exists(StorePath + ".tmp"));
        Assert.False(File.Exists(StorePath + ".keep-snapshots"));
        Assert.Equal(3, Directory.GetFiles(Folder).Length);
    }

    // The snapshot folder's files, or null when there is no folder.
    private List<string>? SnapshotFiles()
        => Directory.Exists(Snapshots) ? Directory.GetFiles(Snapshots, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList() : null;

    private byte[] WriteStoreFile(byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(StorePath, bytes);
        return bytes;
    }

    // A readable schema 2 backup with other records: the failure must neither use it nor change it.
    private byte[] WriteBackup()
    {
        var backup = StoreFixtures.Read(StoreFixtures.Schema2);
        File.WriteAllBytes(BackupPath, backup);
        return backup;
    }

    private BookmarkStoreMigration1To2 Real() => new(_directory.Combine("clips"), () => null, id => id == Admin ? "admin" : null);

    private BookmarkStore CreateStore(IJsonMigration migration, ILogger? logger = null)
        => new(BookmarkStore.CreateDocumentStore(StorePath, [migration], logger ?? NullLogger.Instance));

    /// <summary>The real step, then something that goes wrong: an exception, or JSON schema 2 can't read.</summary>
    private sealed class BrokenStep(BookmarkStoreMigration1To2 real, Action<JsonObject> afterwards) : IJsonMigration
    {
        public int FromVersion => real.FromVersion;

        public void Apply(JsonObject root)
        {
            real.Apply(root);
            afterwards(root);
        }
    }

    /// <summary>Schema 1 as Bookmarks 1.0 read it, as far as this test needs.</summary>
    private sealed record Version1File(int Version, IReadOnlyList<Version1Record> Bookmarks);

    private sealed record Version1Record(Guid Id, Guid UserId, string? Note);
}
