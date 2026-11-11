using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Storage;

/// <summary>
/// JsonDocumentStore beyond the conformance suite: the real 1 → 2 → 3 migrations of the test document, a
/// failure in the middle of the chain, malformed versions, the change contract, unreadable files and argument checks.
/// </summary>
public sealed class JsonDocumentStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    private string StoreFile => _directory.Combine("fx", "notes.json");

    [Fact]
    public void Version1_WithoutAVersion_IsMigratedThroughEveryStep()
    {
        WriteFile(FxNotesStoreConformance.Version1Fixture);

        using var store = CreateStore();

        Assert.Equal(StoreStatus.Migrated, store.Status);
        Assert.Equal(1, store.MigratedFrom);
        Assert.Equal(3, store.Current.Version);
        Assert.Equal(new[] { "first", "second" }, store.Current.Notes.Select(n => n.Text));
        Assert.All(store.Current.Notes, n => Assert.Empty(n.Tags));
        Assert.Equal(FxNoteKind.Pinned, store.Current.Notes[1].Kind);
        Assert.Contains("schema 1 to 3", store.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(3, (int)JsonNode.Parse(File.ReadAllBytes(StoreFile))!["Version"]!);
        Assert.Equal(FxNotesStoreConformance.Version1Fixture, File.ReadAllText(Assert.Single(Directory.GetFiles(Folder, "notes.json.v1-*"))));
    }

    [Fact]
    public async Task Version2_IsMigrated_AndKeepsUnknownFieldsOfTheDocumentAndItsRecords()
    {
        WriteFile(FxNotesStoreConformance.Version2Fixture);

        using (var store = CreateStore())
        {
            Assert.Equal(StoreStatus.Migrated, store.Status);
            Assert.Equal(2, store.MigratedFrom);
            Assert.Equal(new[] { "favourite" }, store.Current.Notes[0].Tags);
            Assert.Empty(store.Current.Notes[1].Tags);
            Assert.Equal("red", store.Current.Notes[0].Extra!["Colour"].GetString());
            Assert.True(store.Current.Extra!.ContainsKey("UserPreferences"));
            await store.UpdateAsync(d => d.Add(FxNote.Create(9)), TestContext.Current.CancellationToken);
        }

        var root = JsonNode.Parse(await File.ReadAllBytesAsync(StoreFile, TestContext.Current.CancellationToken))!;
        Assert.Equal("red", (string)root["Notes"]![0]!["Colour"]!);
        Assert.True((bool)root["UserPreferences"]!["9fa5c677-8cf7-4a94-bff0-c82a60581ccf"]!["AskForName"]!);
        Assert.Null(root["Notes"]![0]!["Tag"]);
    }

    [Fact]
    public async Task FailureInTheMiddleOfTheChain_NamesTheStep_AndChangesNothing()
    {
        WriteFile(FxNotesStoreConformance.Version1Fixture);
        var logger = new ListLogger();

        using var store = new JsonDocumentStore<FxNotesDocument>(
            StoreFile,
            3,
            FxNotesDocument.CreateEmpty,
            [new FxNotesMigration1To2(), new ThrowingMigration(2)],
            logger);

        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
        Assert.Empty(store.Current.Notes);
        Assert.Equal(1, store.LoadedVersion);
        var error = Assert.Single(logger.At(LogLevel.Error));
        Assert.Contains("from schema 1 to 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("the migration from version 2 to 3 failed", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("the migration from version 2 to 3 failed", store.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(FxNotesStoreConformance.Version1Fixture, await File.ReadAllTextAsync(StoreFile, TestContext.Current.CancellationToken));
        var files = Directory.GetFiles(Folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal("notes.json", files[0]);
        Assert.StartsWith("notes.json.v1-", files[1], StringComparison.Ordinal);
        var ex = await Assert.ThrowsAsync<PluginException>(() => store.UpdateAsync(d => d.Add(FxNote.Create(1)), TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public void FailedMigration_AtEveryStart_ReusesTheIdenticalCopy_AndCopiesAChangedFile()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        WriteFile(FxNotesStoreConformance.Version1Fixture);

        for (var start = 0; start < 3; start++)
        {
            var logger = new ListLogger();
            using var store = CreateFailingStore(logger, clock);
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
            Assert.Equal(start > 0, logger.At(LogLevel.Information).Any(e => e.Message.StartsWith("Reusing", StringComparison.Ordinal)));
            clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal(new[] { "notes.json", "notes.json.v1-20260929031000" }, FileNames());

        // Someone edits the file (still version 1): the new content gets a copy of its own, and the first copy stays.
        var edited = FxNotesStoreConformance.Version1Fixture.Replace("first", "first, edited", StringComparison.Ordinal);
        WriteFile(edited);
        using (var store = CreateFailingStore(NullLogger.Instance, clock))
        {
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
        }

        Assert.Equal(new[] { "notes.json", "notes.json.v1-20260929031000", "notes.json.v1-20261002031000" }, FileNames());
        Assert.Equal(FxNotesStoreConformance.Version1Fixture, File.ReadAllText(Path.Combine(Folder, "notes.json.v1-20260929031000")));
        Assert.Equal(edited, File.ReadAllText(Path.Combine(Folder, "notes.json.v1-20261002031000")));
    }

    [Fact]
    public void SuccessfulMigration_AfterFailedOnes_NamesTheReusedCopy()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        WriteFile(FxNotesStoreConformance.Version1Fixture);
        using (var failing = CreateFailingStore(NullLogger.Instance, clock))
        {
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, failing.Status);
        }

        clock.Advance(TimeSpan.FromDays(1));
        using var store = CreateStore(time: clock);

        Assert.Equal(StoreStatus.Migrated, store.Status);
        Assert.Contains("kept as notes.json.v1-20260929031000.", store.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(new[] { "notes.json", "notes.json.bak", "notes.json.v1-20260929031000" }, FileNames());
    }

    [Fact]
    public void UpgradedDocument_ThatReadsAsAnotherVersion_FailsTheMigration()
    {
        // A document type that reads its version from another property (a plugin bug) can't pass as upgraded.
        WriteFile("""{ "Version": 1, "Other": 1 }""");

        using var store = new JsonDocumentStore<FixedVersionDocument>(
            StoreFile,
            2,
            () => new FixedVersionDocument(),
            [new NoOpMigration(1)],
            NullLogger.Instance);

        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
        Assert.Contains("reading the upgraded data failed", store.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "Version": "3", "Notes": [] }""")]
    [InlineData("""{ "Version": 0, "Notes": [] }""")]
    [InlineData("""{ "Version": -3, "Notes": [] }""")]
    [InlineData("""{ "Version": 2.5, "Notes": [] }""")]
    [InlineData("""{ "Version": 99999999999, "Notes": [] }""")]
    [InlineData("""{ "Version": null, "Notes": [] }""")]
    [InlineData("""[ { "Version": 3 } ]""")]
    [InlineData("null")]
    [InlineData("""{ "Version": 3, "Notes": [] } { }""")]
    [InlineData("""{ "Version": 3, "Notes": [] """)]
    [InlineData("""{ "Version": 3, "Notes": [], }""")]
    [InlineData("""{ /* comment */ "Version": 3, "Notes": [] }""")]
    public void MalformedFiles_AreUnreadable_LeftInPlace_AndReadOnly_WithNothingToTakeTheirPlace(string content)
    {
        // Until Bookmarks 1.2 such a file was moved aside and the store started empty (RecoveredEmpty); now the store never
        // starts empty on its own where data may be lost.
        WriteFile(content);

        using (var store = CreateStore())
        {
            Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        }

        Assert.Equal(content, File.ReadAllText(StoreFile));
        Assert.Equal(["notes.json"], FileNames());

        // With a .bak, it is moved aside as it was, and the .bak used.
        File.WriteAllText(BackupFile, """{ "Version": 3, "Notes": [] }""");
        using var recovered = CreateStore();
        Assert.Equal(StoreStatus.RecoveredFromBackup, recovered.Status);
        Assert.Equal(content, File.ReadAllText(Assert.Single(Directory.GetFiles(Folder, "notes.json.corrupt-*"))));
    }

    [Fact]
    public void AnEmptyFile_HoldsNoData_IsMovedAside_AndTheStoreStartsEmpty_WithNothingElseThere()
    {
        WriteFile(string.Empty);

        using var store = CreateStore();

        Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
        Assert.False(store.IsReadOnly);
        Assert.Equal(string.Empty, File.ReadAllText(Assert.Single(Directory.GetFiles(Folder, "notes.json.corrupt-*"))));
    }

    [Fact]
    public void NestedVersionProperties_AreNotTheFilesVersion()
    {
        WriteFile("""{ "Notes": [ { "Version": 7, "Text": "x" } ], "Extra": { "Version": 9 }, "Version": 3 }""");

        using var store = CreateStore();

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Equal(3, store.LoadedVersion);
        Assert.Equal(7, store.Current.Notes[0].Extra!["Version"].GetInt32());
    }

    [Fact]
    public void DuplicatePropertiesInAnOlderFile_FailTheMigration_WithoutTouchingTheFile()
    {
        const string content = """{ "Version": 2, "Notes": [], "Notes": [] }""";
        WriteFile(content);

        using var store = CreateStore();

        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
        Assert.Equal(content, File.ReadAllText(StoreFile));
        Assert.Empty(Directory.GetFiles(Folder, "*.corrupt-*"));
    }

    [Theory]
    [InlineData("""{ "Version": 3, "Notes": [] }""", null)]
    [InlineData("""{ "Version": 4, "Notes": [] }""", StoreMessages.NewerVersion)]
    [InlineData("""{ "Version": 2, "Notes": [], "Notes": [] }""", StoreMessages.MigrationFailed)]
    [InlineData("""{ "Version": 3, "Notes": 7 }""", StoreMessages.DataUnreadable)]
    public async Task ThrowIfReadOnly_ThrowsTheWritesOwn503_OnlyWhileTheStoreIsReadOnly(string content, string? message)
    {
        // A request's write that looks its record up first asks this before the lookup, since a failed
        // upgrade's empty stand-in would answer "not found" for data the untouched file still holds.
        WriteFile(content);
        using var store = CreateStore();

        if (message is null)
        {
            store.ThrowIfReadOnly();
            Assert.False(store.IsReadOnly);
            return;
        }

        var ex = Assert.Throws<PluginException>(store.ThrowIfReadOnly);
        var write = await Assert.ThrowsAsync<PluginException>(() => store.UpdateAsync(d => d.Add(FxNote.Create(2)), TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
        Assert.Equal(message, ex.Message);
        Assert.Equal((write.Kind, write.Message), (ex.Kind, ex.Message));
        Assert.Equal(content, await File.ReadAllTextAsync(StoreFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Change_ReturningAnotherVersion_Throws_AndWritesNothing()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(d => d with { Version = 2 }, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(StoreFile));
        Assert.Empty(store.Current.Notes);
    }

    [Fact]
    public async Task Change_ThatThrows_WritesNothing_AndReleasesTheWriter()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateAsync(_ => throw new ArgumentException("no"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(StoreFile));

        await store.UpdateAsync(d => d.Add(FxNote.Create(1)), TestContext.Current.CancellationToken);
        Assert.Single(store.Current.Notes);
    }

    [Fact]
    public async Task Change_RunsOnce_OnTheLatestDocument()
    {
        using var store = CreateStore();
        var runs = 0;

        await store.UpdateAsync(d => d.Add(FxNote.Create(1)), TestContext.Current.CancellationToken);
        var result = await store.UpdateAsync(
            d =>
            {
                runs++;
                Assert.Single(d.Notes);
                return d.Add(FxNote.Create(2));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, runs);
        Assert.Same(result, store.Current);
    }

    [Fact]
    public void Construction_ReadsNothing_TheFirstUseLoads()
    {
        using var store = CreateStore();
        WriteFile(FxNotesStoreConformance.Version3Fixture);

        Assert.Single(store.Current.Notes);
    }

    [Fact]
    public async Task UnreadableFile_IsUnavailable_UntilItCanBeRead_AndNothingIsMoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs Windows file sharing to make the file unreadable.");
        }

        WriteFile(FxNotesStoreConformance.Version3Fixture);
        var logger = new ListLogger();
        using var store = CreateStore(logger);

        using (new FileStream(StoreFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<PluginException>(() => store.Current);
            Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
            Assert.Equal(StoreMessages.ReadFailed, ex.Message);
            await Assert.ThrowsAsync<PluginException>(() => store.UpdateAsync(d => d, TestContext.Current.CancellationToken));
            Assert.Throws<PluginException>(() => store.IsReadOnly);
        }

        Assert.Equal(new[] { "notes.json" }, Directory.GetFiles(Folder).Select(Path.GetFileName));
        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Single(store.Current.Notes);

        // Every use retried the load; only the first failure is an Error (any user's request can cause one), the rest Debug.
        var error = Assert.Single(logger.At(LogLevel.Error));
        Assert.IsType<IOException>(error.Exception, exactMatch: false);
        Assert.Equal(2, logger.At(LogLevel.Debug).Count(e => e.Message.StartsWith("Still could not read", StringComparison.Ordinal)));
        Assert.Single(logger.At(LogLevel.Information), e => e.Message.EndsWith("could be read again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadOnlyBackup_TheWriteStillSucceeds_AndOneWarningSaysTheBackupIsStale()
    {
        // File.Replace can't replace a read-only (or open) .bak; the plain overwrite of the commit's last step takes over.
        var logger = new ListLogger();
        using var store = CreateStore(logger);
        await store.UpdateAsync(d => d.Add(FxNote.Create(1)), TestContext.Current.CancellationToken);
        await store.UpdateAsync(d => d.Add(FxNote.Create(2)), TestContext.Current.CancellationToken);
        var staleBackup = await File.ReadAllBytesAsync(BackupFile, TestContext.Current.CancellationToken);
        File.SetAttributes(BackupFile, FileAttributes.ReadOnly);
        try
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(3)), TestContext.Current.CancellationToken);
            await store.UpdateAsync(d => d.Add(FxNote.Create(4)), TestContext.Current.CancellationToken);

            Assert.Equal(4, store.Current.Notes.Count);
            using (var reader = CreateStore())
            {
                Assert.Equal(4, reader.Current.Notes.Count);
            }

            Assert.Equal(staleBackup, await File.ReadAllBytesAsync(BackupFile, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(StoreFile + ".tmp"));
            var warning = Assert.Single(logger.At(LogLevel.Warning));
            Assert.Contains("notes.json.bak is no longer kept up to date", warning.Message, StringComparison.Ordinal);
            Assert.IsType<IOException>(warning.Exception, exactMatch: false);
            Assert.Single(logger.At(LogLevel.Debug), e => e.Message.StartsWith("Could not save", StringComparison.Ordinal));
            Assert.Empty(logger.At(LogLevel.Error));
        }
        finally
        {
            File.SetAttributes(BackupFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void QuarantinedFiles_InTheSameSecond_GetDistinctNames()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        Directory.CreateDirectory(Folder);
        File.WriteAllText(BackupFile, """{ "Version": 3, "Notes": [] }""");
        for (var i = 0; i < 3; i++)
        {
            WriteFile("broken " + i);
            using var store = CreateStore(time: clock);
            Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
        }

        Assert.Equal(
            new[] { "notes.json.bak", "notes.json.corrupt-20260929031000", "notes.json.corrupt-20260929031000-1", "notes.json.corrupt-20260929031000-2" },
            Directory.GetFiles(Folder).Select(Path.GetFileName).Order());
        Assert.Equal("broken 2", File.ReadAllText(Path.Combine(Folder, "notes.json.corrupt-20260929031000-2")));
    }

    [Fact]
    public void KeptFiles_AreThePreUpgradeCopiesAndCorruptFiles_NewestFirst_AndNothingElse()
    {
        // The .v<N>-* and .corrupt-* files stay until an admin deletes them and are listed on Diagnostics.
        Directory.CreateDirectory(Folder);
        string[] kept =
        [
            "notes.json.v1-20260929031000", "notes.json.v2-20260930031000", "notes.json.v2-20260930031000-1",
            "notes.json.corrupt-20260928031000", "notes.json.corrupt-20260928031000-2", "NOTES.JSON.v12-20261001000000"
        ];
        string[] others =
        [
            "notes.json.bak", "notes.json.tmp", "notes.json.keep-snapshots", "notes.json.v-20260929031000",
            "notes.json.v0-20260929031000", "notes.json.vx-20260929031000", "notes.json.v1-2026092903100", "notes.json.v1-20260929031000-",
            "notes.json.v1-20260929031000-x", "notes.json.v1-20260929031000x", "notes.json.corrupt-20261399000000", "notes.json.corrupt",
            "other.json.v1-20260929031000", "notes.jsonv1-20260929031000", "notes.json.v1-20260929031000.txt",
            "notes.json.v1-20260929031000.tmp", "notes.json.v2-20260930031000-1.tmp", "notes.json.corrupt-20260928031000.tmp"
        ];
        foreach (var name in kept.Concat(others))
        {
            File.WriteAllText(Path.Combine(Folder, name), name);
        }

        using var store = CreateStore();
        var files = store.ListKeptFiles();

        Assert.Equal(
            [
                ("NOTES.JSON.v12-20261001000000", StoreKeptFileKind.PreUpgradeCopy, (int?)12, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
                ("notes.json.v2-20260930031000-1", StoreKeptFileKind.PreUpgradeCopy, 2, new DateTime(2026, 9, 30, 3, 10, 0, DateTimeKind.Utc)),
                ("notes.json.v2-20260930031000", StoreKeptFileKind.PreUpgradeCopy, 2, new DateTime(2026, 9, 30, 3, 10, 0, DateTimeKind.Utc)),
                ("notes.json.v1-20260929031000", StoreKeptFileKind.PreUpgradeCopy, 1, new DateTime(2026, 9, 29, 3, 10, 0, DateTimeKind.Utc)),
                ("notes.json.corrupt-20260928031000-2", StoreKeptFileKind.Corrupt, null, new DateTime(2026, 9, 28, 3, 10, 0, DateTimeKind.Utc)),
                ("notes.json.corrupt-20260928031000", StoreKeptFileKind.Corrupt, null, new DateTime(2026, 9, 28, 3, 10, 0, DateTimeKind.Utc)),
            ],
            files.Select(f => (f.Name, f.Kind, f.SchemaVersion, f.Utc)));
        Assert.All(files, f => Assert.Equal(DateTimeKind.Utc, f.Utc.Kind));
        Assert.All(files, f => Assert.Equal(f.Name.Length, f.Size));
    }

    [Fact]
    public void KeptFiles_AfterAnUpgradeAndAQuarantine_NameTheCopyTheStoreMade()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        using (var none = CreateStore(time: clock))
        {
            Assert.Empty(none.ListKeptFiles());
        }

        WriteFile(FxNotesStoreConformance.Version1Fixture);
        using (var upgraded = CreateStore(time: clock))
        {
            Assert.Equal(StoreStatus.Migrated, upgraded.Status);
            var copy = Assert.Single(upgraded.ListKeptFiles());
            Assert.Equal(new StoreKeptFile("notes.json.v1-20260929031000", StoreKeptFileKind.PreUpgradeCopy, 1, clock.GetUtcNow().UtcDateTime, Encoding.UTF8.GetByteCount(FxNotesStoreConformance.Version1Fixture)), copy);
            Assert.Contains(copy.Name, upgraded.StatusMessage, StringComparison.Ordinal);
        }

        clock.Advance(TimeSpan.FromDays(1));
        WriteFile("broken");
        using var quarantined = CreateStore(time: clock);
        Assert.Equal(StoreStatus.RecoveredFromBackup, quarantined.Status);
        Assert.Equal(
            [("notes.json.corrupt-20260930031000", StoreKeptFileKind.Corrupt), ("notes.json.v1-20260929031000", StoreKeptFileKind.PreUpgradeCopy)],
            quarantined.ListKeptFiles().Select(f => (f.Name, f.Kind)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostics_AsTheStoresFirstUse_ListTheCopyItsUpgradeMade(bool upgradeFails)
    {
        // Diagnostics may be the first thing to load a store (the kit's users store, a later plugin's), and that
        // load is what upgrades the file and keeps the copy the admin then looks for: its message names the copy.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        WriteFile(FxNotesStoreConformance.Version1Fixture);
        using var store = upgradeFails ? CreateFailingStore(NullLogger.Instance, clock) : CreateStore(time: clock);

        var description = KitDiagnostics.DescribeStore(store);

        Assert.Equal(upgradeFails ? "ReadOnlyMigrationFailed" : "Migrated", description["Status"]);
        Assert.Equal(1, description["KeptFileCount"]);
        var copy = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(description["KeptFiles"]));
        Assert.Equal("notes.json.v1-20260929031000", copy["Name"]);
        Assert.Equal(1, copy["SchemaVersion"]);
        if (!upgradeFails)
        {
            Assert.Contains("notes.json.v1-20260929031000", (string)description["Message"]!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void CopyCutShort_IsNeitherListedNorReused_AndTheNextUpgradeReplacesIt(int length)
    {
        // A kill (or a power cut) while the pre-upgrade copy is written leaves only <copy>.tmp (empty, partial,
        // or whole but not yet renamed): the copy gets its name once its bytes are on disk, so no partial file ever has a
        // name ListKeptFiles lists and the downgrade procedure restores.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        WriteFile(FxNotesStoreConformance.Version1Fixture);
        var original = File.ReadAllBytes(StoreFile);
        var leftover = Path.Combine(Folder, "notes.json.v1-20260928031000.tmp");
        File.WriteAllBytes(leftover, length < 0 ? original : original[..length]);

        using (var before = CreateStore(time: clock))
        {
            Assert.Empty(before.ListKeptFiles());
        }

        // Listing loaded nothing; the next start upgrades, keeps a copy of its own and deletes the leftover.
        Assert.True(File.Exists(leftover));
        var logger = new ListLogger();
        using (var store = CreateStore(logger, clock))
        {
            Assert.Equal(StoreStatus.Migrated, store.Status);
            Assert.DoesNotContain(logger.At(LogLevel.Information), e => e.Message.StartsWith("Reusing", StringComparison.Ordinal));
            Assert.Equal("notes.json.v1-20260929031000", Assert.Single(store.ListKeptFiles()).Name);
        }

        Assert.Equal(["notes.json", "notes.json.bak", "notes.json.v1-20260929031000"], FileNames());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Folder, "notes.json.v1-20260929031000")));
    }

    [Fact]
    public void UpgradeFromTheBackup_KeepsTheBackupsBytes_AndLeavesNoTempFile()
    {
        // The copy holds the bytes the upgrade read (here the .bak's, the store file being missing), written and flushed
        // under <copy>.tmp and only then renamed.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        Directory.CreateDirectory(Folder);
        File.WriteAllText(BackupFile, FxNotesStoreConformance.Version1Fixture);
        using (var store = CreateStore(time: clock))
        {
            Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
            Assert.Equal("notes.json.v1-20260929031000", Assert.Single(store.ListKeptFiles()).Name);
        }

        Assert.Equal(["notes.json", "notes.json.bak", "notes.json.v1-20260929031000"], FileNames());
        Assert.Equal(FxNotesStoreConformance.Version1Fixture, File.ReadAllText(Path.Combine(Folder, "notes.json.v1-20260929031000")));
    }

    [Fact]
    public void Diagnostics_ListTheNewestKeptFiles_AndCountThemAll()
    {
        Directory.CreateDirectory(Folder);
        for (var day = 1; day <= KitDiagnostics.MaxKeptFiles + 5; day++)
        {
            File.WriteAllText(Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"notes.json.corrupt-202609{day:00}000000")), "x");
        }

        using var store = CreateStore();
        var description = KitDiagnostics.DescribeStore(store);

        Assert.Equal(KitDiagnostics.MaxKeptFiles + 5, description["KeptFileCount"]);
        var listed = Assert.IsAssignableFrom<IReadOnlyList<IDictionary<string, object?>>>(description["KeptFiles"]);
        Assert.Equal(KitDiagnostics.MaxKeptFiles, listed.Count);
        Assert.Equal("notes.json.corrupt-20260925000000", listed[0]["Name"]);
        Assert.Equal("Corrupt", listed[0]["Kind"]);
        Assert.Null(listed[0]["SchemaVersion"]);
        Assert.Equal(new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc), listed[0]["Utc"]);
        Assert.Equal(1L, listed[0]["Size"]);
    }

    [Fact]
    public void EmptyDocumentAtAnotherVersion_IsAProgrammingError()
    {
        using var store = new JsonDocumentStore<FxNotesDocument>(
            StoreFile,
            3,
            () => new FxNotesDocument { Version = 1 },
            FxNotesDocument.Migrations,
            NullLogger.Instance);

        Assert.Throws<InvalidOperationException>(() => store.Current);
    }

    [Fact]
    public void Constructor_ChecksTheMigrations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(0, []));
        Assert.Throws<ArgumentException>(() => Create(3, [new FxNotesMigration1To2()]));
        Assert.Throws<ArgumentException>(() => Create(3, [new FxNotesMigration1To2(), new NoOpMigration(1), new FxNotesMigration2To3()]));
        Assert.Throws<ArgumentException>(() => Create(3, [new FxNotesMigration1To2(), new FxNotesMigration2To3(), new NoOpMigration(3)]));
        Assert.Throws<ArgumentException>(() => Create(3, [new NoOpMigration(0), new FxNotesMigration1To2(), new FxNotesMigration2To3()]));
        Create(3, FxNotesDocument.Migrations).Dispose();
        Create(1, []).Dispose();

        JsonDocumentStore<FxNotesDocument> Create(int version, IReadOnlyList<IJsonMigration> migrations)
            => new(StoreFile, version, FxNotesDocument.CreateEmpty, migrations, NullLogger.Instance);
    }

    [Fact]
    public void Paths_AreFull_AndTheStoreIsNamedAfterTheFile()
    {
        using var store = CreateStore();

        Assert.Equal(Path.GetFullPath(StoreFile), store.FilePath);
        Assert.Equal("notes", store.StoreName);
        Assert.Equal(3, store.CurrentVersion);
        Assert.Same(JsonStoreFormat.Options, store.SerializerOptions);
        Assert.Equal(Path.Combine(Path.GetFullPath(Folder), "snapshots"), store.SnapshotDirectory);
        Assert.Equal(store.FilePath + ".last-restore", store.LastRestoreMarkerPath);
    }

    [Fact]
    public async Task RestoreAtLoad_LooksInTheStoresSnapshotFolder_AndSnapshotsElsewhereAreNamedOnce()
    {
        // The restore at load sees only SnapshotDirectory; a plugin that snapshots somewhere else is told so, once.
        var ct = TestContext.Current.CancellationToken;
        var other = _directory.Combine("elsewhere");
        var logger = new ListLogger();
        using (var store = new JsonDocumentStore<FxNotesDocument>(StoreFile, 3, FxNotesDocument.CreateEmpty, FxNotesDocument.Migrations, logger, snapshotDirectory: other))
        {
            Assert.Equal(Path.GetFullPath(other), store.SnapshotDirectory);
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            await store.SnapshotAsync(other, 7, ct);
            await store.SnapshotAsync(other + Path.DirectorySeparatorChar, 7, ct);
            Assert.Empty(logger.At(LogLevel.Warning));

            await store.SnapshotAsync(_directory.Combine("fx", "snapshots"), 7, ct);
            await store.PreOpSnapshotAsync(_directory.Combine("fx", "snapshots"), SnapshotOperations.Other, ct);
            Assert.Single(logger.At(LogLevel.Warning), e => e.Message.Contains("restores from " + Path.GetFullPath(other), StringComparison.Ordinal));
        }

        File.Delete(StoreFile);
        using (var reopened = new JsonDocumentStore<FxNotesDocument>(StoreFile, 3, FxNotesDocument.CreateEmpty, FxNotesDocument.Migrations, NullLogger.Instance, snapshotDirectory: other))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, reopened.Status);
            Assert.Single(reopened.Current.Notes);
        }

        // The default folder holds snapshots too, but this store doesn't look there.
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        Directory.Delete(other, recursive: true);
        using var elsewhere = new JsonDocumentStore<FxNotesDocument>(StoreFile, 3, FxNotesDocument.CreateEmpty, FxNotesDocument.Migrations, NullLogger.Instance, snapshotDirectory: other);
        Assert.Equal(StoreStatus.Ok, elsewhere.Status);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Folder, "snapshots"), "notes-*.json"));
    }

    [Fact]
    public async Task ReadSnapshot_GivesTheDocumentARestoreWould_AndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshots = Path.Combine(Folder, "snapshots");
        using var store = CreateStore();
        await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
        var snapshot = Path.GetFileName((await store.SnapshotAsync(snapshots, 7, ct))!);
        await store.UpdateAsync(d => d.Add(FxNote.Create(2)), ct);
        var before = await File.ReadAllBytesAsync(StoreFile, ct);

        var read = await store.ReadSnapshotAsync(snapshots, snapshot.ToUpperInvariant(), ct);

        Assert.Single(read.Notes);
        Assert.Equal(2, store.Current.Notes.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, ct));
        Assert.Empty(Directory.GetDirectories(snapshots));
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => store.ReadSnapshotAsync(snapshots, "../notes.json", ct))).Kind);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "Snapshot": 3, "RestoredUtc": "yesterday" }""")]
    public async Task UnreadableLastRestoreMarker_TriesTheSnapshotsNewestFirst(string marker)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var snapshots = Path.Combine(Folder, "snapshots");
        using (var store = CreateStore(time: clock))
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            await store.SnapshotAsync(snapshots, 7, ct);
            clock.Advance(TimeSpan.FromDays(1));
            await store.UpdateAsync(d => d.Add(FxNote.Create(2)), ct);
            await store.SnapshotAsync(snapshots, 7, ct);
        }

        await File.WriteAllTextAsync(StoreFile + ".last-restore", marker, ct);
        File.Delete(StoreFile);
        File.Delete(BackupFile);

        using var reopened = CreateStore(time: clock);

        Assert.Equal(StoreStatus.RestoredFromSnapshot, reopened.Status);
        Assert.Equal(2, reopened.Current.Notes.Count);
        Assert.Contains("notes-20261002-031000.json", reopened.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMarkerWhoseSnapshotsWereAllDeletedByHand_IsReleasedAtTheNextDailySnapshot_AndOneThatComesBackRotatesAsAny()
    {
        // Bookmarks 1.2.1 (reversing, for the JSON store, the SQLite recovery journal's choice): a marker whose kept
        // snapshots are all gone keeps nothing, and stayed for good with its Warning and Diagnostics line. The next daily
        // snapshot releases it, with an Information; a kept snapshot put back after that rotates as any. The SQLite
        // database's twin, SqliteRecoveryJournalTests', still keeps the marker.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var snapshots = Path.Combine(Folder, "snapshots");
        string first;
        using (var store = CreateStore(time: clock))
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            first = (await store.SnapshotAsync(snapshots, 3, ct))!;
        }

        var saved = _directory.Combine("saved-" + Path.GetFileName(first));
        File.Copy(first, saved);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        using var restarted = CreateStore(logger, clock);
        Assert.Equal(StoreStatus.RestoredFromSnapshot, restarted.Status);
        var marker = restarted.KeepSnapshotsMarkerPath;
        Assert.True(File.Exists(marker));

        // While one of its snapshots is there, the marker stays.
        clock.Advance(TimeSpan.FromDays(1));
        await restarted.SnapshotAsync(snapshots, 3, ct);
        Assert.True(File.Exists(marker));
        Assert.NotNull(restarted.KeepSnapshotsBeforeUtc);

        // An admin deletes the kept snapshot by hand: the next daily snapshot releases the marker.
        File.Delete(first);
        clock.Advance(TimeSpan.FromDays(1));
        await restarted.SnapshotAsync(snapshots, 3, ct);

        Assert.False(File.Exists(marker));
        Assert.Null(restarted.KeepSnapshotsBeforeUtc);
        Assert.Contains(logger.At(LogLevel.Information), e => e.Message.Contains("snapshots rotate as usual", StringComparison.Ordinal));

        // The snapshot comes back: it rotates as any, here as the oldest.
        File.Copy(saved, first);
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            await restarted.SnapshotAsync(snapshots, 3, ct);
        }

        Assert.False(File.Exists(first), "The snapshot that came back after the release was kept.");
        Assert.Equal(3, restarted.ListSnapshots(snapshots).Count(s => s.Kind == SnapshotKind.Daily));
        Assert.DoesNotContain(restarted.ListSnapshots(snapshots), s => s.Kept);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMarkerDatedFarAhead_IsCappedAtAWeekAhead_KeepingItsSnapshots(bool byName)
    {
        // Bookmarks 1.2.1: a marker's time is what an earlier version keeps by (every snapshot dated before it); one far ahead
        // (a snapshot taken while the clock was wrong) made an earlier version, after a downgrade, keep every snapshot for good.
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var snapshots = Path.Combine(Folder, "snapshots");
        string first;
        using (var store = CreateStore(time: clock))
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            first = (await store.SnapshotAsync(snapshots, 3, ct))!;
        }

        var marker = StoreFile + JsonDocumentStore<FxNotesDocument>.KeepSnapshotsMarkerSuffix;
        var json = new JsonObject { ["KeepSnapshotsBeforeUtc"] = now.UtcDateTime.AddYears(10), ["SinceUtc"] = now.UtcDateTime.AddDays(-1), ["Reason"] = "test" };
        if (byName)
        {
            json["Snapshots"] = new JsonArray(Path.GetFileName(first));
        }

        await File.WriteAllTextAsync(marker, json.ToJsonString(), ct);

        using var reopened = CreateStore(time: clock);
        Assert.Equal(marker, reopened.KeepSnapshotsMarkerPath);
        var cap = KeepSnapshotsMarker.CapOf(now.UtcDateTime);
        Assert.Equal(cap, reopened.KeepSnapshotsBeforeUtc);
        Assert.Equal(cap, JsonNode.Parse(await File.ReadAllTextAsync(marker, ct))!["KeepSnapshotsBeforeUtc"]!.GetValue<DateTime>());
        Assert.True(Assert.Single(reopened.ListSnapshots(snapshots), s => s.Name == Path.GetFileName(first)).Kept);
        Assert.Equal("test", JsonNode.Parse(await File.ReadAllTextAsync(marker, ct))!["Reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnIncidentWithASnapshotDatedFarAhead_RecordsAMarkerCappedAtAWeekAhead()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now.AddYears(5));
        var snapshots = Path.Combine(Folder, "snapshots");
        using (var store = CreateStore(time: clock))
        {
            // Taken while the clock was five years ahead.
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            await store.SnapshotAsync(snapshots, 3, ct);
        }

        clock.Set(now);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        using var restarted = CreateStore(time: clock);

        Assert.Equal(StoreStatus.RestoredFromSnapshot, restarted.Status);
        Assert.Equal(KeepSnapshotsMarker.CapOf(now.UtcDateTime), restarted.KeepSnapshotsBeforeUtc);
        Assert.All(restarted.ListSnapshots(snapshots), s => Assert.True(s.Kept));
    }

    [Fact]
    public async Task RefreshBackup_LeavesNoEarlierVersionBesideTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = CreateStore();
        Assert.False(await store.RefreshBackupAsync(ct) && File.Exists(BackupFile));

        await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
        await store.UpdateAsync(d => d.Add(FxNote.Create(2)), ct);
        Assert.NotEqual(await File.ReadAllBytesAsync(StoreFile, ct), await File.ReadAllBytesAsync(BackupFile, ct));

        Assert.True(await store.RefreshBackupAsync(ct));

        Assert.Equal(await File.ReadAllBytesAsync(StoreFile, ct), await File.ReadAllBytesAsync(BackupFile, ct));
        Assert.Equal(2, store.Current.Notes.Count);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RestoreAtLoad_PassesOverASnapshotItHasNoPermissionToRead_InsteadOfA503AtEveryUse()
    {
        // A snapshot that can't be read for want of permission stays so, unlike one in use; it used to make every use a
        // 503 "The data file couldn't be read", with a Warning and a stack trace each time.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Denying a file's reading needs a Windows ACL.");
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var snapshots = Path.Combine(Folder, "snapshots");
        using (var store = CreateStore(time: clock))
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            await store.SnapshotAsync(snapshots, 7, ct);
            clock.Advance(TimeSpan.FromDays(1));
            await store.UpdateAsync(d => d.Add(FxNote.Create(2)), ct);
            await store.SnapshotAsync(snapshots, 7, ct);
        }

        File.Delete(StoreFile);
        File.Delete(BackupFile);
        var denied = Path.Combine(snapshots, "notes-20261002-031000.json");
        var logger = new ListLogger();
        using (DenyReading(denied))
        {
            // The older snapshot is restored, and the banner names the one passed over.
            using (var store = CreateStore(logger, clock))
            {
                Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
                Assert.Single(store.Current.Notes);
                Assert.Contains("restored from the snapshot notes-20261001-031000.json", store.StatusMessage, StringComparison.Ordinal);
                Assert.EndsWith("Passed over first: notes-20261002-031000.json (no permission to read it).", store.StatusMessage, StringComparison.Ordinal);
                Assert.Single(logger.At(LogLevel.Warning), e => e.Message.Contains("no permission to read it", StringComparison.Ordinal));
                Assert.Empty(logger.At(LogLevel.Error));
            }

            // Alone, it leaves the store read-only with the banner (ReadOnlyDataMissing): every use answers, logged once.
            File.Delete(StoreFile);
            File.Delete(BackupFile);
            File.Delete(Path.Combine(snapshots, "notes-20261001-031000.json"));
            var alone = new ListLogger();
            using var readOnly = CreateStore(alone, clock);
            for (var i = 0; i < 5; i++)
            {
                Assert.Empty(readOnly.Current.Notes);
                Assert.Equal(StoreStatus.ReadOnlyDataMissing, readOnly.Status);
            }

            Assert.Contains("none of them could be restored: notes-20261002-031000.json (no permission to read it).", readOnly.StatusMessage, StringComparison.Ordinal);
            Assert.Single(alone.At(LogLevel.Warning));
            Assert.Single(alone.At(LogLevel.Error));
        }
    }

    [Fact]
    public async Task RestoreAtLoad_RunsThePluginsStepAfterARestore_BeforeSaving_AndOnlyThen()
    {
        // The plugin's afterRestoreAtLoad step (Bookmarks: the clip jobs its admin restore ends) runs on the restored data
        // before it is saved; a store whose file loads, or whose .bak is used, never runs it.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        using (var store = CreateStore(time: clock))
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            await store.SnapshotAsync(Path.Combine(Folder, "snapshots"), 7, ct);
        }

        File.Delete(StoreFile);
        File.Delete(BackupFile);
        var seen = new List<int>();
        Func<FxNotesDocument, FxNotesDocument> step = d =>
        {
            seen.Add(d.Notes.Count);
            return d.Add(FxNote.Create(7));
        };

        using (var restored = CreateStore(time: clock, afterRestoreAtLoad: step))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, restored.Status);
            Assert.Equal(2, restored.Current.Notes.Count);
        }

        Assert.Equal([1], seen);
        using (var saved = CreateStore(time: clock, afterRestoreAtLoad: step))
        {
            Assert.Equal(StoreStatus.Ok, saved.Status);
            Assert.Equal(2, saved.Current.Notes.Count);
            await saved.UpdateAsync(d => d.Add(FxNote.Create(3)), ct);
        }

        File.Delete(StoreFile);
        using (var fromBackup = CreateStore(time: clock, afterRestoreAtLoad: step))
        {
            Assert.Equal(StoreStatus.RecoveredFromBackup, fromBackup.Status);
        }

        Assert.Equal([1], seen);

        // A step that fails, or returns no document or one at another version: the restored data is saved as it is, with an
        // Error naming the snapshot.
        Func<FxNotesDocument, FxNotesDocument>[] failing =
        [
            _ => throw new InvalidOperationException("Step failed."),
            _ => null!,
            d => d with { Version = 2 }
        ];
        foreach (var bad in failing)
        {
            File.Delete(StoreFile);
            File.Delete(BackupFile);
            File.Delete(StoreFile + ".last-restore");
            var logger = new ListLogger();
            using var store = CreateStore(logger, clock, bad);
            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.Single(store.Current.Notes);
            Assert.Single(logger.At(LogLevel.Error), e => e.Message.Contains("at load", StringComparison.Ordinal) && e.Message.Contains("notes-20261001-031000.json", StringComparison.Ordinal) && e.Message.EndsWith("the restored data is saved as it is", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task UnreadableFile_RestoredAtLoad_AndAnAdminsRecovery_RunThePluginsStep_StartEmptyDoesnt()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var snapshots = Path.Combine(Folder, "snapshots");
        using (var store = CreateStore(time: clock))
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            await store.SnapshotAsync(snapshots, 7, ct);
        }

        File.Delete(BackupFile);
        WriteFile("not json");
        var seen = new List<int>();
        Func<FxNotesDocument, FxNotesDocument> step = d =>
        {
            seen.Add(d.Notes.Count);
            return d.Add(FxNote.Create(7));
        };
        using (var restored = CreateStore(time: clock, afterRestoreAtLoad: step))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, restored.Status);
            Assert.Equal(2, restored.Current.Notes.Count);
        }

        Assert.Equal([1], seen);

        // The snapshot made unusable (a newer version's) and the file unreadable again: read-only, so no step at load; an
        // admin's restore of a readable copy put back runs it.
        var original = Directory.GetFiles(snapshots, "notes-*.json").Single();
        var good = await File.ReadAllBytesAsync(original, ct);
        await File.WriteAllTextAsync(original, """{ "Version": 4, "Notes": [] }""", ct);
        File.Delete(BackupFile);
        WriteFile("not json either");
        using var store2 = CreateStore(time: clock, afterRestoreAtLoad: step);
        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store2.Status);
        Assert.Equal([1], seen);
        await File.WriteAllBytesAsync(Path.Combine(snapshots, "notes-20261002-031000.json"), good, ct);
        var result = await store2.RecoverAsync(snapshots, "notes-20261002-031000.json", startEmpty: false, ct);
        Assert.Equal("notes-20261002-031000.json", result.RestoredFrom);
        Assert.Equal([1, 1], seen);
        Assert.Equal(2, store2.Current.Notes.Count);
    }

    [Fact]
    public async Task Recover_StartEmpty_RunsNoStep_AndEndsTheWaitOfABackgroundWriter()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("not json");
        var ran = 0;
        using var store = CreateStore(afterRestoreAtLoad: d =>
        {
            ran++;
            return d;
        });
        Assert.True(store.IsReadOnly);
        var waiting = store.WaitUntilWritableAsync(ct);
        using (var cancel = new System.Threading.CancellationTokenSource())
        {
            var cancelled = store.WaitUntilWritableAsync(cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }

        Assert.False(waiting.IsCompleted);
        await store.RecoverAsync(Path.Combine(Folder, "snapshots"), null, startEmpty: true, ct);

        await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0, ran);
        Assert.True(store.WaitUntilWritableAsync(ct).IsCompletedSuccessfully);
        await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Recover_OfASnapshotTheServerMayNotRead_Is409_AndOnceItMay_RestoresIt()
    {
        // A snapshot without permission, passed over at load (ReadOnlyDataMissing): the admin's restore of it is refused
        // while the permission is missing, and works once it is given back, without a restart.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Denying a file's reading needs a Windows ACL.");
        var ct = TestContext.Current.CancellationToken;
        var snapshots = Path.Combine(Folder, "snapshots");
        using (var store = CreateStore())
        {
            await store.UpdateAsync(d => d.Add(FxNote.Create(1)), ct);
            await store.SnapshotAsync(snapshots, 7, ct);
        }

        File.Delete(StoreFile);
        File.Delete(BackupFile);
        var denied = Directory.GetFiles(snapshots, "notes-*.json").Single();
        using var readOnly = CreateStore();
        using (DenyReading(denied))
        {
            Assert.Equal(StoreStatus.ReadOnlyDataMissing, readOnly.Status);
            var refused = await Assert.ThrowsAsync<PluginException>(() => readOnly.RecoverAsync(snapshots, Path.GetFileName(denied), startEmpty: false, ct));
            Assert.Equal(PluginErrorKind.Conflict, refused.Kind);
            Assert.Contains("no permission to read it", refused.Message, StringComparison.Ordinal);
            Assert.True(readOnly.IsReadOnly);
        }

        await readOnly.RecoverAsync(snapshots, Path.GetFileName(denied), startEmpty: false, ct);
        Assert.Equal(StoreStatus.RestoredFromSnapshot, readOnly.Status);
        Assert.Single(readOnly.Current.Notes);
    }

    [Fact]
    public void Format_IsBookmarks10s()
    {
        // PascalCase, indented, enums as strings, nulls omitted (bm:Storage/JsonBookmarkStore.cs).
        var options = JsonStoreFormat.Options;
        Assert.True(options.IsReadOnly);
        Assert.True(options.WriteIndented);
        Assert.Null(options.PropertyNamingPolicy);
        Assert.Equal(System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, options.DefaultIgnoreCondition);

        var json = JsonSerializer.Serialize(new FxNote { Kind = FxNoteKind.Pinned, Comment = null }, options);
        Assert.Contains("\"Kind\": \"Pinned\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Comment", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EndTicks", json, StringComparison.Ordinal);
        Assert.False(JsonStoreFormat.CreateOptions().IsReadOnly);
    }

    [Theory]
    [InlineData("""{ "Version": 4 }""", 4)]
    [InlineData("""{ "A": { "Version": 5 }, "B": [ { "Version": 6 } ] }""", null)]
    [InlineData("""{ "Version": 1, "Version": 2 }""", 2)]
    [InlineData("""{ "version": 2 }""", null)]
    [InlineData("""  { }  """, null)]
    public void ReadVersion_ReadsOnlyTheRootsVersion(string json, int? expected)
    {
        Assert.Equal(expected, JsonStoreFormat.ReadVersion(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void SkipByteOrderMark_SkipsOnlyTheMark()
    {
        Assert.Equal("{}"u8.ToArray(), JsonStoreFormat.SkipByteOrderMark([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']).ToArray());
        Assert.Equal("{}"u8.ToArray(), JsonStoreFormat.SkipByteOrderMark("{}"u8).ToArray());
        Assert.Equal(new byte[] { 0xEF, 0xBB }, JsonStoreFormat.SkipByteOrderMark([0xEF, 0xBB]).ToArray());
    }

    public void Dispose() => _directory.Dispose();

    private string Folder => Path.GetDirectoryName(StoreFile)!;

    private string BackupFile => StoreFile + ".bak";

    private JsonDocumentStore<FxNotesDocument> CreateStore(ILogger? logger = null, TimeProvider? time = null, Func<FxNotesDocument, FxNotesDocument>? afterRestoreAtLoad = null)
        => new(StoreFile, FxNotesDocument.CurrentVersion, FxNotesDocument.CreateEmpty, FxNotesDocument.Migrations, logger ?? NullLogger.Instance, timeProvider: time, afterRestoreAtLoad: afterRestoreAtLoad);

    // Denies the current user reading a file until disposed (a Windows ACL).
    [SupportedOSPlatform("windows")]
    private static Denial DenyReading(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(rule);
        file.SetAccessControl(security);
        return new Denial(file, rule);
    }

    // The test document's store with a version 2 → 3 step that always throws.
    private JsonDocumentStore<FxNotesDocument> CreateFailingStore(ILogger logger, TimeProvider time)
        => new(StoreFile, 3, FxNotesDocument.CreateEmpty, [new FxNotesMigration1To2(), new ThrowingMigration(2)], logger, timeProvider: time);

    private List<string?> FileNames() => Directory.GetFiles(Folder).Select(Path.GetFileName).Order().ToList();

    private void WriteFile(string content)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(StoreFile, content);
    }

    private sealed class ThrowingMigration(int fromVersion) : IJsonMigration
    {
        public int FromVersion { get; } = fromVersion;

        public void Apply(JsonObject root) => throw new InvalidOperationException("Step failed.");
    }

    private sealed class NoOpMigration(int fromVersion) : IJsonMigration
    {
        public int FromVersion { get; } = fromVersion;

        public void Apply(JsonObject root)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class Denial(FileInfo file, FileSystemAccessRule rule) : IDisposable
    {
        public void Dispose()
        {
            if (!file.Exists)
            {
                return;
            }

            var security = file.GetAccessControl();
            security.RemoveAccessRule(rule);
            file.SetAccessControl(security);
        }
    }

    private sealed class FixedVersionDocument : IVersionedDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("Other")]
        public int Version { get; init; } = 2;
    }
}
