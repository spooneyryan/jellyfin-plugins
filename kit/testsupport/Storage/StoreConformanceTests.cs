using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.TestSupport.Storage;

/// <summary>
/// The conformance suite of <see cref="JsonDocumentStore{TDoc}"/>, which the kit and every
/// plugin with a JSON store run against their own document: atomic writes, <c>.bak</c> recovery and corrupt-file
/// quarantine, migrations and the <c>.v&lt;N&gt;</c> copy, read-only mode for newer files and for failed migrations,
/// extension data, concurrent writes, snapshots (and the earlier ones kept after starting empty), and a timing measurement
/// for a 5 MB document.
/// </summary>
/// <remarks>
/// A plugin derives a sealed test class and returns its <see cref="StoreConformance{TDoc}"/> from
/// <see cref="CreateSubject"/>. Checks that don't apply (no fixtures, no older versions, records without extension data,
/// which the plugin has to state in its <c>AddUnknownRecordField</c>) report as skipped. The suite takes its document
/// through the subject instead of a type parameter because xunit test classes are public and kit types and plugin
/// documents are internal.
/// </remarks>
public abstract class StoreConformanceTests : IDisposable
{
    /// <summary>The size of the timing document (the size at which a JSON store should move to SQLite).</summary>
    public const long TimingDocumentBytes = 5L * 1024 * 1024;

    private const string UnknownField = "ConformanceUnknownField";

    // The SnapshotsToKeep of the missing-file tests.
    private const int KeepInTests = 3;

    private readonly TempDirectory _directory = new();
    private StoreConformanceSubject? _subject;

    private StoreConformanceSubject Subject => _subject ??= CreateSubject();

    private string StoreFile => _directory.Combine("data", Subject.StoreFileName);

    private string DataDirectory => _directory.Combine("data");

    private string BackupFile => StoreFile + ".bak";

    private string SnapshotDirectory => _directory.Combine("data", "snapshots");

    private int Current => Subject.CurrentVersion;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Loading

    [Fact]
    public void MissingFile_StartsEmpty_AndCreatesNothing()
    {
        using var store = Open();

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.False(store.IsReadOnly);
        Assert.Equal(0, store.Count());
        Assert.Null(store.LoadedVersion);
        Assert.Null(store.StatusMessage);
        Assert.Equal(Current, store.DocumentVersion);
        Assert.False(Directory.Exists(DataDirectory) && Directory.EnumerateFileSystemEntries(DataDirectory).Any());
    }

    [Fact]
    public async Task WrittenRecords_SurviveAReload()
    {
        await WriteThroughStoreAsync(3);

        using var store = Open();

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Equal(3, store.Count());
        Assert.Equal(Current, store.LoadedVersion);
        Assert.Equal(Current, store.DocumentVersion);
    }

    [Fact]
    public async Task WrittenFile_IsIndentedUtf8WithoutBom_CarriesTheVersion_AndOmitsNulls()
    {
        var bytes = await WriteThroughStoreAsync(2);

        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "The file starts with a byte order mark.");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains(Environment.NewLine + "  \"Version\": " + Current.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(bytes));
        Assert.Equal(Current, (int)root[JsonStoreFormat.VersionProperty]!);
        Assert.Empty(NullProperties(root, "$"));
    }

    [Fact]
    public async Task FileWithAByteOrderMark_Loads()
    {
        var bytes = await WriteThroughStoreAsync(2);
        await File.WriteAllBytesAsync(StoreFile, [0xEF, 0xBB, 0xBF, .. bytes], Ct);

        using var store = Open();

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void FileWithoutAVersion_IsVersionOne()
    {
        if (Current != 1)
        {
            Assert.Skip("The store is past version 1; files without a Version are covered by its version-1 fixtures.");
        }

        WriteFile(Edit(Subject.CreateFile(2), root => root.Remove(JsonStoreFormat.VersionProperty)));

        using var store = Open();

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Equal(1, store.LoadedVersion);
        Assert.Equal(1, store.DocumentVersion);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void CurrentVersionFixtures_LoadUnchanged()
    {
        var fixtures = Subject.Fixtures.Where(f => f.Version == Current).ToList();
        if (fixtures.Count == 0)
        {
            Assert.Skip("The plugin has no fixture at its current schema version.");
        }

        foreach (var fixture in fixtures)
        {
            ResetDataDirectory();
            WriteFile(fixture.Content);

            using (var store = Open())
            {
                Assert.True(StoreStatus.Ok == store.Status, $"{fixture.Name}: status {store.Status}, {store.StatusMessage}");
                Assert.Equal(fixture.Records, store.Count());
                Assert.Equal(Current, store.LoadedVersion);
            }

            Assert.True(fixture.Content.AsSpan().SequenceEqual(File.ReadAllBytes(StoreFile)), $"{fixture.Name}: loading changed the file.");
            Assert.Equal(new[] { StoreFile }, Directory.GetFiles(DataDirectory));
        }
    }

    [Fact]
    public void OlderFixtures_AreMigrated_CopiedFirst_AndSavedAtOnce()
    {
        var fixtures = Subject.Fixtures.Where(f => f.Version < Current).ToList();
        if (fixtures.Count == 0)
        {
            Assert.Skip("The plugin has no fixture of an older schema version.");
        }

        foreach (var fixture in fixtures)
        {
            ResetDataDirectory();
            WriteFile(fixture.Content);

            using (var store = Open())
            {
                Assert.True(StoreStatus.Migrated == store.Status, $"{fixture.Name}: status {store.Status}, {store.StatusMessage}");
                Assert.Equal(fixture.Version, store.MigratedFrom);
                Assert.Equal(fixture.Version, store.LoadedVersion);
                Assert.Equal(fixture.Records, store.Count());
                Assert.Equal(Current, store.DocumentVersion);
            }

            var copy = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.v{fixture.Version}-*"));
            Assert.True(fixture.Content.AsSpan().SequenceEqual(File.ReadAllBytes(copy)), $"{fixture.Name}: the .v{fixture.Version} copy differs.");
            Assert.True(fixture.Content.AsSpan().SequenceEqual(File.ReadAllBytes(BackupFile)), $"{fixture.Name}: .bak isn't the old file.");
            Assert.Equal(Current, ReadVersion(StoreFile));

            using var reloaded = Open();
            Assert.Equal(StoreStatus.Ok, reloaded.Status);
            Assert.Equal(fixture.Records, reloaded.Count());
            Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.v*"));
        }
    }

    // ---- Writes

    [Fact]
    public async Task Update_KeepsThePreviousFileAsBackup_AndLeavesNoTempFile()
    {
        using var store = Open();
        await store.AddRecordAsync(1, Ct);
        var first = await File.ReadAllBytesAsync(StoreFile, Ct);
        await store.AddRecordAsync(2, Ct);

        Assert.Equal(first, await File.ReadAllBytesAsync(BackupFile, Ct));
        Assert.False(File.Exists(StoreFile + ".tmp"));
        Assert.Equal(2, store.Count());
        Assert.Equal(2, store.CountFile(StoreFile));
    }

    [Fact]
    public async Task Update_ReturningTheSameDocument_WritesNothing()
    {
        using var store = Open();
        await store.AddRecordAsync(1, Ct);
        File.Delete(StoreFile);

        await store.NoOpUpdateAsync(Ct);

        Assert.False(File.Exists(StoreFile));
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public async Task StaleTempFile_FromACrash_IsIgnoredAndReplaced()
    {
        await WriteThroughStoreAsync(2);
        await File.WriteAllTextAsync(StoreFile + ".tmp", "{ half a docu", Ct);

        using var store = Open();
        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Equal(2, store.Count());
        await store.AddRecordAsync(3, Ct);

        Assert.False(File.Exists(StoreFile + ".tmp"));
        Assert.Equal(3, store.CountFile(StoreFile));
    }

    [Fact]
    public async Task FailedWrite_LeavesMemoryAndFileUnchanged_RemovesTheTempFile_AndIsUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs Windows file sharing to make the replace fail.");
        }

        var before = await WriteThroughStoreAsync(2);
        var logger = new ListLogger();
        using var store = Open(new StoreOpenOptions(Logger: logger));
        Assert.Equal(2, store.Count());

        using (new FileStream(StoreFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<PluginException>(() => store.AddRecordAsync(3, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
            Assert.Equal(StoreMessages.WriteFailed, ex.Message);
        }

        Assert.Equal(2, store.Count());
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.False(File.Exists(StoreFile + ".tmp"));
        Assert.Single(logger.At(LogLevel.Error));

        await store.AddRecordAsync(4, Ct);
        Assert.Equal(3, store.CountFile(StoreFile));
    }

    [Fact]
    public async Task ConcurrentUpdates_AreAllPersisted()
    {
        using (var store = Open())
        {
            await Task.WhenAll(Enumerable.Range(0, 50).Select(n => Task.Run(() => store.AddRecordAsync(n, Ct), Ct)));
            Assert.Equal(50, store.Count());
        }

        using var reloaded = Open();
        Assert.Equal(50, reloaded.Count());
    }

    // ---- Recovery

    [Fact]
    public async Task CorruptFile_IsQuarantined_AndTheBackupIsUsed()
    {
        await WriteThroughStoreAsync(2);
        var backup = await File.ReadAllBytesAsync(BackupFile, Ct);
        await File.WriteAllTextAsync(StoreFile, "{ \"Version\": 1, not json", Ct);
        var logger = new ListLogger();

        using (var store = Open(new StoreOpenOptions(Logger: logger)))
        {
            Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
            Assert.False(store.IsReadOnly);
            Assert.Equal(1, store.Count());
            Assert.NotNull(store.StatusMessage);
            Assert.False(File.Exists(StoreFile));
            var corrupt = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
            Assert.Equal("{ \"Version\": 1, not json", await File.ReadAllTextAsync(corrupt, Ct));
            Assert.Equal(backup, await File.ReadAllBytesAsync(BackupFile, Ct));
            Assert.NotEmpty(logger.At(LogLevel.Error));

            await store.AddRecordAsync(7, Ct);
        }

        using var reloaded = Open();
        Assert.Equal(StoreStatus.Ok, reloaded.Status);
        Assert.Equal(2, reloaded.Count());
    }

    [Fact]
    public async Task CorruptFile_WithoutABackupOrSnapshots_IsReadOnly_AndLeftInPlace_UntilAnAdminStartsEmpty()
    {
        // The store never starts empty on its own where data may be
        // lost, the SQLite database's rule. Until Bookmarks 1.2 this was the old RecoveredEmpty: the file moved aside,
        // and an empty store that the next write made the data.
        var content = "\0\0\0"u8.ToArray();
        WriteFile(content);
        var logger = new ListLogger();

        using (var store = Open(new StoreOpenOptions(Logger: logger)))
        {
            Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
            Assert.True(store.IsReadOnly);
            Assert.True(store.CanRecover);
            Assert.Equal(0, store.Count());
            Assert.StartsWith(
                "The data file couldn't be read and there was no backup. There is no snapshot of it. Nothing was changed: the data file is left as it is, and the data is read-only. To go on, start empty on the plugin's settings page (what couldn't be read is then kept as " + Subject.StoreFileName + ".corrupt-<time>)",
                store.StatusMessage,
                StringComparison.Ordinal);
            Assert.NotNull(Assert.Single(logger.At(LogLevel.Error)).Exception);
            var write = await Assert.ThrowsAsync<PluginException>(() => store.AddRecordAsync(1, Ct));
            Assert.Equal((PluginErrorKind.Unavailable, StoreMessages.DataUnreadable), (write.Kind, write.Message));
            Assert.Equal(StoreMessages.ReadOnlyForUsers(StoreStatus.ReadOnlyDataUnreadable), write.NonAdminMessage);
            Assert.Null(await store.SnapshotAsync(SnapshotDirectory, 1, Ct));
            var export = Assert.Throws<PluginException>(() => store.ExportData());
            Assert.Equal((PluginErrorKind.Unavailable, StoreMessages.ExportDataUnreadable), (export.Kind, export.Message));
        }

        // Nothing moved or written, so a restart finds the same: had the file been moved aside, it would find none, and
        // start empty on its own.
        Assert.Equal(new[] { StoreFile }, Directory.GetFiles(DataDirectory));
        Assert.Equal(content, await File.ReadAllBytesAsync(StoreFile, Ct));
        using (var again = Open())
        {
            Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, again.Status);

            // An admin's choice: the file is kept as .corrupt-<ts>, and the store is empty and writable at once.
            var result = await again.RecoverAsync(SnapshotDirectory, null, startEmpty: true, Ct);
            Assert.True(result.StartedEmpty);
            var corrupt = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
            Assert.Equal([Path.GetFileName(corrupt)], result.MovedAside);
            Assert.Equal(content, await File.ReadAllBytesAsync(corrupt, Ct));
            Assert.Equal(StoreStatus.RecoveredEmpty, again.Status);
            Assert.False(again.IsReadOnly);
            Assert.False(again.CanRecover);
            Assert.Equal("The data file couldn't be read and there was no backup; an admin chose to start empty. What was found in its place is kept as " + Path.GetFileName(corrupt) + ".", again.StatusMessage);

            // With no snapshot at all there is nothing to keep, and no marker (the kept snapshots are recorded by
            // name).
            Assert.False(File.Exists(StoreFile + ".keep-snapshots"));
            Assert.Null(result.KeepSnapshotsBeforeUtc);
            await again.AddRecordAsync(1, Ct);
            Assert.Equal(1, again.CountFile(StoreFile));
        }

        using var restarted = Open();
        Assert.Equal(StoreStatus.Ok, restarted.Status);
        Assert.Equal(1, restarted.Count());
    }

    [Fact]
    public void CorruptFile_WithACorruptBackup_AndNoSnapshots_IsReadOnly_AndLeavesBoth()
    {
        // Until Bookmarks 1.2: the old RecoveredEmpty, the .bak left in place for the second write to overwrite.
        WriteFile("[]"u8.ToArray());
        File.WriteAllText(BackupFile, "garbage");

        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        Assert.Equal(0, store.Count());
        Assert.StartsWith(
            "The data file couldn't be read, and its backup couldn't be read either. There is no snapshot of it. Nothing was changed: the data file and its backup are left as they are, and the data is read-only.",
            store.StatusMessage,
            StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
        Assert.Equal("[]", File.ReadAllText(StoreFile));
        Assert.Equal("garbage", File.ReadAllText(BackupFile));
    }

    [Fact]
    public async Task FileThatDoesNotDeserialize_AtTheCurrentVersion_IsUnreadable_AndMovedAsideWhenASnapshotTakesItsPlace()
    {
        var broken = Edit(Subject.CreateFile(2), Subject.BreakDeserialization);
        WriteFile(broken);
        using (var alone = Open())
        {
            Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, alone.Status);
            Assert.Equal(broken, await File.ReadAllBytesAsync(StoreFile, Ct));
        }

        // Beside a snapshot of data, it is moved aside as it was, and the snapshot restored.
        ResetDataDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(BackupFile);
        WriteFile(broken);
        using var store = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        var corrupt = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal(broken, await File.ReadAllBytesAsync(corrupt, Ct));
    }

    [Fact]
    public void MissingFile_WithABackup_UsesTheBackup()
    {
        File.WriteAllBytes(EnsureDirectory(BackupFile), Subject.CreateFile(3));

        using var store = Open();

        Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
        Assert.Equal(3, store.Count());
        Assert.NotNull(store.StatusMessage);
        Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
    }

    [Fact]
    public void OlderBackup_OfACorruptFile_IsMigrated()
    {
        var backup = Subject.CreateFile(2);
        File.WriteAllBytes(EnsureDirectory(BackupFile), backup);
        File.WriteAllText(StoreFile, "not json");

        using (var store = Open(new StoreOpenOptions(ExtraMigration: new DelegateMigration(Current, _ => { }))))
        {
            Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
            Assert.Equal(Current, store.MigratedFrom);
            Assert.Equal(2, store.Count());
        }

        Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        var copy = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.v{Current}-*"));
        Assert.Equal(backup, File.ReadAllBytes(copy));
        Assert.Equal(backup, File.ReadAllBytes(BackupFile));
        Assert.Equal(Current + 1, ReadVersion(StoreFile));
    }

    // ---- Versions

    [Fact]
    public async Task NewerVersion_IsServedReadOnly_AndNeverTouched()
    {
        var newer = Edit(Subject.CreateFile(2), root => root[JsonStoreFormat.VersionProperty] = Current + 1);
        WriteFile(newer);
        var logger = new ListLogger();

        using var store = Open(new StoreOpenOptions(Logger: logger));

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Status);
        Assert.True(store.IsReadOnly);
        Assert.Equal(Current + 1, store.LoadedVersion);
        Assert.Equal(2, store.Count());
        Assert.NotNull(store.StatusMessage);
        var ex = await Assert.ThrowsAsync<PluginException>(() => store.AddRecordAsync(3, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
        Assert.Equal(StoreMessages.NewerVersion, ex.Message);
        await Assert.ThrowsAsync<PluginException>(() => store.NoOpUpdateAsync(Ct));
        Assert.Null(await store.SnapshotAsync(SnapshotDirectory, 7, Ct));
        Assert.False(Directory.Exists(SnapshotDirectory));
        Assert.Equal(newer, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Equal(new[] { StoreFile }, Directory.GetFiles(DataDirectory));
        Assert.Single(logger.At(LogLevel.Error));
    }

    [Fact]
    public void NewerVersion_ThatThisVersionCannotRead_IsReadOnly_AndNotQuarantined()
    {
        var newer = Edit(
            Subject.CreateFile(2),
            root =>
            {
                Subject.BreakDeserialization(root);
                root[JsonStoreFormat.VersionProperty] = Current + 1;
            });
        WriteFile(newer);

        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Status);
        Assert.Equal(0, store.Count());
        Assert.Equal(newer, File.ReadAllBytes(StoreFile));
        Assert.Equal(new[] { StoreFile }, Directory.GetFiles(DataDirectory));
    }

    [Fact]
    public async Task Migration_CopiesTheFileFirst_AndSavesTheUpgradeAtOnce()
    {
        var original = await WriteThroughStoreAsync(3);
        var logger = new ListLogger();

        using (var store = Open(new StoreOpenOptions(ExtraMigration: new DelegateMigration(Current, _ => { }), Logger: logger)))
        {
            Assert.Equal(StoreStatus.Migrated, store.Status);
            Assert.False(store.IsReadOnly);
            Assert.Equal(Current, store.MigratedFrom);
            Assert.Equal(Current + 1, store.DocumentVersion);
            Assert.Equal(3, store.Count());
            Assert.NotNull(store.StatusMessage);
        }

        var copy = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.v{Current}-*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(copy, Ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(BackupFile, Ct));
        Assert.Equal(Current + 1, ReadVersion(StoreFile));
        Assert.Empty(logger.At(LogLevel.Error));

        using var reloaded = Open(new StoreOpenOptions(ExtraMigration: new DelegateMigration(Current, _ => throw new InvalidOperationException("must not run again"))));
        Assert.Equal(StoreStatus.Ok, reloaded.Status);
        Assert.Equal(3, reloaded.Count());
    }

    [Fact]
    public void Migrations_CoverEveryOlderVersion_OnceEach()
    {
        Assert.Equal(Enumerable.Range(1, Current - 1), Subject.MigrationSources.Order());
    }

    /// <summary>A migration whose <c>Apply</c> throws.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public Task FailedMigration_WhenAStepThrows_LeavesEverythingUntouched_AndIsReadOnly()
        => AssertFailedMigrationAsync(
            new DelegateMigration(Current, _ => throw new InvalidOperationException("Conformance: this step always fails.")),
            string.Create(CultureInfo.InvariantCulture, $"the migration from version {Current} to {Current + 1}"));

    /// <summary>Migrated JSON that fails to deserialize.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public Task FailedMigration_WhenTheUpgradedDataDoesNotDeserialize_LeavesEverythingUntouched_AndIsReadOnly()
        => AssertFailedMigrationAsync(new DelegateMigration(Current, root => Subject.BreakDeserialization(root)), "reading the upgraded data");

    // ---- Unknown fields

    [Fact]
    public async Task UnknownDocumentFields_SurviveAWrite()
    {
        var value = JsonNode.Parse("""{ "Nested": [1, "two", { "Three": 3 }], "Flag": true }""")!;
        WriteFile(Edit(Subject.CreateFile(1), root => root[UnknownField] = value.DeepClone()));

        using (var store = Open())
        {
            await store.AddRecordAsync(2, Ct);
        }

        var written = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllBytesAsync(StoreFile, Ct)));
        Assert.True(JsonNode.DeepEquals(value, written[UnknownField]), "The unknown document field was lost or changed.");
    }

    [Fact]
    public async Task UnknownRecordFields_SurviveAWrite()
    {
        var value = JsonNode.Parse("""{ "Kept": ["a", 1] }""")!;
        var supported = true;
        WriteFile(Edit(Subject.CreateFile(1), root => supported = Subject.AddUnknownRecordField(root, UnknownField, value.DeepClone())));
        if (!supported)
        {
            Assert.Skip("The plugin's records don't keep unknown fields; its AddUnknownRecordField says why.");
        }

        using (var store = Open())
        {
            await store.AddRecordAsync(2, Ct);
        }

        var written = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllBytesAsync(StoreFile, Ct)));
        var found = FindProperties(written, UnknownField).Where(p => !ReferenceEquals(p.Parent, written)).ToList();
        var field = Assert.Single(found);
        Assert.True(JsonNode.DeepEquals(value, field.Value), "The unknown record field was changed.");
    }

    // ---- Snapshots

    [Fact]
    public async Task Snapshot_WritesTheDocument_KeepsTheNewest_AndLeavesOtherFilesAlone()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        using var store = Open(new StoreOpenOptions(Time: clock));
        await store.AddRecordAsync(1, Ct);
        Directory.CreateDirectory(SnapshotDirectory);
        var other = Path.Combine(SnapshotDirectory, "other-20200101-000000.json");
        var note = Path.Combine(SnapshotDirectory, "notes.txt");
        await File.WriteAllTextAsync(other, "{}", Ct);
        await File.WriteAllTextAsync(note, "keep", Ct);

        var paths = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            await store.AddRecordAsync(10 + i, Ct);
            paths.Add((await store.SnapshotAsync(SnapshotDirectory, 3, Ct))!);
            clock.Advance(TimeSpan.FromDays(1));
        }

        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        Assert.Equal(stem + "-20260929-031000.json", Path.GetFileName(paths[0]));
        Assert.False(File.Exists(paths[0]));
        Assert.All(paths.Skip(1), p => Assert.True(File.Exists(p)));
        Assert.Equal(5, store.CountFile(paths[^1]));
        Assert.Equal(4, store.CountFile(paths[^2]));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(note));
        Assert.Empty(Directory.GetFiles(SnapshotDirectory, "*.tmp"));
        Assert.Equal(new DateTime(2026, 10, 2, 3, 10, 0, DateTimeKind.Utc), store.LastSnapshotUtc);
    }

    [Fact]
    public async Task Snapshot_TwiceInOneSecond_KeepsBoth()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        using var store = Open(new StoreOpenOptions(Time: clock));
        await store.AddRecordAsync(1, Ct);

        var first = await store.SnapshotAsync(SnapshotDirectory, 7, Ct);
        await store.AddRecordAsync(2, Ct);
        var second = await store.SnapshotAsync(SnapshotDirectory, 7, Ct);

        Assert.NotEqual(first, second);
        Assert.Equal(1, store.CountFile(first!));
        Assert.Equal(2, store.CountFile(second!));
        Assert.Equal(second, SnapshotRotation.List(SnapshotDirectory, Path.GetFileNameWithoutExtension(Subject.StoreFileName), ".json")[0].Path);
    }

    [Fact]
    public async Task Snapshot_WithKeepZero_WritesNothing()
    {
        using var store = Open();
        await store.AddRecordAsync(1, Ct);

        Assert.Null(await store.SnapshotAsync(SnapshotDirectory, 0, Ct));
        Assert.False(Directory.Exists(SnapshotDirectory));
        Assert.Null(store.LastSnapshotUtc);
    }

    [Fact]
    public async Task Snapshots_AfterAnUnreadableFile_NeverRotateTheEarlierOnesOut()
    {
        // Three good snapshots, then the file and its .bak become unreadable: the old RecoveredEmpty until Bookmarks
        // 1.2, now the newest snapshot restored at load (both moved aside), and either way the earlier snapshots kept.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        var earlier = new List<string>();
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            await store.AddRecordAsync(1, Ct);
            for (var i = 0; i < 3; i++)
            {
                earlier.Add((await store.SnapshotAsync(SnapshotDirectory, 3, Ct))!);
                clock.Advance(TimeSpan.FromDays(1));
            }
        }

        await File.WriteAllTextAsync(StoreFile, "[]", Ct);
        await File.WriteAllTextAsync(BackupFile, "garbage", Ct);
        var logger = new ListLogger();
        var later = new List<string>();

        using (var store = Open(new StoreOpenOptions(Logger: logger, Time: clock)))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.Equal(1, store.Count());
            Assert.Equal(2, Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*").Length);
            later.Clear();
            for (var i = 0; i < 4; i++)
            {
                later.Add((await store.SnapshotAsync(SnapshotDirectory, 3, Ct))!);
                clock.Advance(TimeSpan.FromDays(1));
            }

            Assert.All(earlier, p => Assert.True(File.Exists(p), p + " was rotated out while the store started empty."));
            Assert.All(earlier, p => Assert.Equal(1, store.CountFile(p)));
            Assert.False(File.Exists(later[0]));
            Assert.All(later.Skip(1), p => Assert.True(File.Exists(p)));
            Assert.Equal(6, Directory.GetFiles(SnapshotDirectory).Length);
            Assert.Single(logger.At(LogLevel.Warning), e => e.Message.Contains("after its data was missing or unreadable; the earlier snapshots", StringComparison.Ordinal));
            await store.AddRecordAsync(2, Ct);
        }

        // After a restart, with a readable file again, the earlier snapshots are still kept; only
        // the later ones rotate.
        var later2 = later;
        using (var reopened = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.Ok, reopened.Status);
            Assert.NotNull(reopened.KeepSnapshotsBeforeUtc);
            var next = (await reopened.SnapshotAsync(SnapshotDirectory, 3, Ct))!;
            Assert.All(earlier, p => Assert.True(File.Exists(p), p + " was rotated out after a restart."));
            Assert.False(File.Exists(later2[1]));
            Assert.True(File.Exists(next));
            Assert.Equal(6, Directory.GetFiles(SnapshotDirectory).Length);
            Assert.Equal(earlier.Select(Path.GetFileName).Order(StringComparer.Ordinal), reopened.ListSnapshots(SnapshotDirectory).Where(s => s.Kept).Select(s => s.Name).Order(StringComparer.Ordinal));

            // Restoring one brings the data back and ends the protection: rotation is as usual again.
            var restored = await reopened.RestoreSnapshotAsync(SnapshotDirectory, Path.GetFileName(earlier[2]), Ct);
            Assert.Equal(Path.GetFileName(earlier[2]), restored.RestoredFrom);
            Assert.Equal(1, reopened.Count());
            Assert.Null(reopened.KeepSnapshotsBeforeUtc);
            Assert.False(File.Exists(StoreFile + ".keep-snapshots"));
            await reopened.SnapshotAsync(SnapshotDirectory, 3, Ct);
            Assert.Equal(3, Directory.GetFiles(SnapshotDirectory).Length);
        }

        using var third = Open(new StoreOpenOptions(Time: clock));
        Assert.Null(third.KeepSnapshotsBeforeUtc);
    }

    [Fact]
    public async Task StartingEmpty_KeepsTheTime_UntilANewIncidentMovesItForward()
    {
        // An empty file moved aside beside snapshots of an empty document only (RecoveredEmpty: nothing is lost) keeps
        // every snapshot from then on; a restart with nothing new keeps that time, and a new incident moves it forward.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            await store.SnapshotAsync(SnapshotDirectory, 3, Ct);
        }

        clock.Advance(TimeSpan.FromDays(1));
        WriteFile(" \n"u8.ToArray());
        DateTime? first;
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
            first = store.KeepSnapshotsBeforeUtc;
            Assert.Equal(clock.GetUtcNow().UtcDateTime, first);
        }

        // Restarted a day later, the file still missing (moved aside) and nothing new: the time stays.
        clock.Advance(TimeSpan.FromDays(1));
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.Ok, store.Status);
            Assert.Equal(first, store.KeepSnapshotsBeforeUtc);
        }

        // A new empty file is a new incident: everything before it is kept.
        clock.Advance(TimeSpan.FromDays(1));
        WriteFile([]);
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
            Assert.Equal(clock.GetUtcNow().UtcDateTime, store.KeepSnapshotsBeforeUtc);
        }
    }

    [Fact]
    public async Task PreOpSnapshots_AreTheirOwnClass_TheNewestFiveKept()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        using var store = Open(new StoreOpenOptions(Time: clock));
        await store.AddRecordAsync(1, Ct);
        var daily = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            daily.Add((await store.SnapshotAsync(SnapshotDirectory, 3, Ct))!);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var preOp = new List<string>();
        foreach (var operation in new[] { SnapshotOperations.Import, SnapshotOperations.Restore, SnapshotOperations.Remap, SnapshotOperations.ApplyPolicy, SnapshotOperations.UserDeleted, SnapshotOperations.Purge, SnapshotOperations.Import })
        {
            preOp.Add((await store.PreOpSnapshotAsync(SnapshotDirectory, operation, Ct))!);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var storeName = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        Assert.All(preOp, p => Assert.Equal(Path.Combine(SnapshotDirectory, SnapshotOperations.Folder), Path.GetDirectoryName(p)));
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(storeName) + @"-apply-policy-\d{8}-\d{6}\.json$", Path.GetFileName(preOp[3]));
        Assert.All(daily, p => Assert.True(File.Exists(p), "A pre-operation snapshot rotated a daily one out."));
        Assert.Equal(preOp.Skip(2).Select(Path.GetFileName).Order(StringComparer.Ordinal), Directory.GetFiles(Path.Combine(SnapshotDirectory, SnapshotOperations.Folder)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(preOp.Skip(2), p => Assert.Equal(1, store.CountFile(p)));

        var listed = store.ListSnapshots(SnapshotDirectory);
        Assert.Equal(8, listed.Count);
        Assert.Equal(3, listed.Count(s => s.Kind == SnapshotKind.Daily && s.Operation is null));
        Assert.Equal(5, listed.Count(s => s.Kind == SnapshotKind.PreOp));
        Assert.Equal(SnapshotOperations.Import, listed[0].Operation);
        Assert.All(listed, s => Assert.Equal(Current, s.SchemaVersion));
        Assert.All(listed, s => Assert.False(s.Kept));
        Assert.Equal(listed.OrderByDescending(s => s.Utc).Select(s => s.Name), listed.Select(s => s.Name));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PreOpSnapshotAsync(SnapshotDirectory, "anything", Ct));
    }

    [Fact]
    public async Task RestoreSnapshot_ReplacesTheData_AfterASafetySnapshotOfTheStateBefore()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        using var store = Open(new StoreOpenOptions(Time: clock));
        await store.AddRecordAsync(1, Ct);
        await store.AddRecordAsync(2, Ct);
        var snapshot = (await store.SnapshotAsync(SnapshotDirectory, 7, Ct))!;
        clock.Advance(TimeSpan.FromHours(1));
        for (var i = 3; i <= 5; i++)
        {
            await store.AddRecordAsync(i, Ct);
        }

        var result = await store.RestoreSnapshotAsync(SnapshotDirectory, Path.GetFileName(snapshot).ToUpperInvariant(), Ct);

        Assert.Equal(2, store.Count());
        Assert.Equal(Path.GetFileName(snapshot), result.RestoredFrom);
        Assert.Equal(Current, result.SchemaVersion);
        Assert.Null(result.MigratedFrom);
        var safety = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, result.SafetySnapshot!);
        Assert.Equal(5, store.CountFile(safety));
        using var reopened = Open();
        Assert.Equal(2, reopened.Count());

        // The safety snapshot can be restored in turn (undo).
        await reopened.RestoreSnapshotAsync(SnapshotDirectory, result.SafetySnapshot!, Ct);
        Assert.Equal(5, reopened.Count());
    }

    [Fact]
    public async Task RestoreSnapshot_RefusesUnknownNewerAndUnreadableSnapshots_AndChangesNothing()
    {
        await WriteThroughStoreAsync(2);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);
        var storeName = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        Directory.CreateDirectory(SnapshotDirectory);
        await File.WriteAllBytesAsync(Path.Combine(SnapshotDirectory, storeName + "-20200101-000000.json"), Edit(Subject.CreateFile(1), r => r[JsonStoreFormat.VersionProperty] = Current + 1), Ct);
        await File.WriteAllTextAsync(Path.Combine(SnapshotDirectory, storeName + "-20200102-000000.json"), "{ not json", Ct);
        await File.WriteAllBytesAsync(Path.Combine(DataDirectory, "outside.json"), Subject.CreateFile(9), Ct);

        using var store = Open();
        var missing = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, "nothing.json", Ct));
        Assert.Equal(PluginErrorKind.NotFound, missing.Kind);
        var traversal = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, "../outside.json", Ct));
        Assert.Equal(PluginErrorKind.NotFound, traversal.Kind);
        var newer = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, storeName + "-20200101-000000.json", Ct));
        Assert.Equal(PluginErrorKind.Conflict, newer.Kind);
        Assert.Contains("newer plugin version", newer.Message, StringComparison.Ordinal);
        var corrupt = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, storeName + "-20200102-000000.json", Ct));
        Assert.Equal(PluginErrorKind.Conflict, corrupt.Kind);

        Assert.Equal(2, store.Count());
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.False(Directory.Exists(Path.Combine(SnapshotDirectory, SnapshotOperations.Folder)), "A refused restore took a safety snapshot.");
        Assert.Null(store.ListSnapshots(SnapshotDirectory).Single(s => s.Name.EndsWith("20200102-000000.json", StringComparison.Ordinal)).SchemaVersion);
    }

    [Fact]
    public async Task RestoreSnapshot_WhileReadOnly_Is409()
    {
        WriteFile(Edit(Subject.CreateFile(1), r => r[JsonStoreFormat.VersionProperty] = Current + 1));
        var storeName = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        Directory.CreateDirectory(SnapshotDirectory);
        await File.WriteAllBytesAsync(Path.Combine(SnapshotDirectory, storeName + "-20200101-000000.json"), Subject.CreateFile(3), Ct);

        using var store = Open();
        Assert.True(store.IsReadOnly);
        var ex = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, storeName + "-20200101-000000.json", Ct));
        Assert.Equal(PluginErrorKind.Conflict, ex.Kind);
        Assert.Null(await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Restore, Ct));
    }

    [Fact]
    public async Task RestoreSnapshot_OfAnOlderVersion_IsMigrated()
    {
        var older = Subject.Fixtures.Where(f => f.Version < Current).ToList();
        if (older.Count == 0)
        {
            Assert.Skip("The store has no fixture of an older schema version.");
        }

        var storeName = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        foreach (var fixture in older)
        {
            ResetDataDirectory();
            Directory.CreateDirectory(SnapshotDirectory);
            var name = storeName + "-20200101-000000.json";
            await File.WriteAllBytesAsync(Path.Combine(SnapshotDirectory, name), fixture.Content, Ct);

            using var store = Open();
            Assert.Equal(fixture.Version, store.ListSnapshots(SnapshotDirectory).Single().SchemaVersion);
            var result = await store.RestoreSnapshotAsync(SnapshotDirectory, name, Ct);

            Assert.Equal(fixture.Records, store.Count());
            Assert.Equal(fixture.Version, result.MigratedFrom);
            Assert.Equal(Current, ReadVersion(StoreFile));
        }
    }

    [Fact]
    public async Task RestoreSnapshot_OfTheOldestPreOperationSnapshot_NeverRotatesItOut()
    {
        // The restore's own safety snapshot rotates the pre-operation class (the newest 5). The snapshot being
        // restored must survive that, above all when the write then fails, and stay listed after a successful restore.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        using var store = Open(new StoreOpenOptions(Time: clock));
        var preOp = new List<string>();
        for (var i = 1; i <= SnapshotOperations.ToKeep; i++)
        {
            await store.AddRecordAsync(i, Ct);
            preOp.Add(Path.GetFileName((await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Import, Ct))!));
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var oldest = preOp[0];
        var preOpDirectory = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder);
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(StoreFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, oldest, Ct));
                Assert.Equal(PluginErrorKind.Unavailable, failed.Kind);
            }

            Assert.Equal(SnapshotOperations.ToKeep, store.Count());
            Assert.True(File.Exists(Path.Combine(preOpDirectory, oldest)), "A failed restore rotated its own snapshot out.");
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var result = await store.RestoreSnapshotAsync(SnapshotDirectory, oldest, Ct);

        Assert.Equal(1, store.Count());
        var listed = store.ListSnapshots(SnapshotDirectory);
        Assert.Contains(listed, s => s.Name == oldest);
        Assert.Contains(listed, s => s.Name == result.SafetySnapshot);
        Assert.Equal(1, store.CountFile(Path.Combine(preOpDirectory, oldest)));

        // The next pre-operation snapshot rotates as usual again: the newest 5.
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct);
        Assert.Equal(SnapshotOperations.ToKeep, store.ListSnapshots(SnapshotDirectory).Count(s => s.Kind == SnapshotKind.PreOp));
    }

    [Fact]
    public async Task RestoringASnapshotTakenAfterTheDataWasLost_KeepsTheEarlierOnesProtected()
    {
        // Only a snapshot taken before the store started empty (since Bookmarks 1.2: was restored at load from an
        // unreadable file) brings the data back. Restoring a later one (a daily one, or a pre-operation one) must leave the
        // last good copies protected from rotation.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));
        var earlier = new List<string>();
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            await store.AddRecordAsync(1, Ct);
            await store.AddRecordAsync(2, Ct);
            for (var i = 0; i < 3; i++)
            {
                earlier.Add(Path.GetFileName((await store.SnapshotAsync(SnapshotDirectory, 3, Ct))!));
                clock.Advance(TimeSpan.FromDays(1));
            }
        }

        await File.WriteAllTextAsync(StoreFile, "[]", Ct);
        await File.WriteAllTextAsync(BackupFile, "garbage", Ct);
        var marker = StoreFile + ".keep-snapshots";
        var logger = new ListLogger();
        using var reopened = Open(new StoreOpenOptions(Logger: logger, Time: clock));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, reopened.Status);
        var keepBefore = reopened.KeepSnapshotsBeforeUtc;
        Assert.NotNull(keepBefore);
        clock.Advance(TimeSpan.FromHours(1));
        var emptyDaily = Path.GetFileName((await reopened.SnapshotAsync(SnapshotDirectory, 3, Ct))!);
        clock.Advance(TimeSpan.FromHours(1));
        var emptyPreOp = Path.GetFileName((await reopened.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct))!);
        clock.Advance(TimeSpan.FromHours(1));

        foreach (var later in new[] { emptyDaily, emptyPreOp })
        {
            var result = await reopened.RestoreSnapshotAsync(SnapshotDirectory, later, Ct);

            Assert.Equal(keepBefore, result.KeepSnapshotsBeforeUtc);
            Assert.Equal(keepBefore, reopened.KeepSnapshotsBeforeUtc);
            Assert.True(File.Exists(marker), "Restoring " + later + " released the kept snapshots.");
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(2, logger.At(LogLevel.Warning).Count(e => e.Message.Contains("still kept", StringComparison.Ordinal)));

        // Daily snapshots go on rotating only among the later ones.
        for (var i = 0; i < 4; i++)
        {
            await reopened.SnapshotAsync(SnapshotDirectory, 3, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.All(earlier, e => Assert.Contains(reopened.ListSnapshots(SnapshotDirectory), s => s.Name == e && s.Kept));

        // One of the earlier ones brings the data back and ends the protection.
        var back = await reopened.RestoreSnapshotAsync(SnapshotDirectory, earlier[2], Ct);
        Assert.Null(back.KeepSnapshotsBeforeUtc);
        Assert.Null(reopened.KeepSnapshotsBeforeUtc);
        Assert.Equal(2, reopened.Count());
        Assert.False(File.Exists(marker));
    }

    // ---- A missing or empty file beside snapshots of its data (for the JSON store)

    [Fact]
    public async Task MissingFile_BesideSnapshotsOfItsData_IsRestoredFromTheNewest_AndNoSnapshotRotatesOut()
    {
        // The file and its .bak gone while the snapshots remain: deleted by hand, a manual restore half done, or a backup
        // of snapshots/ alone put back (the advice while the server runs). Starting empty, as the store did, let
        // the daily rotation delete every snapshot holding the data within keep days.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));
        var logger = new ListLogger();
        var newest = Path.GetFileName(earlier[^1].Path);

        using (var store = Open(new StoreOpenOptions(Logger: logger, Time: clock)))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.False(store.IsReadOnly);
            Assert.Equal(earlier[^1].Records, store.Count());
            Assert.Equal(Current, store.LoadedVersion);
            Assert.Null(store.MigratedFrom);
            Assert.StartsWith("The data file was missing and there was no backup, so the data was restored from the snapshot " + newest + " taken 2026-10-03 03:10 UTC;", store.StatusMessage, StringComparison.Ordinal);

            // A Warning, as the .bak's recovery is, with the snapshot's time said to be UTC.
            Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.StartsWith("Restored ", StringComparison.Ordinal) && e.Message.Contains(newest + " taken 2026-10-03 03:10:00 UTC", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.At(LogLevel.Information), e => e.Message.StartsWith("Restored ", StringComparison.Ordinal));
            Assert.Empty(logger.At(LogLevel.Error));

            // Saved at once, with no .bak (there was nothing to keep), and every earlier snapshot kept from now on.
            Assert.Equal(earlier[^1].Records, store.CountFile(StoreFile));
            Assert.False(File.Exists(BackupFile));
            Assert.True(File.Exists(StoreFile + ".keep-snapshots"));
            Assert.Equal(clock.GetUtcNow().UtcDateTime, store.KeepSnapshotsBeforeUtc);

            for (var i = 0; i <= KeepInTests; i++)
            {
                await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
                clock.Advance(TimeSpan.FromDays(1));
            }

            AssertKept(store, earlier);
            await store.AddRecordAsync(100, Ct);
        }

        // After a restart the file is there (Ok, no banner); the earlier snapshots are still kept, and listed as kept.
        using (var reopened = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.Ok, reopened.Status);
            Assert.Null(reopened.StatusMessage);
            Assert.Equal(earlier[^1].Records + 1, reopened.Count());
            await reopened.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            AssertKept(reopened, earlier);
            Assert.Equal(
                earlier.Select(s => Path.GetFileName(s.Path)).Order(StringComparer.Ordinal),
                reopened.ListSnapshots(SnapshotDirectory).Where(s => s.Kept).Select(s => s.Name).Order(StringComparer.Ordinal));

            // An admin's restore of one of them ends the protection.
            await reopened.RestoreSnapshotAsync(SnapshotDirectory, newest, Ct);
            Assert.Null(reopened.KeepSnapshotsBeforeUtc);
            Assert.False(File.Exists(StoreFile + ".keep-snapshots"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t \n")]
    [InlineData("﻿\r\n")]
    public async Task EmptyFile_BesideSnapshotsOfItsData_IsMovedAside_AndRestoredFromTheNewest(string content)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        var bytes = Encoding.UTF8.GetBytes(content);
        WriteFile(bytes);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        var moved = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(moved, Ct));
        Assert.StartsWith("The data file was empty and was moved to " + Path.GetFileName(moved) + " and there was no backup, so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path), store.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(earlier[^1].Records, store.CountFile(StoreFile));
        Assert.False(File.Exists(BackupFile), "The empty file became the .bak.");

        for (var i = 0; i <= KeepInTests; i++)
        {
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        AssertKept(store, earlier);
    }

    [Fact]
    public async Task EmptyFile_WithABackup_UsesTheBackup()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        await TakeDailySnapshotsAsync(clock);
        var backup = await File.ReadAllBytesAsync(BackupFile, Ct);
        WriteFile([]);

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
        Assert.Equal(store.CountFile(BackupFile), store.Count());
        Assert.Equal(backup, await File.ReadAllBytesAsync(BackupFile, Ct));
        Assert.StartsWith("The data file was empty and was moved to ", store.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyDocument_BesideSnapshotsOfData_IsTheData_AndTheSnapshotsRotateAsUsual()
    {
        // A file that reads is the data, even with no records: a store whose records were all deleted looks exactly like
        // this. It is never replaced by a snapshot, and rotation keeps the newest keep, as for any store.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        WriteFile(Subject.CreateFile(0));
        File.Delete(BackupFile);

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Equal(0, store.Count());
        Assert.Null(store.StatusMessage);
        Assert.Null(store.KeepSnapshotsBeforeUtc);
        Assert.False(File.Exists(StoreFile + ".keep-snapshots"));
        for (var i = 0; i <= KeepInTests; i++)
        {
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.All(earlier, s => Assert.False(File.Exists(s.Path)));
    }

    [Fact]
    public async Task MissingFile_BesideSnapshotsOfAnEmptyDocumentOnly_StartsEmpty_AsAFreshInstall()
    {
        // A fresh install takes daily snapshots before its first write; they hold nothing, so nothing is restored.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            for (var i = 0; i < 2; i++)
            {
                await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
                await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct);
                clock.Advance(TimeSpan.FromDays(1));
            }
        }

        using var reopened = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.Ok, reopened.Status);
        Assert.Null(reopened.StatusMessage);
        Assert.Equal(0, reopened.Count());
        Assert.Null(reopened.KeepSnapshotsBeforeUtc);
        Assert.Equal(["snapshots"], Directory.GetFileSystemEntries(DataDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task MissingFile_PassesOverSnapshotsThatCantBeUsed_OrHoldNoData_ForTheNewestThatHoldsData()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var empty = Path.Combine(SnapshotDirectory, stem + "-20261101-031000.json");
        var newer = Path.Combine(SnapshotDirectory, stem + "-20261102-031000.json");
        var corrupt = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, stem + "-import-20261103-031000.json");
        await File.WriteAllBytesAsync(empty, Subject.CreateFile(0), Ct);
        await File.WriteAllBytesAsync(newer, Edit(Subject.CreateFile(9), r => r[JsonStoreFormat.VersionProperty] = Current + 1), Ct);
        await File.WriteAllTextAsync(EnsureDirectory(corrupt), "{ \"Version\": 1, not json", Ct);
        clock.Set(new DateTimeOffset(2026, 11, 4, 3, 10, 0, TimeSpan.Zero));

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        Assert.Contains(Path.GetFileName(earlier[^1].Path), store.StatusMessage, StringComparison.Ordinal);
        Assert.EndsWith(
            string.Create(CultureInfo.InvariantCulture, $" Passed over first: {Path.GetFileName(corrupt)} (can't be read), {Path.GetFileName(newer)} (schema {Current + 1}, from a newer plugin version), {Path.GetFileName(empty)} (holds no data)."),
            store.StatusMessage,
            StringComparison.Ordinal);
        Assert.True(File.Exists(newer) && File.Exists(corrupt) && File.Exists(empty));
    }

    [Fact]
    public async Task MissingFile_BesideSnapshotsNoneOfWhichCanBeUsed_IsReadOnly_AndTouchesNothing()
    {
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        Directory.CreateDirectory(Path.Combine(SnapshotDirectory, SnapshotOperations.Folder));
        var newer = Path.Combine(SnapshotDirectory, stem + "-20261001-031000.json");
        var corrupt = Path.Combine(SnapshotDirectory, stem + "-20261002-031000.json");
        var empty = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, stem + "-restore-20261003-031000.json");
        await File.WriteAllBytesAsync(newer, Edit(Subject.CreateFile(4), r => r[JsonStoreFormat.VersionProperty] = Current + 1), Ct);
        await File.WriteAllTextAsync(corrupt, "{ \"Version\": 1, not json", Ct);
        await File.WriteAllBytesAsync(empty, Subject.CreateFile(0), Ct);
        var before = SnapshotFiles();
        var logger = new ListLogger();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 3, 10, 0, TimeSpan.Zero));

        using (var store = Open(new StoreOpenOptions(Logger: logger, Time: clock)))
        {
            Assert.Equal(StoreStatus.ReadOnlyDataMissing, store.Status);
            Assert.True(store.IsReadOnly);
            Assert.Equal(0, store.Count());
            Assert.Null(store.LoadedVersion);
            Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"none of them could be restored: {Path.GetFileName(corrupt)} (can't be read), {Path.GetFileName(newer)} (schema {Current + 1}, from a newer plugin version). Nothing can be changed"), store.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("To go on, restore a snapshot that can be read (put one back in " + SnapshotDirectory + " first if you have one), or start empty, on the plugin's settings page; or stop the server, copy the snapshot you want over " + Subject.StoreFileName, store.StatusMessage, StringComparison.Ordinal);
            Assert.True(store.CanRecover);
            Assert.DoesNotContain(Path.GetFileName(empty), store.StatusMessage, StringComparison.Ordinal);
            Assert.Contains(logger.At(LogLevel.Error), e => e.Message.Contains(Path.GetFileName(newer), StringComparison.Ordinal));

            var write = await Assert.ThrowsAsync<PluginException>(() => store.AddRecordAsync(1, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, write.Kind);
            Assert.Equal(StoreMessages.DataMissing, write.Message);
            Assert.Null(await store.SnapshotAsync(SnapshotDirectory, 1, Ct));
            Assert.Null(await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, Assert.Throws<PluginException>(() => store.ExportData()).Kind);
            var restore = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, Path.GetFileName(empty), Ct));
            Assert.Equal(PluginErrorKind.Conflict, restore.Kind);
        }

        Assert.Equal(before, SnapshotFiles());
        Assert.False(File.Exists(StoreFile));
        Assert.False(File.Exists(StoreFile + ".keep-snapshots"));

        // The way out: the version that wrote the newer snapshot (here: a readable file put back), then a restart.
        WriteFile(Subject.CreateFile(2));
        using var fixedStore = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.Ok, fixedStore.Status);
        Assert.Equal(2, fixedStore.Count());
    }

    [Fact]
    public async Task MissingFile_AfterARestore_TriesTheSnapshotsTakenSince_ThenTheRestoredOne_BeforeTheStateItReplaced()
    {
        // A restore's safety snapshot (pre-op "restore") is the newest file but holds the state an admin replaced.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        string restored;
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            restored = Path.GetFileName(earlier[0].Path);
            var result = await store.RestoreSnapshotAsync(SnapshotDirectory, restored, Ct);
            Assert.Equal(earlier[^1].Records, store.CountFile(Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, result.SafetySnapshot!)));
        }

        Assert.True(File.Exists(StoreFile + ".last-restore"));
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));
        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.Equal(earlier[0].Records, store.Count());
            Assert.Contains(restored, store.StatusMessage, StringComparison.Ordinal);

            // A daily snapshot taken since then comes first.
            await store.AddRecordAsync(100, Ct);
            clock.Advance(TimeSpan.FromHours(1));
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
        }

        File.Delete(StoreFile);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));
        using var again = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, again.Status);
        Assert.Equal(earlier[0].Records + 1, again.Count());
    }

    [Fact]
    public async Task MissingFile_BesideAnOlderSnapshot_UpgradesIt()
    {
        var older = Subject.Fixtures.Where(f => f.Version < Current).ToList();
        if (older.Count == 0)
        {
            Assert.Skip("The store has no fixture of an older schema version.");
        }

        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        foreach (var fixture in older)
        {
            ResetDataDirectory();
            Directory.CreateDirectory(SnapshotDirectory);
            await File.WriteAllBytesAsync(Path.Combine(SnapshotDirectory, stem + "-20261001-031000.json"), fixture.Content, Ct);

            using var store = Open();

            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.Equal(fixture.Records, store.Count());
            Assert.Equal(fixture.Version, store.LoadedVersion);
            Assert.Equal(fixture.Version, store.MigratedFrom);
            Assert.Equal(Current, ReadVersion(StoreFile));
            Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(Path.Combine(SnapshotDirectory, stem + "-20261001-031000.json"), Ct));
        }
    }

    [Fact]
    public async Task MissingFile_BesideASnapshotThatCantBeReadJustNow_IsUnavailable_AndTriedAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Holding a file so that no other handle can read it is Windows' share mode.");
        }

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        var logger = new ListLogger();
        var name = Path.GetFileName(earlier[^1].Path);

        using var store = Open(new StoreOpenOptions(Logger: logger, Time: clock));
        using (new FileStream(earlier[^1].Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Never a reason to fall back to an older snapshot, or to nothing. Every use is a 503 naming the snapshot's
            // file (never a path, never "the data file couldn't be read"), and only the first one is logged above Debug, as
            // one Error naming the snapshot rather than the missing file.
            for (var i = 0; i < 5; i++)
            {
                var ex = Assert.Throws<PluginException>(() => store.Count());
                Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
                Assert.Equal(StoreMessages.SnapshotReadFailed(name), ex.Message);
                Assert.DoesNotContain(SnapshotDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.False(File.Exists(StoreFile));
        }

        var logged = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Contains(earlier[^1].Path, logged.Message, StringComparison.Ordinal);
        Assert.Contains("couldn't be read just now", logged.Message, StringComparison.Ordinal);
        Assert.Equal(4, logger.At(LogLevel.Debug).Count(e => e.Message.StartsWith("Still could not read the snapshot ", StringComparison.Ordinal)));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        Assert.Single(logger.At(LogLevel.Information), e => e.Message.EndsWith("could be read again", StringComparison.Ordinal));
    }

    // ---- The restore at load: empty files, passed-over snapshots and snapshots dated ahead

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    public async Task MissingFile_WithAnEmptyBackup_MovesTheBackupAside_AndIsRestoredFromTheNewestSnapshot(string content)
    {
        // An empty .bak holds no data, like an empty file: it is moved aside and counts as none, so the snapshots are
        // tried rather than starting empty.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(StoreFile);
        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(BackupFile, bytes, Ct);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        var moved = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(moved, Ct));
        Assert.False(File.Exists(BackupFile));
        Assert.StartsWith(
            "The data file was missing, and its backup was empty and was moved to " + Path.GetFileName(moved) + ", so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path),
            store.StatusMessage,
            StringComparison.Ordinal);
        Assert.Equal(earlier[^1].Records, store.CountFile(StoreFile));
    }

    [Fact]
    public async Task EmptyFile_AndAnEmptyBackup_AreBothMovedAside_AndRestoredFromTheNewestSnapshot()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        WriteFile([]);
        await File.WriteAllTextAsync(BackupFile, "\n\n", Ct);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        var moved = Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, moved.Count);
        Assert.Empty(await File.ReadAllBytesAsync(moved[0], Ct));
        Assert.Equal("\n\n", await File.ReadAllTextAsync(moved[1], Ct));
        Assert.StartsWith(
            "The data file was empty and was moved to " + Path.GetFileName(moved[0]) + ", and its backup was empty and was moved to " + Path.GetFileName(moved[1]) + ", so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path),
            store.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_WithAnEmptyBackup_AndNoSnapshots_StartsEmpty_AndSaysWhatWasMoved()
    {
        Directory.CreateDirectory(DataDirectory);
        await File.WriteAllBytesAsync(BackupFile, [], Ct);

        using var store = Open();

        Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
        Assert.Equal(0, store.Count());
        var moved = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal("The data file was missing, and its backup was empty and was moved to " + Path.GetFileName(moved) + ", so the data starts empty.", store.StatusMessage);
    }

    [Fact]
    public async Task PreOpSnapshots_TakenBeforeARestoreAtLoad_AreKept_AndOnlyTheLaterOnesRotate()
    {
        // A pre-operation snapshot passed over at load (here a newer version's, the only copy of its data) must outlive the
        // pre-operation snapshots taken afterwards, as the daily ones do: "every earlier snapshot is kept".
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var newer = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, stem + "-import-20261004-020000.json");
        var newerBytes = Edit(Subject.CreateFile(9), r => r[JsonStoreFormat.VersionProperty] = Current + 1);
        await File.WriteAllBytesAsync(EnsureDirectory(newer), newerBytes, Ct);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));
        var logger = new ListLogger();

        using var store = Open(new StoreOpenOptions(Logger: logger, Time: clock));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Contains(Path.GetFileName(newer) + " (schema", store.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("The earlier snapshots, daily and pre-operation, are kept until you restore one of them (or delete " + Subject.StoreFileName + ".keep-snapshots).", store.StatusMessage, StringComparison.Ordinal);

        for (var i = 0; i <= SnapshotOperations.ToKeep; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct);
        }

        Assert.Equal(newerBytes, await File.ReadAllBytesAsync(newer, Ct));
        var listed = store.ListSnapshots(SnapshotDirectory);
        Assert.True(listed.Single(s => s.Name == Path.GetFileName(newer)).Kept);
        Assert.Equal(SnapshotOperations.ToKeep, listed.Count(s => s.Kind == SnapshotKind.PreOp && !s.Kept));
        AssertKept(store, earlier);
        Assert.Single(logger.At(LogLevel.Warning), e => e.Message.Contains("are kept until one of them is restored or " + Subject.StoreFileName + ".keep-snapshots is deleted", StringComparison.Ordinal) && e.Message.Contains("started empty", StringComparison.Ordinal));

        // An admin's restore of a kept one ends the protection: the pre-operation snapshots rotate as before.
        await store.RestoreSnapshotAsync(SnapshotDirectory, Path.GetFileName(earlier[^1].Path), Ct);
        Assert.Null(store.KeepSnapshotsBeforeUtc);
        await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct);
        Assert.False(File.Exists(newer));
        Assert.Equal(SnapshotOperations.ToKeep, store.ListSnapshots(SnapshotDirectory).Count(s => s.Kind == SnapshotKind.PreOp));
    }

    [Fact]
    public async Task MissingFile_BesideEmptySnapshotFilesOnly_StartsEmpty_AsAFreshInstall()
    {
        // A snapshot file with no bytes or white space only holds no data: never a reason for ReadOnlyDataMissing.
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var daily = Path.Combine(SnapshotDirectory, stem + "-20261001-031000.json");
        var preOp = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, stem + "-other-20261002-031000.json");
        await File.WriteAllBytesAsync(EnsureDirectory(daily), [], Ct);
        await File.WriteAllTextAsync(EnsureDirectory(preOp), " \r\n", Ct);
        var before = SnapshotFiles();

        using var store = Open();

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Null(store.StatusMessage);
        Assert.Equal(0, store.Count());
        Assert.Equal(before, SnapshotFiles());
    }

    [Fact]
    public async Task MissingFile_PassesOverAnEmptySnapshotFile_AsHoldingNoData()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var blank = Path.Combine(SnapshotDirectory, stem + "-20261004-031000.json");
        await File.WriteAllBytesAsync(blank, [], Ct);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        Assert.EndsWith(" Passed over first: " + Path.GetFileName(blank) + " (holds no data).", store.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_BesideASnapshotDatedLaterThanNow_TriesItLast_AndSaysSo()
    {
        // A snapshot's name is its time; one dated a year ahead was taken while the clock was wrong, so it says nothing about
        // how new its data is. It is tried after every other one, and named.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var future = Path.Combine(SnapshotDirectory, stem + "-20271001-031000.json");
        await File.WriteAllBytesAsync(future, Subject.CreateFile(1), Ct);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));
        var note = " Dated later than now, so tried last: " + Path.GetFileName(future) + ". If the clock is wrong now rather than when they were taken, they may hold newer data than the snapshot restored: they are kept too, so restore the one you want on the plugin's settings page.";

        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.Equal(earlier[^1].Records, store.Count());
            Assert.StartsWith("The data file was missing and there was no backup, so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path), store.StatusMessage, StringComparison.Ordinal);
            Assert.EndsWith(note, store.StatusMessage, StringComparison.Ordinal);
            Assert.True(File.Exists(future));
        }

        // With no other snapshot holding data, it is restored all the same, and the banner says so.
        ResetDataDirectory();
        await File.WriteAllBytesAsync(EnsureDirectory(future), Subject.CreateFile(1), Ct);
        using var alone = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, alone.Status);
        Assert.Equal(1, alone.Count());
        Assert.Contains("restored from the snapshot " + Path.GetFileName(future) + " taken 2027-10-01 03:10 UTC", alone.StatusMessage, StringComparison.Ordinal);
        Assert.EndsWith(note, alone.StatusMessage, StringComparison.Ordinal);
    }

    // ---- The restore: the clock moved back, a load tried again, an
    // unreadable .bak or file, and an admin's way out of the read-only states

    [Fact]
    public async Task MissingFile_WithTheClockMovedBackIntoTheSnapshots_KeepsEverySnapshot_AndNamesTheOnesDatedLater()
    {
        // V3: the clock more than a day back into the snapshots' times, and the file missing. The snapshot dated after now is
        // tried last, so an older one is restored, and the banner names it; every snapshot is kept, the one dated later too,
        // while the clock is wrong and once it is right again, so the newest data is never rotated out.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(StoreFile);
        File.Delete(BackupFile);
        clock.Set(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var newest = Path.GetFileName(earlier[^1].Path);

        using (var store = Open(new StoreOpenOptions(Time: clock)))
        {
            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.Equal(earlier[1].Records, store.Count());
            Assert.Contains("restored from the snapshot " + Path.GetFileName(earlier[1].Path) + " taken 2026-10-02 03:10 UTC", store.StatusMessage, StringComparison.Ordinal);
            Assert.EndsWith(" Dated later than now, so tried last: " + newest + ". If the clock is wrong now rather than when they were taken, they may hold newer data than the snapshot restored: they are kept too, so restore the one you want on the plugin's settings page.", store.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(new DateTime(2026, 10, 3, 3, 10, 1, DateTimeKind.Utc), store.KeepSnapshotsBeforeUtc);
            Assert.All(store.ListSnapshots(SnapshotDirectory), s => Assert.True(s.Kept, s.Name));

            for (var i = 0; i <= KeepInTests; i++)
            {
                await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
                clock.Advance(TimeSpan.FromDays(1));
            }

            AssertKept(store, earlier);
        }

        clock.Set(new DateTimeOffset(2026, 10, 10, 3, 10, 0, TimeSpan.Zero));
        using var corrected = Open(new StoreOpenOptions(Time: clock));
        for (var i = 0; i <= KeepInTests; i++)
        {
            await corrected.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        AssertKept(corrected, earlier);

        // Restoring the one dated later brings its data back and ends the protection, as any snapshot from before does.
        await corrected.RestoreSnapshotAsync(SnapshotDirectory, newest, Ct);
        Assert.Equal(earlier[^1].Records, corrected.Count());
        Assert.Null(corrected.KeepSnapshotsBeforeUtc);
    }

    [Fact]
    public async Task Rotation_NeitherDeletesNorCountsASnapshotDatedInTheFuture()
    {
        // V3: a snapshot dated more than a day after now was taken while the clock was ahead, or the clock is behind now;
        // its name says nothing about its age, so rotating by name could delete the newest data, or keep one real snapshot
        // fewer. Daily and pre-operation alike.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var daily = Path.Combine(SnapshotDirectory, stem + "-20261101-031000.json");
        var preOp = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, stem + "-import-20261101-031000.json");
        await File.WriteAllBytesAsync(EnsureDirectory(daily), Subject.CreateFile(5), Ct);
        await File.WriteAllBytesAsync(EnsureDirectory(preOp), Subject.CreateFile(5), Ct);
        WriteFile(Subject.CreateFile(1));
        using var store = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.Ok, store.Status);

        for (var i = 0; i <= KeepInTests; i++)
        {
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromHours(1));
        }

        for (var i = 0; i <= SnapshotOperations.ToKeep; i++)
        {
            await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.True(File.Exists(daily) && File.Exists(preOp));
        var listed = store.ListSnapshots(SnapshotDirectory);
        Assert.Equal(KeepInTests + 1, listed.Count(s => s.Kind == SnapshotKind.Daily));
        Assert.Equal(SnapshotOperations.ToKeep + 1, listed.Count(s => s.Kind == SnapshotKind.PreOp));
    }

    [Fact]
    public async Task EmptyFile_ThenASnapshotInUse_TheNextAttemptStillSaysTheFileWasEmpty()
    {
        // V1: the first attempt moved the empty file aside and then found the snapshot in use (503); the next one finds the
        // file missing, and must still say it was empty, and where it went.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that no other handle can read it is Windows' share mode.");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        WriteFile([]);
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));
        using (new FileStream(earlier[^1].Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(PluginErrorKind.Unavailable, Assert.Throws<PluginException>(() => store.Count()).Kind);
        }

        var moved = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.StartsWith("The data file was empty and was moved to " + Path.GetFileName(moved) + " and there was no backup, so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path), store.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyFile_ThenASnapshotInUse_ThatHoldsNoData_TheNextAttemptStartsEmpty_WithTheBanner()
    {
        // V4: as above, but the snapshot holds no data: RecoveredEmpty with the banner naming the moved file, not Ok as a
        // fresh install.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that no other handle can read it is Windows' share mode.");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        string snapshot;
        using (var fresh = Open(new StoreOpenOptions(Time: clock)))
        {
            snapshot = (await fresh.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct))!;
        }

        WriteFile(" \n"u8.ToArray());
        clock.Advance(TimeSpan.FromHours(1));
        using var store = Open(new StoreOpenOptions(Time: clock));
        using (new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(PluginErrorKind.Unavailable, Assert.Throws<PluginException>(() => store.Count()).Kind);
        }

        var moved = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
        Assert.Equal("The data file was empty and was moved to " + Path.GetFileName(moved) + " and there was no backup, so the data starts empty.", store.StatusMessage);
        Assert.True(File.Exists(StoreFile + ".keep-snapshots"));
    }

    [Fact]
    public async Task MissingFile_WithAnEmptyBackup_ThenASnapshotInUse_TheNextAttemptStillNamesTheBackup()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that no other handle can read it is Windows' share mode.");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(StoreFile);
        await File.WriteAllBytesAsync(BackupFile, [], Ct);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));
        using (new FileStream(earlier[^1].Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(PluginErrorKind.Unavailable, Assert.Throws<PluginException>(() => store.Count()).Kind);
        }

        var moved = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.StartsWith("The data file was missing, and its backup was empty and was moved to " + Path.GetFileName(moved) + ", so the data was restored from the snapshot ", store.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_WithAnUnreadableBackup_MovesItAside_AndIsRestoredFromTheNewestSnapshot_NeverOverwritingIt()
    {
        // V5: an unreadable .bak beside a missing file was RecoveredEmpty without a look at the snapshots, and left in place,
        // so the second write overwrote it.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        File.Delete(StoreFile);
        await File.WriteAllTextAsync(BackupFile, "garbage", Ct);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        var moved = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal("garbage", await File.ReadAllTextAsync(moved, Ct));
        Assert.False(File.Exists(BackupFile));
        Assert.StartsWith("The data file was missing, and its backup couldn't be read and was moved to " + Path.GetFileName(moved) + ", so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path), store.StatusMessage, StringComparison.Ordinal);

        await store.AddRecordAsync(100, Ct);
        await store.AddRecordAsync(101, Ct);
        Assert.Equal("garbage", await File.ReadAllTextAsync(moved, Ct));
        Assert.Equal(earlier[^1].Records + 1, store.CountFile(BackupFile));
    }

    [Fact]
    public void MissingFile_WithAnUnreadableBackup_AndNoSnapshots_IsReadOnly_AndLeavesTheBackup()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(BackupFile, "garbage");

        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        Assert.StartsWith("The data file was missing, and its backup couldn't be read. There is no snapshot of it. Nothing was changed: its backup is left as it is, and the data is read-only.", store.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("garbage", File.ReadAllText(BackupFile));
        Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
    }

    [Fact]
    public async Task EmptyFile_AndAnUnreadableBackup_AreBothMovedAside_UnderTheirOwnNames_AndRestored()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        WriteFile([]);
        await File.WriteAllTextAsync(BackupFile, "garbage", Ct);
        clock.Advance(TimeSpan.FromHours(1));

        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        var moved = Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, moved.Count);
        Assert.Empty(await File.ReadAllBytesAsync(moved[0], Ct));
        Assert.Equal("garbage", await File.ReadAllTextAsync(moved[1], Ct));
        Assert.StartsWith(
            "The data file was empty and was moved to " + Path.GetFileName(moved[0]) + ", and its backup couldn't be read and was moved to " + Path.GetFileName(moved[1]) + ", so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path),
            store.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptFile_WithoutABackup_IsMovedAside_AndRestoredFromTheNewestSnapshot_AndNoSnapshotRotatesOut()
    {
        // An unreadable file is restored from its snapshots as a missing one is (until Bookmarks 1.2:
        // the old RecoveredEmpty with the snapshots kept), the SQLite database's way with a damaged file.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        WriteFile("{ \"Version\": 1, not json"u8.ToArray());
        File.Delete(BackupFile);
        clock.Advance(TimeSpan.FromHours(1));
        var logger = new ListLogger();

        using var store = Open(new StoreOpenOptions(Logger: logger, Time: clock));

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(earlier[^1].Records, store.Count());
        var corrupt = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal("{ \"Version\": 1, not json", await File.ReadAllTextAsync(corrupt, Ct));
        Assert.StartsWith("The data file couldn't be read and was moved to " + Path.GetFileName(corrupt) + " and there was no backup, so the data was restored from the snapshot " + Path.GetFileName(earlier[^1].Path), store.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(logger.At(LogLevel.Error), e => e.Message.Contains("moved it to", StringComparison.Ordinal) && e.Exception is JsonException);
        Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.StartsWith("Restored ", StringComparison.Ordinal));
        Assert.Equal(earlier[^1].Records, store.CountFile(StoreFile));
        Assert.False(File.Exists(BackupFile), "The unreadable file became the .bak.");
        Assert.Equal(clock.GetUtcNow().UtcDateTime, store.KeepSnapshotsBeforeUtc);

        for (var i = 0; i <= KeepInTests; i++)
        {
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        AssertKept(store, earlier);
    }

    [Fact]
    public async Task CorruptFile_BesideSnapshotsOfAnEmptyDocumentOnly_IsReadOnly_AndAnAdminMayRestoreOneOfThem()
    {
        // The snapshots hold nothing, but the file may (written after them): the store doesn't decide, an admin does.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        using (var fresh = Open(new StoreOpenOptions(Time: clock)))
        {
            await fresh.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
            await fresh.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct);
        }

        WriteFile("not json"u8.ToArray());
        clock.Advance(TimeSpan.FromHours(1));
        using var store = Open(new StoreOpenOptions(Time: clock));

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        var names = store.ListSnapshots(SnapshotDirectory).Select(s => s.Name).ToList();
        Assert.Contains("None of its snapshots holds any data: " + string.Join(", ", names) + ".", store.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("To go on, restore one of the snapshots, or start empty, on the plugin's settings page", store.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("not json", await File.ReadAllTextAsync(StoreFile, Ct));

        var result = await store.RecoverAsync(SnapshotDirectory, names[^1], startEmpty: false, Ct);
        Assert.Equal(names[^1], result.RestoredFrom);
        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.Equal(0, store.Count());
        var corrupt = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal("not json", await File.ReadAllTextAsync(corrupt, Ct));
    }

    [Fact]
    public async Task CorruptFile_BesideASnapshotInUse_Is503_MovesNothing_ThenIsRestored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that no other handle can read it is Windows' share mode.");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        WriteFile("not json"u8.ToArray());
        File.Delete(BackupFile);

        using var store = Open(new StoreOpenOptions(Time: clock));
        using (new FileStream(earlier[^1].Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<PluginException>(() => store.Count());
            Assert.Equal((PluginErrorKind.Unavailable, StoreMessages.SnapshotReadFailed(Path.GetFileName(earlier[^1].Path))), (ex.Kind, ex.Message));
            Assert.Equal("not json", await File.ReadAllTextAsync(StoreFile, Ct));
            Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
        }

        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        var corrupt = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*"));
        Assert.Equal("not json", await File.ReadAllTextAsync(corrupt, Ct));
        Assert.StartsWith("The data file couldn't be read and was moved to " + Path.GetFileName(corrupt) + " and there was no backup, so the data was restored", store.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recover_RestoresTheSnapshotAnAdminChooses_FromDataMissing_AndKeepsEverySnapshot()
    {
        // The only snapshot is a newer version's, so the store is read-only; an admin
        // puts a readable snapshot back while the server runs and restores it (from the plugin's settings page).
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var newer = Path.Combine(SnapshotDirectory, stem + "-20261002-031000.json");
        await File.WriteAllBytesAsync(EnsureDirectory(newer), Edit(Subject.CreateFile(4), r => r[JsonStoreFormat.VersionProperty] = Current + 1), Ct);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 3, 10, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        using var store = Open(new StoreOpenOptions(Logger: logger, Time: clock));
        Assert.Equal(StoreStatus.ReadOnlyDataMissing, store.Status);
        Assert.True(store.CanRecover);

        var good = Path.Combine(SnapshotDirectory, stem + "-20261001-031000.json");
        await File.WriteAllBytesAsync(good, Subject.CreateFile(3), Ct);
        var writable = store.WaitUntilWritableAsync(Ct);
        Assert.False(writable.IsCompleted);

        var result = await store.RecoverAsync(SnapshotDirectory, Path.GetFileName(good).ToUpperInvariant(), startEmpty: false, Ct);

        Assert.Equal(Path.GetFileName(good), result.RestoredFrom);
        Assert.False(result.StartedEmpty);
        Assert.Equal(Current, result.SchemaVersion);
        Assert.Null(result.MigratedFrom);
        Assert.Empty(result.MovedAside);
        await writable.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
        Assert.False(store.IsReadOnly);
        Assert.False(store.CanRecover);
        Assert.Equal(3, store.Count());
        Assert.StartsWith("The data file was missing and there was no backup; an admin restored the data from the snapshot " + Path.GetFileName(good) + " taken 2026-10-01 03:10 UTC, so changes made after it are missing.", store.StatusMessage, StringComparison.Ordinal);
        Assert.Single(logger.At(LogLevel.Warning), e => e.Message.StartsWith("An admin restored ", StringComparison.Ordinal));
        Assert.Equal(3, store.CountFile(StoreFile));
        Assert.True(File.Exists(StoreFile + ".last-restore"));

        // Every snapshot is kept, the newer version's too: it may be the only copy of its data.
        Assert.Equal(clock.GetUtcNow().UtcDateTime, store.KeepSnapshotsBeforeUtc);
        for (var i = 0; i <= KeepInTests; i++)
        {
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.True(File.Exists(newer) && File.Exists(good));
        await store.AddRecordAsync(9, Ct);
        using var restarted = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.Ok, restarted.Status);
        Assert.Equal(4, restarted.Count());
        Assert.NotNull(restarted.KeepSnapshotsBeforeUtc);
    }

    [Fact]
    public async Task Recover_StartsEmpty_WhenAnAdminChooses_KeepingEverySnapshotAndWhatCouldntBeRead()
    {
        // From ReadOnlyDataUnreadable beside a newer version's snapshots, daily and pre-operation: the unreadable file and
        // .bak are moved aside under their own names, and the snapshots outlive every rotation.
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var newerDaily = Path.Combine(SnapshotDirectory, stem + "-20261003-031000.json");
        var newerPreOp = Path.Combine(SnapshotDirectory, SnapshotOperations.Folder, stem + "-import-20261002-031000.json");
        var newerBytes = Edit(Subject.CreateFile(4), r => r[JsonStoreFormat.VersionProperty] = Current + 1);
        await File.WriteAllBytesAsync(EnsureDirectory(newerDaily), newerBytes, Ct);
        await File.WriteAllBytesAsync(EnsureDirectory(newerPreOp), newerBytes, Ct);
        WriteFile("not json"u8.ToArray());
        await File.WriteAllTextAsync(BackupFile, "garbage too", Ct);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 3, 10, 0, TimeSpan.Zero));
        using var store = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        Assert.Contains("Its snapshots may hold the data, but none of them could be restored: ", store.StatusMessage, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<PluginException>(() => store.RestoreSnapshotAsync(SnapshotDirectory, Path.GetFileName(newerDaily), Ct));
        Assert.Equal(PluginErrorKind.Conflict, refused.Kind);

        var result = await store.RecoverAsync(SnapshotDirectory, null, startEmpty: true, Ct);

        Assert.True(result.StartedEmpty);
        Assert.Null(result.RestoredFrom);
        Assert.Equal([Subject.StoreFileName + ".corrupt-20261004031000", Subject.StoreFileName + ".corrupt-20261004031000-1"], result.MovedAside);
        Assert.Equal("not json", await File.ReadAllTextAsync(Path.Combine(DataDirectory, result.MovedAside[0]), Ct));
        Assert.Equal("garbage too", await File.ReadAllTextAsync(Path.Combine(DataDirectory, result.MovedAside[1]), Ct));
        Assert.False(File.Exists(BackupFile));
        Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
        Assert.Equal(0, store.CountFile(StoreFile));
        Assert.Equal(
            "The data file couldn't be read, and its backup couldn't be read either; an admin chose to start empty. What was found in its place is kept as " + result.MovedAside[0] + " and " + result.MovedAside[1] + ". Every snapshot is kept until you restore one of them (or delete " + Subject.StoreFileName + ".keep-snapshots).",
            store.StatusMessage);

        for (var i = 0; i <= KeepInTests; i++)
        {
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        for (var i = 0; i <= SnapshotOperations.ToKeep; i++)
        {
            await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Other, Ct);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(newerBytes, await File.ReadAllBytesAsync(newerDaily, Ct));
        Assert.Equal(newerBytes, await File.ReadAllBytesAsync(newerPreOp, Ct));
        Assert.All(store.ListSnapshots(SnapshotDirectory).Where(s => s.Name == Path.GetFileName(newerDaily) || s.Name == Path.GetFileName(newerPreOp)), s => Assert.True(s.Kept));

        using var restarted = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.Ok, restarted.Status);
        Assert.Equal(0, restarted.Count());
        Assert.NotNull(restarted.KeepSnapshotsBeforeUtc);
        Assert.Equal(2, Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.corrupt-*").Length);
    }

    [Fact]
    public async Task Recover_RefusesWhatItCantDo_AndChangesNothing()
    {
        // Data that is there: 409 (RestoreSnapshotAsync is the way back). Both or neither of a snapshot and start empty: 400.
        // A name no snapshot of this store has, a path among them: 404. A newer version's, an unreadable or an empty
        // snapshot: 409.
        await WriteThroughStoreAsync(2);
        using (var healthy = Open())
        {
            Assert.False(healthy.CanRecover);
            var ex = await Assert.ThrowsAsync<PluginException>(() => healthy.RecoverAsync(SnapshotDirectory, null, startEmpty: true, Ct));
            Assert.Equal((PluginErrorKind.Conflict, StoreMessages.NothingToRecover), (ex.Kind, ex.Message));
            Assert.Equal(2, healthy.Count());
        }

        ResetDataDirectory();
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var newer = stem + "-20261001-031000.json";
        var corrupt = stem + "-20261002-031000.json";
        var blank = stem + "-20261003-031000.json";
        Directory.CreateDirectory(SnapshotDirectory);
        await File.WriteAllBytesAsync(Path.Combine(SnapshotDirectory, newer), Edit(Subject.CreateFile(1), r => r[JsonStoreFormat.VersionProperty] = Current + 1), Ct);
        await File.WriteAllTextAsync(Path.Combine(SnapshotDirectory, corrupt), "{ not json", Ct);
        await File.WriteAllBytesAsync(Path.Combine(SnapshotDirectory, blank), [], Ct);
        await File.WriteAllBytesAsync(Path.Combine(DataDirectory, "outside.json"), Subject.CreateFile(9), Ct);
        var before = SnapshotFiles();
        using var store = Open();
        Assert.Equal(StoreStatus.ReadOnlyDataMissing, store.Status);

        foreach (var (name, startEmpty) in new (string?, bool)[] { ("x.json", true), (null, false), ("  ", false) })
        {
            Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => store.RecoverAsync(SnapshotDirectory, name, startEmpty, Ct))).Kind);
        }

        foreach (var name in new[] { "nothing.json", "../outside.json" })
        {
            Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => store.RecoverAsync(SnapshotDirectory, name, startEmpty: false, Ct))).Kind);
        }

        foreach (var (name, says) in new[] { (newer, "newer plugin version"), (corrupt, "couldn't be read"), (blank, "holds no data") })
        {
            var ex = await Assert.ThrowsAsync<PluginException>(() => store.RecoverAsync(SnapshotDirectory, name, startEmpty: false, Ct));
            Assert.Equal(PluginErrorKind.Conflict, ex.Kind);
            Assert.Contains(says, ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(StoreStatus.ReadOnlyDataMissing, store.Status);
        Assert.Equal(before, SnapshotFiles());
        Assert.False(File.Exists(StoreFile));
        Assert.False(File.Exists(StoreFile + ".keep-snapshots"));
        Assert.False(File.Exists(StoreFile + ".last-restore"));

        // The other read-only statuses keep the data they have: never replaced here.
        ResetDataDirectory();
        var newerFile = Edit(Subject.CreateFile(2), r => r[JsonStoreFormat.VersionProperty] = Current + 1);
        WriteFile(newerFile);
        using var newerStore = Open();
        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, newerStore.Status);
        Assert.False(newerStore.CanRecover);
        Assert.Equal(StoreMessages.NothingToRecover, (await Assert.ThrowsAsync<PluginException>(() => newerStore.RecoverAsync(SnapshotDirectory, null, startEmpty: true, Ct))).Message);
        Assert.Equal(newerFile, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    // ---- The hardening: the kept snapshots recorded by name, a file put back while the server runs, a move that
    // fails, a stale .bak

    [Fact]
    public async Task ClockBehind_RestoringASnapshotTakenAfterTheIncident_KeepsTheKeptOnes_AndOnlyTheNewOnesRotate()
    {
        // With the clock behind the newest snapshots at the incident, the marker's time
        // (the newest one's plus a second) was later than now, so every snapshot taken afterwards while the clock was behind
        // was dated before it. Restoring one (undoing "start empty, then import" with its import snapshot, say) counted as
        // restoring a kept one: the marker went, with no Warning, and the snapshots from before the incident (the one dated
        // in the future and the newer schema's among them, which the banner promised to keep) rotated out.
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        var before = new List<string>();
        for (var day = 5; day <= 12; day++)
        {
            var path = Path.Combine(SnapshotDirectory, string.Create(CultureInfo.InvariantCulture, $"{stem}-202610{day:00}-031000.json"));
            await File.WriteAllBytesAsync(EnsureDirectory(path), day == 12 ? Edit(Subject.CreateFile(day), r => r[JsonStoreFormat.VersionProperty] = Current + 1) : Subject.CreateFile(day), Ct);
            before.Add(path);
        }

        WriteFile("\0\0\0"u8.ToArray());
        var marker = StoreFile + ".keep-snapshots";
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        using (var store = Open(new StoreOpenOptions(Logger: logger, Time: clock)))
        {
            // The newest not dated in the future is restored (the one of October 11 is, and October 12's has a newer schema).
            Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);
            Assert.Equal(10, store.Count());
            Assert.Contains("restored from the snapshot " + stem + "-20261010-031000.json", store.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(new DateTime(2026, 10, 12, 3, 10, 1, DateTimeKind.Utc), store.KeepSnapshotsBeforeUtc);
            Assert.All(store.ListSnapshots(SnapshotDirectory), s => Assert.True(s.Kept, s.Name));
            var recorded = Assert.IsType<JsonArray>(JsonNode.Parse(await File.ReadAllBytesAsync(marker, Ct))!["Snapshots"]);
            Assert.Equal(before.Select(Path.GetFileName).Order(StringComparer.Ordinal), recorded.Select(n => (string)n!).Order(StringComparer.Ordinal));

            // The next day, the clock still behind: a daily snapshot and an import's are dated before the marker's time, and
            // restoring either leaves the kept ones kept, with a Warning each.
            clock.Set(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
            var daily = Path.GetFileName((await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct))!);
            Assert.Equal(stem + "-20261010-120000.json", daily);
            clock.Advance(TimeSpan.FromMinutes(1));
            var import = Path.GetFileName((await store.PreOpSnapshotAsync(SnapshotDirectory, SnapshotOperations.Import, Ct))!);
            foreach (var later in new[] { import, daily })
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                var result = await store.RestoreSnapshotAsync(SnapshotDirectory, later, Ct);
                Assert.NotNull(result.KeepSnapshotsBeforeUtc);
                Assert.NotNull(store.KeepSnapshotsBeforeUtc);
                Assert.True(File.Exists(marker), "Restoring " + later + " released the kept snapshots.");
                Assert.False(store.ListSnapshots(SnapshotDirectory).Single(s => s.Name == later).Kept, later);
            }

            Assert.Equal(2, logger.At(LogLevel.Warning).Count(e => e.Message.Contains("still kept", StringComparison.Ordinal)));

            // Five daily runs keeping 3: only the snapshots taken since rotate, so all those from before the incident stay.
            for (var i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromDays(1));
                await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            }

            Assert.All(before, p => Assert.True(File.Exists(p), p + " was rotated out."));
            var dailies = store.ListSnapshots(SnapshotDirectory).Where(s => s.Kind == SnapshotKind.Daily).ToList();
            Assert.Equal(before.Count + KeepInTests, dailies.Count);
            Assert.Equal(KeepInTests, dailies.Count(s => !s.Kept));
        }

        // A restart with the clock right again: the marker still names them, and only a restore of one of them ends it.
        clock.Set(new DateTimeOffset(2026, 10, 30, 3, 10, 0, TimeSpan.Zero));
        using var reopened = Open(new StoreOpenOptions(Time: clock));
        for (var i = 0; i < KeepInTests + 1; i++)
        {
            await reopened.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.All(before, p => Assert.Contains(reopened.ListSnapshots(SnapshotDirectory), s => s.Name == Path.GetFileName(p) && s.Kept));
        await reopened.RestoreSnapshotAsync(SnapshotDirectory, Path.GetFileName(before[3]), Ct);
        Assert.Equal(8, reopened.Count());
        Assert.Null(reopened.KeepSnapshotsBeforeUtc);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ASnapshotDatedFarAhead_KeepsOnlyTheSnapshotsThereWereThen_AndTheLaterOnesRotate()
    {
        // One snapshot named 2099 made the marker's time 2099, so every snapshot taken after the incident was kept
        // until then (62 daily ones after 60 days, keeping 3). The names recorded keep exactly those there were.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        var far = Path.Combine(SnapshotDirectory, Path.GetFileNameWithoutExtension(Subject.StoreFileName) + "-20990101-000000.json");
        await File.WriteAllBytesAsync(far, Subject.CreateFile(1), Ct);
        File.Delete(StoreFile);
        File.Delete(BackupFile);

        using var store = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, store.Status);

        // Bookmarks 1.2.1: the marker's time, which only an earlier version reads, is at most a week ahead (it was 2099,
        // which made an earlier version keep every snapshot after a downgrade); the 2099 one is kept by name all the same.
        Assert.Equal(clock.GetUtcNow().UtcDateTime + TimeSpan.FromDays(7), store.KeepSnapshotsBeforeUtc);
        for (var day = 0; day < 60; day++)
        {
            await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            clock.Advance(TimeSpan.FromDays(1));
        }

        AssertKept(store, earlier);
        Assert.True(File.Exists(far));
        var dailies = store.ListSnapshots(SnapshotDirectory).Where(s => s.Kind == SnapshotKind.Daily).ToList();
        Assert.Equal(earlier.Count + 1 + KeepInTests, dailies.Count);
        Assert.Equal(earlier.Count + 1, dailies.Count(s => s.Kept));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMarkerOfAnEarlierVersion_KeepsTheSnapshotsDatedBeforeItsTime_RecordedByName_AndOnlyOneFromBeforeTheIncidentReleasesThem(bool withSince)
    {
        // Bookmarks 1.1's marker is {KeepSnapshotsBeforeUtc, Reason}, the incident's time; an early 1.2 build's adds SinceUtc,
        // the incident's time, and makes KeepSnapshotsBeforeUtc the newest snapshot's plus a second when that is later (a
        // clock behind then). Both kept every snapshot dated before KeepSnapshotsBeforeUtc. Read now: the ones there are
        // dated before it are recorded by name, once (its fields kept, for a downgrade), so the snapshots taken since rotate
        // even while the clock is behind that time, and only one dated before the incident releases them: one dated between
        // the incident and that time may have been taken since, while the clock was behind.
        var stem = Path.GetFileNameWithoutExtension(Subject.StoreFileName);
        string Daily(int day, int hour = 3) => Path.Combine(SnapshotDirectory, string.Create(CultureInfo.InvariantCulture, $"{stem}-202610{day:00}-{hour:00}1000.json"));
        var kept = new List<string> { Daily(1), Daily(2), Daily(3) };
        var since = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        var keepBefore = since;
        if (withSince)
        {
            // October 5 is ambiguous (taken after the incident with the clock behind, or before it with the clock ahead);
            // October 6 was dated in the future then, and set the time.
            kept.Add(Daily(5));
            kept.Add(Daily(6));
            keepBefore = new DateTime(2026, 10, 6, 3, 10, 1, DateTimeKind.Utc);
        }

        foreach (var path in kept)
        {
            await File.WriteAllBytesAsync(EnsureDirectory(path), Subject.CreateFile(kept.IndexOf(path) + 1), Ct);
        }

        WriteFile(Subject.CreateFile(9));
        var marker = StoreFile + ".keep-snapshots";
        var old = new JsonObject { ["KeepSnapshotsBeforeUtc"] = keepBefore };
        if (withSince)
        {
            old["SinceUtc"] = since;
        }

        old["Reason"] = "The data file was empty and was moved to x, so the data starts empty.";
        await File.WriteAllTextAsync(marker, old.ToJsonString(), Ct);

        // The clock is behind the marker's time for 1.2's first marker: new snapshots are dated before it.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        using var store = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Equal(keepBefore, store.KeepSnapshotsBeforeUtc);
        Assert.Equal(kept.Select(Path.GetFileName).Order(StringComparer.Ordinal), store.ListSnapshots(SnapshotDirectory).Where(s => s.Kept).Select(s => s.Name).Order(StringComparer.Ordinal));
        var rewritten = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllBytesAsync(marker, Ct)));
        Assert.Equal(keepBefore, (DateTime)rewritten["KeepSnapshotsBeforeUtc"]!);
        Assert.Equal(since, (DateTime)rewritten["ReleaseBeforeUtc"]!);
        Assert.Equal(kept.Select(Path.GetFileName).Order(StringComparer.Ordinal), rewritten["Snapshots"]!.AsArray().Select(n => (string)n!).Order(StringComparer.Ordinal));

        var taken = new List<string>();
        for (var i = 0; i < KeepInTests + 2; i++)
        {
            taken.Add(Path.GetFileName((await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct))!));
            clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.All(kept, p => Assert.True(File.Exists(p), p + " was rotated out."));
        Assert.Equal(KeepInTests, store.ListSnapshots(SnapshotDirectory).Count(s => s.Kind == SnapshotKind.Daily && !s.Kept));

        // One taken since, or one dated after the incident, leaves them kept; one from before the incident releases them.
        foreach (var later in withSince ? new[] { taken[^1], Path.GetFileName(Daily(5)) } : new[] { taken[^1] })
        {
            await store.RestoreSnapshotAsync(SnapshotDirectory, later, Ct);
            Assert.True(File.Exists(marker), "Restoring " + later + " released the kept snapshots.");
        }

        await store.RestoreSnapshotAsync(SnapshotDirectory, Path.GetFileName(Daily(2)), Ct);
        Assert.Equal(2, store.Count());
        Assert.Null(store.KeepSnapshotsBeforeUtc);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Recover_WhileAReadableFileWasPutBack_IsRefused_AndMovesNothing()
    {
        // The status is what the load found, and the recovery moved whatever was in place: an admin who copied a
        // readable file back while the server runs, then chose Start empty (or a snapshot), had it moved aside as unreadable.
        var unreadable = "\0\0\0"u8.ToArray();
        WriteFile(unreadable);
        var good = Subject.CreateFile(3);
        using (var store = Open())
        {
            Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
            WriteFile(good);

            var refused = await Assert.ThrowsAsync<PluginException>(() => store.RecoverAsync(SnapshotDirectory, null, startEmpty: true, Ct));
            Assert.Equal((PluginErrorKind.Conflict, StoreMessages.ReadableFileInPlace(Subject.StoreFileName)), (refused.Kind, refused.Message));
            Assert.Equal(good, await File.ReadAllBytesAsync(StoreFile, Ct));
            Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
            Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
            Assert.True(store.CanRecover);

            // A readable .bak beside the unreadable file: the same, since the next start uses it.
            WriteFile(unreadable);
            await File.WriteAllBytesAsync(BackupFile, good, Ct);
            refused = await Assert.ThrowsAsync<PluginException>(() => store.RecoverAsync(SnapshotDirectory, null, startEmpty: true, Ct));
            Assert.Equal(StoreMessages.ReadableFileInPlace(Subject.StoreFileName + ".bak"), refused.Message);
            Assert.Equal(unreadable, await File.ReadAllBytesAsync(StoreFile, Ct));
            Assert.Equal(good, await File.ReadAllBytesAsync(BackupFile, Ct));
            Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
        }

        // The restart the refusal names uses it.
        using var restarted = Open();
        Assert.Equal(StoreStatus.RecoveredFromBackup, restarted.Status);
        Assert.Equal(3, restarted.Count());
    }

    [Fact]
    public async Task CorruptFile_WhoseBackupCantBeMovedAside_IsPutBack_And503SaysSo_ThenIsRestored_NamingBoth()
    {
        // The unreadable file was moved aside first; when the unreadable .bak then couldn't be moved (open in
        // another program without delete sharing), the 503 said "The data file couldn't be read", the log "Could not read",
        // and a restart found the file missing: its banner named only the .bak. The file goes back now, as an admin's
        // recovery puts back what it moved.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be moved is Windows' share mode.");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        var unreadable = "\0\0\0"u8.ToArray();
        WriteFile(unreadable);
        await File.WriteAllTextAsync(BackupFile, "{ not json", Ct);
        var logger = new ListLogger();
        using (var held = new FileStream(BackupFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var store = Open(new StoreOpenOptions(Logger: logger, Time: clock));
            for (var i = 0; i < 3; i++)
            {
                var failed = Assert.Throws<PluginException>(() => store.Status);
                Assert.Equal((PluginErrorKind.Unavailable, StoreMessages.MoveAsideFailed), (failed.Kind, failed.Message));
                Assert.Equal(unreadable, await File.ReadAllBytesAsync(StoreFile, Ct));
                Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
            }

            var error = Assert.Single(logger.At(LogLevel.Error));
            Assert.Contains("couldn't be moved aside", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Could not read " + StoreFile + ";", error.Message, StringComparison.Ordinal);
        }

        // Once the .bak can be moved (here: a restart), both go aside and the newest snapshot is restored; the banner names both.
        using var restarted = Open(new StoreOpenOptions(Time: clock));
        Assert.Equal(StoreStatus.RestoredFromSnapshot, restarted.Status);
        Assert.Equal(earlier[^1].Records, restarted.Count());
        Assert.StartsWith("The data file couldn't be read and was moved to " + Subject.StoreFileName + ".corrupt-", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(", and its backup couldn't be read and was moved to " + Subject.StoreFileName + ".corrupt-", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(DataDirectory, "*.corrupt-*").Length);
    }

    [Fact]
    public async Task CorruptFile_WhoseBackupsUpgradeCopyCantBeWritten_IsPutBack_And503SaysSo()
    {
        // A .bak of an older schema takes the unreadable file's place after its copy from before the upgrade is
        // written; when that copy couldn't be written, the file (moved aside first) stayed aside and the 503 said the data
        // file couldn't be read.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var unreadable = "\0\0\0"u8.ToArray();
        WriteFile(unreadable);
        var backup = Subject.CreateFile(2);
        await File.WriteAllBytesAsync(BackupFile, backup, Ct);

        // The store one version ahead, so the .bak is older; a folder where the copy's .tmp would be written.
        var ahead = new StoreOpenOptions(ExtraMigration: new DelegateMigration(Current, _ => { }), Time: clock);
        var blocker = Directory.CreateDirectory(string.Create(CultureInfo.InvariantCulture, $"{StoreFile}.v{Current}-{clock.GetUtcNow().UtcDateTime:yyyyMMddHHmmss}.tmp"));
        using var store = Open(ahead);
        var failed = Assert.Throws<PluginException>(() => store.Status);
        Assert.Equal((PluginErrorKind.Unavailable, StoreMessages.UpgradeCopyFailed), (failed.Kind, failed.Message));
        Assert.Equal(unreadable, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Equal(backup, await File.ReadAllBytesAsync(BackupFile, Ct));
        Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));

        // Once it can be written, the next use upgrades the .bak in the file's place.
        blocker.Delete();
        Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
        Assert.Equal(2, store.Count());
        Assert.Single(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
    }

    [Fact]
    public async Task CorruptFile_WithAStaleBackup_BesideANewerSnapshotOfOtherData_KeepsEverySnapshot_AndNamesIt()
    {
        // A .bak older than a snapshot holding other data (a write that couldn't refresh it) took the file's place,
        // and the newer snapshots rotated out within keep days while the banner said only "the most recent change may be
        // missing".
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
        var earlier = await TakeDailySnapshotsAsync(clock);
        var newest = Path.GetFileName(earlier[^1].Path);
        File.SetLastWriteTimeUtc(BackupFile, new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc));
        WriteFile("\0\0\0"u8.ToArray());
        clock.Advance(TimeSpan.FromHours(1));
        var logger = new ListLogger();
        using (var store = Open(new StoreOpenOptions(Logger: logger, Time: clock)))
        {
            Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
            Assert.Equal(earlier[^1].Records - 1, store.Count());
            Assert.EndsWith(
                " A newer snapshot from 2026-10-03 03:10 UTC exists (" + newest + "); restore it on the plugin's settings page if it holds changes the backup lacks. The snapshots, daily and pre-operation, are kept until you restore one of them (or delete " + Subject.StoreFileName + ".keep-snapshots).",
                store.StatusMessage,
                StringComparison.Ordinal);
            Assert.NotNull(store.KeepSnapshotsBeforeUtc);
            Assert.All(store.ListSnapshots(SnapshotDirectory), s => Assert.True(s.Kept, s.Name));
            Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.Contains(newest + " taken 2026-10-03 03:10:00 UTC holds other data", StringComparison.Ordinal));
            for (var i = 0; i <= KeepInTests; i++)
            {
                clock.Advance(TimeSpan.FromDays(1));
                await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct);
            }

            AssertKept(store, earlier);
            await store.RestoreSnapshotAsync(SnapshotDirectory, newest, Ct);
            Assert.Equal(earlier[^1].Records, store.Count());
            Assert.Null(store.KeepSnapshotsBeforeUtc);
        }

        // A .bak written after the newest snapshot, or one holding its data: as before, nothing kept.
        foreach (var sameData in new[] { false, true })
        {
            ResetDataDirectory();
            clock.Set(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
            var again = await TakeDailySnapshotsAsync(clock);
            if (sameData)
            {
                await File.WriteAllBytesAsync(BackupFile, await File.ReadAllBytesAsync(again[^1].Path, Ct), Ct);
                File.SetLastWriteTimeUtc(BackupFile, new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc));
            }
            else
            {
                File.SetLastWriteTimeUtc(BackupFile, new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
            }

            WriteFile("\0\0\0"u8.ToArray());
            using var store = Open(new StoreOpenOptions(Time: clock));
            Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
            Assert.EndsWith("; the backup copy was used, so the most recent change may be missing.", store.StatusMessage, StringComparison.Ordinal);
            Assert.Null(store.KeepSnapshotsBeforeUtc);
        }
    }

    // ---- Export data

    [Fact]
    public async Task ExportData_ReadsBack_AsTheSameDocument()
    {
        await WriteThroughStoreAsync(3);
        using var store = Open();

        var export = store.ExportData();
        var data = export.Data;

        Assert.Equal(Current, export.SchemaVersion);
        Assert.Equal(Current, (int)data[JsonStoreFormat.VersionProperty]!);
        Assert.Equal(3, store.CountData(data, Current));
        Assert.Equal(3, store.CountData(Assert.IsType<JsonObject>(JsonNode.Parse(data.ToJsonString())), Current));
        var mismatch = Assert.Throws<PluginException>(() => store.CountData(data, Current == 1 ? 2 : 1));
        Assert.True(mismatch.Kind is PluginErrorKind.Invalid or PluginErrorKind.Conflict);
        var newer = (JsonObject)data.DeepClone();
        newer[JsonStoreFormat.VersionProperty] = Current + 1;
        Assert.Equal(PluginErrorKind.Conflict, Assert.Throws<PluginException>(() => store.CountData(newer, Current + 1)).Kind);
    }

    [Fact]
    public async Task ExportData_WhileANewerVersion_PassesTheFileOnAsItIs_AtItsVersion()
    {
        // Export still works in read-only mode. The newer file's own JSON goes out at its
        // version, not this version's reading of it labelled with this version, which no version could import.
        var newer = Edit(
            Subject.CreateFile(2),
            root =>
            {
                root[JsonStoreFormat.VersionProperty] = Current + 1;
                root[UnknownField] = new JsonObject { ["From"] = "the newer version" };
            });
        WriteFile(newer);
        using (var store = Open())
        {
            Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Status);

            var export = store.ExportData();

            Assert.Equal(Current + 1, export.SchemaVersion);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(newer), export.Data));
            Assert.Equal(PluginErrorKind.Conflict, Assert.Throws<PluginException>(() => store.CountData(export.Data, export.SchemaVersion)).Kind);
            Assert.Equal(newer, await File.ReadAllBytesAsync(StoreFile, Ct));
        }

        // A newer file this version can't even show still goes out whole.
        ResetDataDirectory();
        var unreadable = Edit(
            Subject.CreateFile(2),
            root =>
            {
                Subject.BreakDeserialization(root);
                root[JsonStoreFormat.VersionProperty] = Current + 1;
            });
        WriteFile(unreadable);
        using var other = Open();
        Assert.Equal(0, other.Count());
        var whole = other.ExportData();
        Assert.Equal(Current + 1, whole.SchemaVersion);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(unreadable), whole.Data));
    }

    [Fact]
    public async Task ExportData_AfterAFailedMigration_Is503_AndChangesNothing()
    {
        // The store serves an empty document then, and an export of it would look valid and hold nothing. The
        // unchanged file and its .v<N> copy are the backup.
        var original = await WriteThroughStoreAsync(3);
        using var store = Open(new StoreOpenOptions(ExtraMigration: new DelegateMigration(Current, _ => throw new InvalidOperationException("Conformance: this step always fails."))));
        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);

        var ex = Assert.Throws<PluginException>(() => store.ExportData());

        Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
        Assert.Equal(StoreMessages.ExportMigrationFailed, ex.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public void ReadData_OfOlderFixtures_Migrates()
    {
        var older = Subject.Fixtures.Where(f => f.Version < Current).ToList();
        if (older.Count == 0)
        {
            Assert.Skip("The store has no fixture of an older schema version.");
        }

        using var store = Open();
        foreach (var fixture in older)
        {
            var data = Assert.IsType<JsonObject>(JsonNode.Parse(JsonStoreFormat.SkipByteOrderMark(fixture.Content).ToArray()));
            var copy = data.ToJsonString();
            Assert.Equal(fixture.Records, store.CountData(data, fixture.Version));
            Assert.Equal(copy, data.ToJsonString());
        }
    }

    // ---- Timing

    /// <summary>
    /// The cost of loading, rewriting and snapshotting a 5 MB document (the size at which a JSON store should move to
    /// SQLite). The medians are written to the test output; the budgets only catch a pathological regression.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FiveMegabyteDocument_Timing()
    {
        var (content, records) = Subject.CreateLargeFile(TimingDocumentBytes);
        WriteFile(content);

        var loads = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            using var store = Open();
            Assert.Equal(records, store.Count());
            loads.Add(watch.Elapsed.TotalMilliseconds);
        }

        var commits = new List<double>();
        var snapshots = new List<double>();
        using (var store = Open())
        {
            store.Count();
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                await store.AddRecordAsync(1_000_000 + i, Ct);
                commits.Add(watch.Elapsed.TotalMilliseconds);
            }

            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                await store.SnapshotAsync(SnapshotDirectory, 7, Ct);
                snapshots.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        var size = new FileInfo(StoreFile).Length;
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"{GetType().Name}: {size / 1048576.0:F2} MB, {records + 5} records. Load median {Median(loads):F0} ms (all {Join(loads)}); commit median {Median(commits):F0} ms (all {Join(commits)}); snapshot median {Median(snapshots):F0} ms (all {Join(snapshots)}).");
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        TestContext.Current.SendDiagnosticMessage(report);

        Assert.True(size >= TimingDocumentBytes);
        Assert.True(Median(loads) < 5000, report);
        Assert.True(Median(commits) < 5000, report);
        Assert.True(Median(snapshots) < 5000, report);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Returns the plugin's store definition.
    /// </summary>
    /// <returns>The subject.</returns>
    private protected abstract StoreConformanceSubject CreateSubject();

    /// <summary>
    /// Deletes the test's folder.
    /// </summary>
    /// <param name="disposing">Whether managed resources are released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _directory.Dispose();
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static string Join(List<double> values) => string.Join('/', values.Select(v => v.ToString("F0", CultureInfo.InvariantCulture)));

    private static byte[] Edit(byte[] file, Action<JsonObject> edit)
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(file));
        edit(root);
        return JsonSerializer.SerializeToUtf8Bytes(root, JsonStoreFormat.Options);
    }

    private static int ReadVersion(string path) => (int)JsonNode.Parse(File.ReadAllBytes(path))![JsonStoreFormat.VersionProperty]!;

    private static string EnsureDirectory(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }

    private static IEnumerable<string> NullProperties(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is null)
                    {
                        yield return path + "." + key;
                    }

                    foreach (var inner in NullProperties(value, path + "." + key))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var inner in NullProperties(array[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    private static IEnumerable<(JsonObject Parent, JsonNode? Value)> FindProperties(JsonNode? node, string name)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == name)
                    {
                        yield return (obj, value);
                    }

                    foreach (var inner in FindProperties(value, name))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in FindProperties(item, name))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    private static void AssertKept(StoreHandle store, List<(string Path, int Records)> snapshots)
        => Assert.All(snapshots, s =>
        {
            Assert.True(File.Exists(s.Path), s.Path + " was rotated out.");
            Assert.Equal(s.Records, store.CountFile(s.Path));
        });

    private StoreHandle Open(StoreOpenOptions? options = null) => Subject.Open(StoreFile, options);

    // KeepInTests daily snapshots a day apart (keeping KeepInTests), two more records each day: the snapshots hold 2, 4, 6…
    // records, the file ends with the last count and its .bak with the one before.
    private async Task<List<(string Path, int Records)>> TakeDailySnapshotsAsync(ManualTimeProvider clock)
    {
        var taken = new List<(string Path, int Records)>();
        using var store = Open(new StoreOpenOptions(Time: clock));
        for (var day = 0; day < KeepInTests; day++)
        {
            await store.AddRecordAsync(day * 10, Ct);
            await store.AddRecordAsync((day * 10) + 1, Ct);
            taken.Add(((await store.SnapshotAsync(SnapshotDirectory, KeepInTests, Ct))!, store.Count()));
            clock.Advance(TimeSpan.FromDays(1));
        }

        return taken;
    }

    // Every file under the snapshot folder with its content, to show that nothing there changed.
    private List<string> SnapshotFiles()
        => Directory.GetFiles(SnapshotDirectory, "*", SearchOption.AllDirectories)
            .Select(p => p + "=" + Convert.ToBase64String(File.ReadAllBytes(p)))
            .Order(StringComparer.Ordinal)
            .ToList();

    private void WriteFile(byte[] content) => File.WriteAllBytes(EnsureDirectory(StoreFile), content);

    private void ResetDataDirectory()
    {
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    // Writes the given number of records through the store (so the file and its .bak are the store's own) and
    // returns the file's bytes.
    private async Task<byte[]> WriteThroughStoreAsync(int records)
    {
        using (var store = Open())
        {
            for (var i = 0; i < records; i++)
            {
                await store.AddRecordAsync(i, Ct);
            }
        }

        return await File.ReadAllBytesAsync(StoreFile, Ct);
    }

    private async Task AssertFailedMigrationAsync(IJsonMigration failing, string failedStep)
    {
        // A file and a different, readable .bak: the failure must use neither and change neither.
        var original = await WriteThroughStoreAsync(3);
        var backup = await File.ReadAllBytesAsync(BackupFile, Ct);
        Assert.NotEqual(original, backup);
        var logger = new ListLogger();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 3, 10, 0, TimeSpan.Zero));

        using (var store = Open(new StoreOpenOptions(ExtraMigration: failing, Logger: logger, Time: clock)))
        {
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
            Assert.True(store.IsReadOnly);
            Assert.Equal(Current, store.LoadedVersion);
            Assert.Null(store.MigratedFrom);
            Assert.Equal(0, store.Count());
            Assert.NotNull(store.StatusMessage);

            var ex = await Assert.ThrowsAsync<PluginException>(() => store.AddRecordAsync(99, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
            Assert.Equal(StoreMessages.MigrationFailed, ex.Message);
            await Assert.ThrowsAsync<PluginException>(() => store.NoOpUpdateAsync(Ct));

            Assert.Null(await store.SnapshotAsync(SnapshotDirectory, 7, Ct));
            Assert.False(Directory.Exists(SnapshotDirectory), "A read-only store wrote a snapshot.");
            Assert.Null(store.LastSnapshotUtc);

            var error = Assert.Single(logger.At(LogLevel.Error));
            Assert.Contains(failedStep, error.Message, StringComparison.Ordinal);
            Assert.NotNull(error.Exception);
        }

        // Every later start fails the same way (a restart, a day later): it reuses the .v<N> copy instead of adding one.
        clock.Advance(TimeSpan.FromDays(1));
        using (var store = Open(new StoreOpenOptions(ExtraMigration: failing, Time: clock)))
        {
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Equal(backup, await File.ReadAllBytesAsync(BackupFile, Ct));
        Assert.Empty(Directory.GetFiles(DataDirectory, "*.corrupt-*"));
        Assert.False(File.Exists(StoreFile + ".tmp"));
        var copy = Assert.Single(Directory.GetFiles(DataDirectory, $"{Subject.StoreFileName}.v{Current}-*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(copy, Ct));
        Assert.Equal(3, Directory.GetFiles(DataDirectory).Length);

        // The data is intact for the version that can read it.
        using var again = Open();
        Assert.Equal(StoreStatus.Ok, again.Status);
        Assert.Equal(3, again.Count());
    }

    private sealed class DelegateMigration : IJsonMigration
    {
        private readonly Action<JsonObject> _apply;

        public DelegateMigration(int fromVersion, Action<JsonObject> apply)
        {
            FromVersion = fromVersion;
            _apply = apply;
        }

        public int FromVersion { get; }

        public void Apply(JsonObject root) => _apply(root);
    }
}
