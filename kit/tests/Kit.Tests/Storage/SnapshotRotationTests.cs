using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Storage;

/// <summary>
/// Snapshot names, listing and rotation: only the store's own files, newest first, UTC time stamps.
/// </summary>
public sealed class SnapshotRotationTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Fact]
    public void NewPath_IsNamedAfterTheStoreAndTheUtcTime_WithASuffixWhenTaken()
    {
        var utc = new DateTime(2026, 9, 29, 3, 10, 5, DateTimeKind.Utc);

        var first = SnapshotRotation.NewPath(_directory.Path, "bookmarks", ".json", utc);
        File.WriteAllText(first, "{}");
        var second = SnapshotRotation.NewPath(_directory.Path, "bookmarks", ".json", utc);
        File.WriteAllText(second, "{}");
        var third = SnapshotRotation.NewPath(_directory.Path, "bookmarks", ".json", utc);

        Assert.Equal("bookmarks-20260929-031005.json", Path.GetFileName(first));
        Assert.Equal("bookmarks-20260929-031005-1.json", Path.GetFileName(second));
        Assert.Equal("bookmarks-20260929-031005-2.json", Path.GetFileName(third));
    }

    [Fact]
    public void List_ReturnsOnlyTheStoresSnapshots_NewestFirst()
    {
        foreach (var name in new[]
        {
            "bookmarks-20260101-000000.json",
            "bookmarks-20260301-000000.json",
            "bookmarks-20260301-000000-1.json",
            "bookmarks-20260201-120000.json",
            "bookmarks-state-20260401-000000.json",
            "auto-bookmarks-20260401-000000.json",
            "bookmarks-20260401-000000.db",
            "bookmarks-2026041-000000.json",
            "bookmarks-20260401-000000.json.tmp",
            "notes.txt"
        })
        {
            _directory.CreateFile(name, "{}");
        }

        var snapshots = SnapshotRotation.List(_directory.Path, "bookmarks", ".json");

        Assert.Equal(
            new[] { "bookmarks-20260301-000000-1.json", "bookmarks-20260301-000000.json", "bookmarks-20260201-120000.json", "bookmarks-20260101-000000.json" },
            snapshots.Select(s => s.Name));
        Assert.Equal(new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), snapshots[2].Utc);
        Assert.Equal(DateTimeKind.Utc, snapshots[2].Utc.Kind);
        Assert.Equal(1, snapshots[0].Sequence);
        Assert.Equal(2, snapshots[0].Size);
        Assert.Single(SnapshotRotation.List(_directory.Path, "bookmarks", ".db"));
        Assert.Empty(SnapshotRotation.List(_directory.Combine("missing"), "bookmarks", ".json"));
    }

    [Fact]
    public void Rotate_DeletesTheOldest_AndNothingElse()
    {
        foreach (var name in new[] { "s-20260101-000000.json", "s-20260102-000000.json", "s-20260103-000000.json", "s-20260104-000000.json", "t-20260101-000000.json" })
        {
            _directory.CreateFile(name, "{}");
        }

        var deleted = SnapshotRotation.Rotate(_directory.Path, "s", ".json", 2, NullLogger.Instance);

        Assert.Equal(new[] { "s-20260101-000000.json", "s-20260102-000000.json" }, deleted.Select(Path.GetFileName).Order());
        Assert.Equal(
            new[] { "s-20260103-000000.json", "s-20260104-000000.json", "t-20260101-000000.json" },
            Directory.GetFiles(_directory.Path).Select(Path.GetFileName).Order());
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapshotRotation.Rotate(_directory.Path, "s", ".json", 0, NullLogger.Instance));
    }

    [Fact]
    public void Rotate_GivenNow_NeitherDeletesNorCountsSnapshotsDatedMoreThanADayLater()
    {
        // The JSON store's verification, V3: a name dated in the future says nothing about the snapshot's age. Within a day
        // of now it still counts as now (the restore at load's tolerance).
        foreach (var name in new[] { "s-20260101-000000.json", "s-20260102-000000.json", "s-20260103-000000.json", "s-20260104-235959.json", "s-20260201-000000.json" })
        {
            _directory.CreateFile(name, "{}");
        }

        var now = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var deleted = SnapshotRotation.Rotate(_directory.Path, "s", ".json", 2, NullLogger.Instance, nowUtc: now);

        Assert.Equal(new[] { "s-20260101-000000.json", "s-20260102-000000.json" }, deleted.Select(Path.GetFileName).Order());
        Assert.Equal(
            new[] { "s-20260103-000000.json", "s-20260104-235959.json", "s-20260201-000000.json" },
            Directory.GetFiles(_directory.Path).Select(Path.GetFileName).Order());
        var listed = SnapshotRotation.List(_directory.Path, "s", ".json");
        Assert.True(SnapshotRotation.IsFutureDated(listed[0], now));
        Assert.False(SnapshotRotation.IsFutureDated(listed[1], now));

        // The pre-operation class the same way.
        var preOp = SnapshotRotation.PreOpDirectory(_directory.Path);
        Directory.CreateDirectory(preOp);
        foreach (var name in new[] { "s-import-20260101-000000.json", "s-import-20260102-000000.json", "s-import-20260301-000000.json" })
        {
            File.WriteAllText(Path.Combine(preOp, name), "{}");
        }

        Assert.Equal(new[] { "s-import-20260101-000000.json" }, SnapshotRotation.RotatePreOp(_directory.Path, "s", ".json", 1, NullLogger.Instance, nowUtc: now).Select(Path.GetFileName));
        Assert.Equal(new[] { "s-import-20260102-000000.json", "s-import-20260301-000000.json" }, Directory.GetFiles(preOp).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Rotate_PositionalKeepBeforeAndNow_KeepTheirMeaning_AndCompanionSuffixesGoWithADeletedSnapshot()
    {
        // The JSON store passes keepBeforeUtc and nowUtc by position, and the SQLite database adds companionSuffixes
        // after them. Both stores' rules hold in one call: the snapshots before the marker kept, a future-dated name
        // neither deleted nor counted, and a deleted snapshot's -wal, -shm and -journal deleted with it.
        foreach (var name in new[] { "s-20260101-000000.db", "s-20260105-000000.db", "s-20260106-000000.db", "s-20260107-000000.db", "s-20260301-000000.db" })
        {
            _directory.CreateFile(name, "x");
        }

        foreach (var sidecar in new[] { "s-20260101-000000.db-wal", "s-20260105-000000.db-wal", "s-20260105-000000.db-shm", "s-20260105-000000.db-journal", "s-20260301-000000.db-wal" })
        {
            _directory.CreateFile(sidecar, "x");
        }

        var keepBefore = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 1, 7, 12, 0, 0, DateTimeKind.Utc);
        var suffixes = new[] { "-wal", "-shm", "-journal" };

        // After the marker the newest two stay; the 2026-03-01 one (dated in the future) isn't counted.
        var deleted = SnapshotRotation.Rotate(_directory.Path, "s", ".db", 2, NullLogger.Instance, keepBefore, now, suffixes);

        Assert.Equal(new[] { "s-20260105-000000.db" }, deleted.Select(Path.GetFileName));
        Assert.Equal(
            new[] { "s-20260101-000000.db", "s-20260101-000000.db-wal", "s-20260106-000000.db", "s-20260107-000000.db", "s-20260301-000000.db", "s-20260301-000000.db-wal" },
            Directory.GetFiles(_directory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // The pre-operation class: spare, then keepBeforeUtc and nowUtc by position, the suffixes after them.
        var preOp = SnapshotRotation.PreOpDirectory(_directory.Path);
        Directory.CreateDirectory(preOp);
        foreach (var name in new[] { "s-import-20260101-000000.db", "s-import-20260103-000000.db", "s-import-20260103-000000.db-wal", "s-import-20260104-000000.db", "s-import-20260105-000000.db", "s-import-20260401-000000.db" })
        {
            File.WriteAllText(Path.Combine(preOp, name), "x");
        }

        var preOpDeleted = SnapshotRotation.RotatePreOp(_directory.Path, "s", ".db", 1, NullLogger.Instance, "s-import-20260104-000000.db", keepBefore, now, suffixes);

        Assert.Equal(new[] { "s-import-20260103-000000.db" }, preOpDeleted.Select(Path.GetFileName));
        Assert.Equal(
            new[] { "s-import-20260101-000000.db", "s-import-20260104-000000.db", "s-import-20260105-000000.db", "s-import-20260401-000000.db" },
            Directory.GetFiles(preOp).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rotate_GivenKeepNames_NeitherDeletesNorCountsThem_InEitherClass()
    {
        // The JSON store hardening: the kept set is the snapshots there were at the incident, by name (any case),
        // whatever their dates; the others rotate to their own count.
        foreach (var name in new[] { "s-20260101-000000.json", "s-20260102-000000.json", "s-20260103-000000.json", "s-20260104-000000.json", "s-20260105-000000.json" })
        {
            _directory.CreateFile(name, "{}");
        }

        var kept = new[] { "S-20260101-000000.JSON", "s-20260104-000000.json" };
        var deleted = SnapshotRotation.Rotate(_directory.Path, "s", ".json", 1, NullLogger.Instance, keepNames: kept);

        Assert.Equal(new[] { "s-20260102-000000.json", "s-20260103-000000.json" }, deleted.Select(Path.GetFileName).Order());
        Assert.Equal(new[] { "s-20260101-000000.json", "s-20260104-000000.json", "s-20260105-000000.json" }, Directory.GetFiles(_directory.Path).Select(Path.GetFileName).Order());

        var preOp = SnapshotRotation.PreOpDirectory(_directory.Path);
        Directory.CreateDirectory(preOp);
        foreach (var name in new[] { "s-import-20260101-000000.json", "s-restore-20260102-000000.json", "s-other-20260103-000000.json" })
        {
            File.WriteAllText(Path.Combine(preOp, name), "{}");
        }

        Assert.Equal(new[] { "s-restore-20260102-000000.json" }, SnapshotRotation.RotatePreOp(_directory.Path, "s", ".json", 1, NullLogger.Instance, keepNames: ["s-import-20260101-000000.json"]).Select(Path.GetFileName));
        Assert.Equal(new[] { "s-import-20260101-000000.json", "s-other-20260103-000000.json" }, Directory.GetFiles(preOp).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void RotatePreOp_KeepWithin_KeepsEveryOneOfThatTime_UpToTheCap_AndTheNewestAlways()
    {
        // A burst of ten operations keeps all ten (taken within 7 days); older ones go down to the newest `keep`; the
        // cap counts every kept one, the newest first.
        var preOp = SnapshotRotation.PreOpDirectory(_directory.Path);
        Directory.CreateDirectory(preOp);
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var burst = Enumerable.Range(0, 10).Select(i => $"s-user-deleted-20261101-1100{i:00}.db").ToList();
        var old = new[] { "s-remap-20261028-000000.db", "s-remap-20261029-000000.db" };
        foreach (var name in burst.Concat(old))
        {
            File.WriteAllText(Path.Combine(preOp, name), "x");
        }

        Assert.Empty(SnapshotRotation.RotatePreOp(_directory.Path, "s", ".db", 5, NullLogger.Instance, nowUtc: now, keepWithin: TimeSpan.FromDays(7)));
        Assert.Equal(12, Directory.GetFiles(preOp).Length);

        // Four days later the two old ones are older than 7 days (and past the newest five): they go; the burst stays.
        Assert.Equal(old, SnapshotRotation.RotatePreOp(_directory.Path, "s", ".db", 5, NullLogger.Instance, nowUtc: now.AddDays(4), keepWithin: TimeSpan.FromDays(7)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(burst, Directory.GetFiles(preOp).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // The cap: at most 8 of them, newest first.
        Assert.Equal(burst.Take(2), SnapshotRotation.RotatePreOp(_directory.Path, "s", ".db", 5, NullLogger.Instance, nowUtc: now, keepWithin: TimeSpan.FromDays(7), keepAtMost: 8).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // Without keepWithin, the newest five, as before.
        Assert.Equal(3, SnapshotRotation.RotatePreOp(_directory.Path, "s", ".db", 5, NullLogger.Instance, nowUtc: now).Count);
        Assert.Equal(burst.Skip(5), Directory.GetFiles(preOp).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RotatePreOp_KeepWithin_KeepsNoneOlder_NotEvenTheNewestKeep_ButTheSpareTheKeptAndTheFutureDated()
    {
        // Kept by age, a pre-operation copy expires whatever happens (the daily snapshots are the safety net); the one
        // being restored, the incident's kept set and one dated in the future stay.
        var preOp = SnapshotRotation.PreOpDirectory(_directory.Path);
        Directory.CreateDirectory(preOp);
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var burst = Enumerable.Range(0, 8).Select(i => $"s-user-deleted-20261101-1100{i:00}.db").ToList();
        const string Future = "s-remap-20261201-000000.db";
        foreach (var name in burst.Append(Future))
        {
            File.WriteAllText(Path.Combine(preOp, name), "x");
        }

        Assert.Empty(SnapshotRotation.RotatePreOp(_directory.Path, "s", ".db", 5, NullLogger.Instance, nowUtc: now, keepWithin: TimeSpan.FromDays(7)));

        var deleted = SnapshotRotation.RotatePreOp(_directory.Path, "s", ".db", 5, NullLogger.Instance, spare: burst[0], nowUtc: now.AddDays(8), keepNames: [burst[1]], keepWithin: TimeSpan.FromDays(7));

        Assert.Equal(6, deleted.Count);
        Assert.Equal(new[] { burst[0], burst[1], Future }.Order(StringComparer.Ordinal), Directory.GetFiles(preOp).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rotate_LogsAndKeepsASnapshotItCannotDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs Windows file sharing to make the delete fail.");
        }

        var old = _directory.CreateFile("s-20260101-000000.json", "{}");
        _directory.CreateFile("s-20260102-000000.json", "{}");
        var logger = new ListLogger();

        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(SnapshotRotation.Rotate(_directory.Path, "s", ".json", 1, logger));
        }

        Assert.True(File.Exists(old));
        Assert.Single(logger.At(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    public void Dispose() => _directory.Dispose();
}
