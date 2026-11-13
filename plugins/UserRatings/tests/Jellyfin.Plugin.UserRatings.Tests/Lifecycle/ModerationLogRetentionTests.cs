using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The moderation log's retention (<c>ModerationLogDays</c>): the daily step deletes the rows
/// older than the setting, keeps the rest, and asks for a checkpoint, so the deleted rows leave neither the database nor
/// its write-ahead log; 0 keeps every row; a read-only database is left alone; the setting is held to its range.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ModerationLogRetentionTests : IDisposable
{
    private readonly StoreFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _fx.Clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task TheDailyStep_DeletesTheRowsOlderThanModerationLogDays_KeepsTheRest_AndTheirBytesLeaveTheDatabaseAndItsWal()
    {
        // Rows of a year and more ago, one at the cutoff to the tick (kept: only older rows go), and a recent one; each
        // with a marker in its Details, so the files can be searched for the deleted rows' bytes.
        var markers = new Dictionary<string, DateTime>(StringComparer.Ordinal)
        {
            ["zzRetention-400-days"] = Now.AddDays(-400),
            ["zzRetention-366-days"] = Now.AddDays(-366),
            ["zzRetention-at-cutoff"] = Now.AddDays(-365),
            ["zzRetention-10-days"] = Now.AddDays(-10)
        };
        foreach (var (marker, at) in markers)
        {
            await _fx.Store.AppendModerationLogAsync(Row(at, marker), Ct);
        }

        // In the database file itself before the step, not only in its -wal.
        Assert.True(await _fx.Store.RequestCheckpoint());
        Assert.All(markers.Keys, m => Assert.True(Holds(_fx.File, m), m));
        Assert.Equal(2, _fx.Store.CountModerationLogBefore(Now.AddDays(-365)));

        var report = await Retention(365).RunAsync(Ct);

        Assert.NotNull(report);
        Assert.Equal(365, report!["Days"]);
        Assert.Equal(2, report["Deleted"]);
        Assert.Equal(Now.AddDays(-365), report["CutoffUtc"]);
        Assert.Equal(
            ["zzRetention-10-days", "zzRetention-at-cutoff"],
            _fx.Store.GetModerationLog(0, 50).Select(r => Marker(r.Details)).Order(StringComparer.Ordinal));
        Assert.Equal(0, _fx.Store.CountModerationLogBefore(Now.AddDays(-365)));

        // The step asked for the checkpoint: once it completed the deleted rows' bytes are in neither file (secure_delete
        // overwrote them in the pages the checkpoint copied back), while the kept rows are still there.
        Assert.True(await _fx.Store.WaitForCheckpointAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct), "the retention's checkpoint completed");
        foreach (var file in new[] { _fx.File, _fx.File + "-wal" })
        {
            Assert.False(Holds(file, "zzRetention-400-days"), file);
            Assert.False(Holds(file, "zzRetention-366-days"), file);
        }

        Assert.True(Holds(_fx.File, "zzRetention-10-days"));
        Assert.Contains(_fx.Logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("2 rows older than 365 days", StringComparison.Ordinal));

        // A second run the same day has nothing left to delete, and asks for no checkpoint.
        var again = await Retention(365).RunAsync(Ct);
        Assert.Equal(0, again!["Deleted"]);
    }

    [Fact]
    public async Task ZeroDays_KeepsEveryRow()
    {
        await _fx.Store.AppendModerationLogAsync(Row(Now.AddDays(-3000), "zzRetention-ancient"), Ct);

        var report = await Retention(0).RunAsync(Ct);

        Assert.Equal((0, 0), ((int)report!["Days"]!, (int)report["Deleted"]!));
        Assert.False(report.ContainsKey("CutoffUtc"));
        Assert.Single(_fx.Store.GetModerationLog(0, 50));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99999, 3650)]
    public async Task ADayCountOutOfRange_IsHeldToIt(int setting, int used)
    {
        await _fx.Store.AppendModerationLogAsync(Row(Now.AddDays(-3651), "zzRetention-ten-years"), Ct);

        var report = await Retention(setting).RunAsync(Ct);

        Assert.Equal(used, report!["Days"]);
        Assert.Equal(used == 0 ? 0 : 1, report["Deleted"]);
        var settings = RatingsSettings.From(new PluginConfiguration { ModerationLogDays = setting });
        Assert.Equal(used, settings.ModerationLogDays);
        Assert.Contains(settings.Problems, p => p.StartsWith("ModerationLogDays " + setting + " is outside 0 to 3650", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadOnlyDatabase_IsLeftAlone()
    {
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(true);
        var retention = new ModerationLogRetention(store.Object, () => 365, _fx.Clock, _fx.Logger);

        var report = await retention.RunAsync(Ct);

        Assert.Equal(true, report!["ReadOnly"]);
        Assert.Equal(0, report["Deleted"]);
        store.Verify(s => s.WriteAsync(It.IsAny<Func<IRatingsWriter, int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Every row a restore reads stays while a kept snapshot is older than it (the removals,
    // the envelope imports, Replace or this server's own, and Start empty); a row of another action goes at the cutoff, and
    // one of those older than every snapshot goes too.
    [Fact]
    public async Task TheRowsARestoreReads_StayWhileAKeptSnapshotIsOlder_TheOthersGoAtTheCutoff()
    {
        await _fx.Store.AppendModerationLogAsync(Kind(RatingValues.ModerationAction.StartEmpty, Now.AddHours(-1), "zzRetention-before-the-snapshot"), Ct);
        Assert.NotNull(await _fx.Store.PreOpSnapshotAsync(Kit.Storage.SnapshotOperations.Purge, Ct));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        foreach (var row in RestoreRows(Now))
        {
            await _fx.Store.AppendModerationLogAsync(row, Ct);
        }

        _fx.Clock.Advance(TimeSpan.FromDays(3));
        var report = await Retention(1).RunAsync(Ct);

        Assert.True(report!.ContainsKey("RemovalsCutoffUtc"));
        Assert.Equal(3, report["Deleted"]);
        Assert.Equal(
            ["zzRetention-envelope-own", "zzRetention-envelope-replace", "zzRetention-remove-entry", "zzRetention-remove-text", "zzRetention-start-empty"],
            _fx.Store.GetModerationLog(0, 50).Select(r => Marker(r.Details)).Order(StringComparer.Ordinal));
    }

    // The fallback a change once broke without a test failing: snapshots that can't be listed keep every row a
    // restore reads that run, however old, and the others still go.
    [Fact]
    public async Task WhenTheSnapshotsCantBeListed_EveryRowARestoreReadsStays_TheOthersGo()
    {
        foreach (var row in RestoreRows(Now.AddDays(-400)))
        {
            await _fx.Store.AppendModerationLogAsync(row, Ct);
        }

        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(false);
        store.Setup(s => s.ListSnapshots()).Throws(new System.IO.IOException("The snapshots folder couldn't be listed."));
        store.Setup(s => s.WriteAsync(It.IsAny<Func<IRatingsWriter, int>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<IRatingsWriter, int> work, CancellationToken ct) => _fx.Store.WriteAsync(work, ct));
        store.Setup(s => s.RequestCheckpoint()).Returns(Task.FromResult(true));

        var report = await new ModerationLogRetention(store.Object, () => 365, _fx.Clock, _fx.Logger).RunAsync(Ct);

        Assert.Equal(DateTime.MinValue, report!["RemovalsCutoffUtc"]);
        Assert.Equal(2, report["Deleted"]);
        Assert.Equal(5, _fx.Store.GetModerationLog(0, 50).Count);
        Assert.DoesNotContain(_fx.Store.GetModerationLog(0, 50), r => r.Action is RatingValues.ModerationAction.Archive or RatingValues.ModerationAction.ImportJe);
        Assert.Contains(_fx.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("keeps every row a restore reads", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettingsDefault_Is365_AndTheCutoffIsThatManyDaysAgo()
    {
        Assert.Equal(365, RatingsSettings.From(new PluginConfiguration()).ModerationLogDays);
        Assert.Equal(StoreFixture.T0.AddDays(-365), ModerationLogRetention.CutoffFor(365, StoreFixture.T0));
        Assert.Null(ModerationLogRetention.CutoffFor(0, StoreFixture.T0));
    }

    // One row of each action a restore reads, and two of others (Archive, a Jellyfin Enhanced import).
    private static IEnumerable<ModerationLogRecord> RestoreRows(DateTime at) =>
    [
        Row(at, "zzRetention-remove-text"),
        Kind(RatingValues.ModerationAction.RemoveEntry, at, "zzRetention-remove-entry"),
        Kind(RatingValues.ModerationAction.ImportEnvelope, at, "zzRetention-envelope-replace", "\"Mode\":\"Replace\",\"OwnServer\":false,"),
        Kind(RatingValues.ModerationAction.ImportEnvelope, at, "zzRetention-envelope-own", "\"Mode\":\"Merge\",\"OwnServer\":true,"),
        Kind(RatingValues.ModerationAction.StartEmpty, at, "zzRetention-start-empty"),
        Kind(RatingValues.ModerationAction.Archive, at, "zzRetention-archive"),
        Kind(RatingValues.ModerationAction.ImportJe, at, "zzRetention-import-je")
    ];

    private static ModerationLogRecord Kind(string action, DateTime at, string marker, string details = "")
        => new() { AtUtc = at, Action = action, Details = "{" + details + "\"Marker\":\"" + marker + "\"}" };

    private static ModerationLogRecord Row(DateTime at, string marker)
        => new() { AtUtc = at, Action = RatingValues.ModerationAction.RemoveText, RatingId = Guid.NewGuid(), TargetUserId = Guid.NewGuid(), ItemId = Guid.NewGuid(), Reason = "Spam", Details = "{\"Reviews\":1,\"Marker\":\"" + marker + "\"}" };

    private static string Marker(string? details)
    {
        var text = details ?? string.Empty;
        return text[text.IndexOf("zzRetention", StringComparison.Ordinal)..^2];
    }

    private static bool Holds(string path, string text)
        => System.IO.File.Exists(path) && StoreFixture.ReadOpenFile(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private ModerationLogRetention Retention(int days) => new(_fx.Store, () => days, _fx.Clock, _fx.Logger);
}
