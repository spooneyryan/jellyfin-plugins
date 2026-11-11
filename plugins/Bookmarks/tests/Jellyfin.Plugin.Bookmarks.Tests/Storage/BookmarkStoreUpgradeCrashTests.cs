using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Storage.Migrations;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// The 1→2 upgrade on the real file system (NTFS, where the store swaps files with <c>File.Replace</c>), and a
/// server killed at each point of it. The upgrade writes the file's bytes to <c>.v1-*.tmp</c>, flushes them and renames it
/// <c>.v1-*</c>, converts the data in memory, writes the result to <c>.tmp</c> and swaps it in; whatever the kill leaves on
/// disk, the next start ends with the same schema 2 file, one <c>.v1-*</c> copy of the 1.0 file and a <c>.bak</c> of it,
/// and the 1.0 data intact.
/// </summary>
public sealed class BookmarkStoreUpgradeCrashTests : IDisposable
{
    private const string CopyName = "bookmarks.json.v1-20260930031000";

    // The copy a start five minutes later makes when it finds none to reuse.
    private const string RestartCopyName = "bookmarks.json.v1-20260930031500";

    private static readonly DateTimeOffset _firstStart = new(2026, 9, 30, 3, 10, 0, TimeSpan.Zero);

    private readonly TempDirectory _directory = new();
    private readonly byte[] _original = StoreFixtures.Read(StoreFixtures.Bookmarks100);

    private string Folder => _directory.Combine("bookmarks");

    private string StorePath => Path.Combine(Folder, "bookmarks.json");

    [Fact]
    public void Upgrade_OnNtfs_SwapsTheFileWithFileReplace_KeepingTheCopyAndTheBackup()
    {
        RequireNtfs();
        WriteFile(StorePath, _original);

        using (var store = CreateStore(Real(), new ManualTimeProvider(_firstStart)))
        {
            Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        }

        // File.Replace kept the 1.0 file as the .bak; the copy made first is the same bytes; no .tmp is left.
        Assert.Equal(["bookmarks.json", "bookmarks.json.bak", CopyName], FileNames());
        Assert.Equal(_original, File.ReadAllBytes(StorePath + ".bak"));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(Folder, CopyName)));
        Assert.Equal(Upgraded(), File.ReadAllBytes(StorePath));
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllBytes(StorePath))!["Version"]!);
    }

    [Theory]
    [InlineData("an empty copy")]
    [InlineData("half of the copy")]
    [InlineData("the whole copy, not yet renamed")]
    public void KilledWhileTheCopyWasWritten_TheNextStartUpgrades_WithANewCopy_AndDeletesThePartialOne(string left)
    {
        // Until its bytes are on disk the copy is only <copy>.tmp, which no listing shows and no start reuses.
        RequireNtfs();
        WriteFile(StorePath, _original);
        var partial = Path.Combine(Folder, CopyName + ".tmp");
        WriteFile(partial, left switch
        {
            "an empty copy" => [],
            "half of the copy" => _original[..(_original.Length / 2)],
            _ => _original
        });

        var clock = new ManualTimeProvider(_firstStart.AddMinutes(5));
        var logger = new ListLogger();
        using (var restarted = CreateStore(Real(), clock, logger))
        {
            Assert.Empty(restarted.Documents.ListKeptFiles());
            Assert.Equal(StoreStatus.Migrated, restarted.Documents.Status);
            Assert.Equal(10, restarted.GetAll().Count);
            Assert.Equal(RestartCopyName, Assert.Single(restarted.Documents.ListKeptFiles()).Name);
            Assert.Contains(RestartCopyName, restarted.Documents.StatusMessage, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(logger.At(LogLevel.Information), e => e.Message.StartsWith("Reusing", StringComparison.Ordinal));
        AssertRecovered(RestartCopyName);
    }

    [Fact]
    public void KilledAfterTheCopy_BeforeTheUpgradeWasSaved_TheNextStartUpgrades_ReusingTheCopy()
    {
        // The kill is simulated by a step that throws once the store has made the copy.
        RequireNtfs();
        WriteFile(StorePath, _original);
        var clock = new ManualTimeProvider(_firstStart);
        using (var killed = CreateStore(new KilledStep(Real()), clock))
        {
            Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, killed.Documents.Status);
        }

        Assert.Equal(["bookmarks.json", CopyName], FileNames());
        Assert.Equal(_original, File.ReadAllBytes(StorePath));

        clock.Advance(TimeSpan.FromMinutes(5));
        var logger = new ListLogger();
        using (var restarted = CreateStore(Real(), clock, logger))
        {
            Assert.Equal(StoreStatus.Migrated, restarted.Documents.Status);
            Assert.Equal(10, restarted.GetAll().Count);
            Assert.Contains(CopyName, restarted.Documents.StatusMessage, StringComparison.Ordinal);
        }

        Assert.Contains(logger.At(LogLevel.Information), e => e.Message.StartsWith("Reusing", StringComparison.Ordinal));
        AssertRecovered();
    }

    [Theory]
    [InlineData("an empty .tmp")]
    [InlineData("half of the upgraded file in .tmp")]
    [InlineData("the whole upgraded file in .tmp, not swapped in")]
    public void KilledWhileSavingTheUpgrade_TheNextStartUpgradesAgain_AndReplacesTheTempFile(string left)
    {
        RequireNtfs();
        var upgraded = Upgraded();
        WriteFile(StorePath, _original);
        WriteFile(Path.Combine(Folder, CopyName), _original);
        WriteFile(StorePath + ".tmp", left switch
        {
            "an empty .tmp" => [],
            "half of the upgraded file in .tmp" => upgraded[..(upgraded.Length / 2)],
            _ => upgraded
        });

        using (var restarted = CreateStore(Real(), new ManualTimeProvider(_firstStart.AddMinutes(5))))
        {
            Assert.Equal(StoreStatus.Migrated, restarted.Documents.Status);
            Assert.Equal(10, restarted.GetAll().Count);
        }

        AssertRecovered();
    }

    [Fact]
    public void KilledAfterTheSwap_TheNextStartReadsSchema2_AndRunsNoStep()
    {
        RequireNtfs();
        WriteFile(StorePath, Upgraded());
        WriteFile(StorePath + ".bak", _original);
        WriteFile(Path.Combine(Folder, CopyName), _original);

        using (var restarted = CreateStore(new KilledStep(Real()), new ManualTimeProvider(_firstStart.AddMinutes(5))))
        {
            Assert.Equal(StoreStatus.Ok, restarted.Documents.Status);
            Assert.Equal(2, restarted.Documents.LoadedVersion);
            Assert.Equal(10, restarted.GetAll().Count);
        }

        AssertRecovered();
    }

    public void Dispose() => _directory.Dispose();

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void RequireNtfs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS and File.Replace with a backup are Windows'.");
        var format = new DriveInfo(Path.GetPathRoot(_directory.Path)!).DriveFormat;
        Assert.SkipUnless(string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase), "The temp folder isn't on NTFS: " + format);
    }

    private void AssertRecovered(string copyName = CopyName)
    {
        Assert.Equal(["bookmarks.json", "bookmarks.json.bak", copyName], FileNames());
        Assert.Equal(Upgraded(), File.ReadAllBytes(StorePath));
        Assert.Equal(_original, File.ReadAllBytes(StorePath + ".bak"));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(Folder, copyName)));
    }

    private string[] FileNames() => Directory.GetFiles(Folder).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    // The file an uninterrupted upgrade writes, made in a folder of its own with the same step.
    private byte[] Upgraded()
    {
        var reference = _directory.Combine("reference", "bookmarks.json");
        if (!File.Exists(reference))
        {
            WriteFile(reference, _original);
            using var store = new BookmarkStore(BookmarkStore.CreateDocumentStore(reference, [Real()], NullLogger.Instance));
            Assert.Equal(StoreStatus.Migrated, store.Documents.Status);
        }

        return File.ReadAllBytes(reference);
    }

    private BookmarkStoreMigration1To2 Real() => new(_directory.Combine("clips"), () => null, _ => null);

    private BookmarkStore CreateStore(IJsonMigration migration, TimeProvider clock, ILogger? logger = null)
        => new(BookmarkStore.CreateDocumentStore(StorePath, [migration], logger ?? NullLogger.Instance, clock));

    /// <summary>Runs the real step and then dies: the process is killed after the copy, before the save.</summary>
    private sealed class KilledStep(BookmarkStoreMigration1To2 real) : IJsonMigration
    {
        public int FromVersion => real.FromVersion;

        public void Apply(JsonObject root)
        {
            real.Apply(root);
            throw new InvalidOperationException("Killed.");
        }
    }
}
