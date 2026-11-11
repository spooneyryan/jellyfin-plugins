using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// One consumer's waiting changes: one change per item and list, the merge rules, the trailing
/// debounce with its maximum wait, and what a batch carries.
/// </summary>
public sealed class LibraryChangeQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = new("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid User1 = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid User2 = new("22222222-0000-0000-0000-000000000002");

    private readonly LibraryChangeQueue _queue = new(new RecordingConsumer("Test"));

    [Fact]
    public void AnItemAddedThenUpdated_StaysAdded_WithTheUpdateReasons()
    {
        Offer(Change(LibraryChangeKind.Added, A));
        Offer(Change(LibraryChangeKind.Updated, A, reason: ItemUpdateType.MetadataImport));
        Offer(Change(LibraryChangeKind.Updated, A, reason: ItemUpdateType.ImageUpdate));

        var batch = _queue.Take(fullPass: false, duringPostScan: false);

        var added = Assert.Single(batch.Added);
        Assert.Equal(LibraryChangeKind.Added, added.Kind);
        Assert.Equal(ItemUpdateType.MetadataImport | ItemUpdateType.ImageUpdate, added.UpdateReason);
        Assert.Empty(batch.Updated);
    }

    [Fact]
    public void RepeatedUpdates_AreOneChange_WithTheReasonsCombined_AndTheLatestFacts()
    {
        var parent = Guid.NewGuid();
        Offer(Change(LibraryChangeKind.Updated, A, reason: ItemUpdateType.MetadataDownload, at: 0));
        Offer(Change(LibraryChangeKind.Updated, A, reason: ItemUpdateType.MetadataEdit, at: 5) with { ParentId = parent });

        var updated = Assert.Single(_queue.Take(false, false).Updated);

        Assert.Equal(ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataEdit, updated.UpdateReason);
        Assert.Equal(parent, updated.ParentId);
        Assert.Equal(T0.AddSeconds(5), updated.Utc);
    }

    [Fact]
    public void AnItemAddedOrUpdated_ThenRemoved_IsOnlyRemoved()
    {
        Offer(Change(LibraryChangeKind.Added, A));
        Offer(Change(LibraryChangeKind.Updated, B));
        Offer(Change(LibraryChangeKind.Removed, A));
        Offer(Change(LibraryChangeKind.Removed, B));

        var batch = _queue.Take(false, false);

        Assert.Empty(batch.Added);
        Assert.Empty(batch.Updated);
        Assert.Equal([A, B], batch.Removed.Select(c => c.ItemId));
    }

    [Fact]
    public void AnItemRemovedAndAddedAgain_IsInBothLists()
    {
        // The same path gives the same id: the consumer detaches, then finds the item again by its id.
        Offer(Change(LibraryChangeKind.Removed, A));
        Offer(Change(LibraryChangeKind.Added, A));

        var batch = _queue.Take(false, false);

        Assert.Equal(A, Assert.Single(batch.Removed).ItemId);
        Assert.Equal(A, Assert.Single(batch.Added).ItemId);
    }

    [Fact]
    public void ARemovedFolder_GoesToRemovedFolders_AndToRemovedOnlyWhenItsListTookIt()
    {
        var folder = Change(LibraryChangeKind.Removed, A, BaseItemKind.MusicArtist) with { IsFolder = true };
        _queue.Offer(folder, toList: false, toRemovedFolders: true);
        _queue.Offer(Change(LibraryChangeKind.Removed, B, BaseItemKind.Series) with { IsFolder = true }, toList: true, toRemovedFolders: true);
        _queue.Offer(Change(LibraryChangeKind.Removed, C), toList: false, toRemovedFolders: false);

        var batch = _queue.Take(false, false);

        Assert.Equal([B], batch.Removed.Select(c => c.ItemId));
        Assert.Equal([A, B], batch.RemovedFolders.Select(c => c.ItemId));
        Assert.Equal(2, batch.Count);
        Assert.Equal([BaseItemKind.MusicArtist, BaseItemKind.Series], batch.Kinds.Order());
    }

    [Fact]
    public void UserData_IsOneChangePerItemAndUser()
    {
        Offer(Change(LibraryChangeKind.UserData, A) with { UserId = User1, SaveReason = UserDataSaveReason.TogglePlayed });
        Offer(Change(LibraryChangeKind.UserData, A) with { UserId = User1, SaveReason = UserDataSaveReason.UpdateUserRating });
        Offer(Change(LibraryChangeKind.UserData, A) with { UserId = User2, SaveReason = UserDataSaveReason.PlaybackFinished });

        var batch = _queue.Take(false, false);

        Assert.Equal(2, batch.UserData.Count);
        Assert.Equal(UserDataSaveReason.UpdateUserRating, batch.UserData.Single(c => c.UserId == User1).SaveReason);
        Assert.Equal(UserDataSaveReason.PlaybackFinished, batch.UserData.Single(c => c.UserId == User2).SaveReason);
    }

    [Fact]
    public void EachList_KeepsTheOrderItsItemsWereFirstSeen()
    {
        Offer(Change(LibraryChangeKind.Added, C, at: 0));
        Offer(Change(LibraryChangeKind.Added, A, at: 1));
        Offer(Change(LibraryChangeKind.Added, B, at: 2));
        Offer(Change(LibraryChangeKind.Updated, C, at: 3));

        Assert.Equal([C, A, B], _queue.Take(false, false).Added.Select(c => c.ItemId));
    }

    [Fact]
    public void TheBatch_IsDueAfterTheQuietPeriod_OrAtTheLatestAfterTheMaximumWait()
    {
        var queue = new LibraryChangeQueue(new RecordingConsumer("Timing", new LibraryChangeFilter { QuietPeriod = TimeSpan.FromSeconds(60), MaxWait = TimeSpan.FromMinutes(10) }));
        Assert.Null(queue.DueAt());

        queue.Offer(Change(LibraryChangeKind.Updated, A, at: 0), true, false);
        Assert.Equal(T0.AddSeconds(60), queue.DueAt());

        // Every change moves the quiet period on...
        queue.Offer(Change(LibraryChangeKind.Updated, B, at: 30), true, false);
        Assert.Equal(T0.AddSeconds(90), queue.DueAt());

        // ...but never past the maximum wait after the first change.
        queue.Offer(Change(LibraryChangeKind.Updated, C, at: 590), true, false);
        Assert.Equal(T0.AddMinutes(10), queue.DueAt());

        var batch = queue.Take(false, false);
        Assert.Equal(T0, batch.FirstUtc);
        Assert.Equal(T0.AddSeconds(590), batch.LastUtc);
        Assert.Null(queue.DueAt());
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void AChangeTheQueueDoesntTake_StartsNoTimer()
    {
        _queue.Offer(Change(LibraryChangeKind.Removed, A), toList: false, toRemovedFolders: false);

        Assert.Null(_queue.DueAt());
        Assert.True(_queue.IsEmpty);
    }

    [Fact]
    public void AnEmptyTake_IsAnEmptyBatch_WithItsFlags()
    {
        var batch = _queue.Take(fullPass: true, duringPostScan: true);

        Assert.True(batch.IsEmpty);
        Assert.Equal(0, batch.Count);
        Assert.True(batch.FullPass);
        Assert.True(batch.DuringPostScan);
        Assert.Empty(batch.Kinds);
        Assert.Null(batch.FirstUtc);
    }

    [Fact]
    public void AChangeAfterTheFullPass_MarksTheWaitingChanges_UntilTheyAreTaken()
    {
        Offer(Change(LibraryChangeKind.Added, A));
        Assert.False(_queue.HasAfterFullPass);

        _queue.Offer(Change(LibraryChangeKind.Removed, B, BaseItemKind.MusicArtist) with { IsFolder = true }, toList: false, toRemovedFolders: true, afterFullPass: true);
        Assert.True(_queue.HasAfterFullPass);
        Assert.Equal(true, ((System.Collections.Generic.IDictionary<string, object?>)_queue.Describe()["Pending"]!)["AfterFullPass"]);
        var batch = _queue.Take(fullPass: false, duringPostScan: false);

        Assert.True(batch.AfterFullPass);
        Assert.Equal([A], batch.Added.Select(c => c.ItemId));
        Assert.Equal([B], batch.RemovedFolders.Select(c => c.ItemId));
        Assert.False(_queue.HasAfterFullPass);

        // A full pass takes marked changes as part of itself; a change the queue doesn't take marks nothing.
        _queue.Offer(Change(LibraryChangeKind.Added, C), toList: true, toRemovedFolders: false, afterFullPass: true);
        var full = _queue.Take(fullPass: true, duringPostScan: true);
        Assert.False(full.AfterFullPass);
        Assert.Equal([C], full.Added.Select(c => c.ItemId));
        _queue.Offer(Change(LibraryChangeKind.Removed, A), toList: false, toRemovedFolders: false, afterFullPass: true);
        Assert.False(_queue.HasAfterFullPass);
        Assert.False(_queue.Take(false, false).AfterFullPass);

        _queue.Record(batch, T0, TimeSpan.Zero, scanRunning: false, null);
        var recent = Assert.IsType<System.Collections.Generic.List<System.Collections.Generic.IDictionary<string, object?>>>(_queue.Describe()["Recent"]);
        Assert.Equal(true, Assert.Single(recent)["AfterFullPass"]);
        Assert.Throws<ArgumentException>(() => new LibraryChangeBatch([], [], [], [], [], fullPass: true, duringPostScan: true, afterFullPass: true));
    }

    [Fact]
    public void Deferrals_AreCountedOncePerBatch()
    {
        Offer(Change(LibraryChangeKind.Added, A));

        Assert.True(_queue.NoteDeferral());
        Assert.False(_queue.NoteDeferral());
        _queue.Take(false, false);
        Offer(Change(LibraryChangeKind.Added, B));
        Assert.True(_queue.NoteDeferral());

        Assert.Equal(2, _queue.Deferrals);
    }

    [Fact]
    public void Delays_AreClampedToSaneValues()
    {
        var odd = new LibraryChangeFilter { QuietPeriod = TimeSpan.FromMinutes(-1), MaxWait = TimeSpan.FromDays(3) };
        Assert.Equal((TimeSpan.Zero, LibraryChangeFilter.MaxDelay), odd.EffectiveDelays());

        var inverted = new LibraryChangeFilter { QuietPeriod = TimeSpan.FromMinutes(5), MaxWait = TimeSpan.FromMinutes(1) };
        Assert.Equal((TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)), inverted.EffectiveDelays());

        var defaults = new LibraryChangeFilter();
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10)), defaults.EffectiveDelays());
        Assert.True(defaults.LibraryItemsOnly);
        Assert.True(defaults.CaptureIdentity);
        Assert.False(defaults.IncludeUserData);
        Assert.False(defaults.CaptureTopParent);
        Assert.Null(defaults.Kinds);
    }

    [Fact]
    public void Describe_ListsTheWaitingChanges_TheCounters_AndTheLatestBatchesNewestFirst()
    {
        Offer(Change(LibraryChangeKind.Added, A));
        var waiting = _queue.Describe();
        Assert.Equal("Test", waiting["Name"]);
        Assert.Equal(1, ((System.Collections.Generic.IDictionary<string, object?>)waiting["Pending"]!)["Added"]);

        for (var i = 0; i < LibraryChangeQueue.MaxRecent + 2; i++)
        {
            _queue.Record(_queue.Take(fullPass: i % 2 == 0, duringPostScan: i % 2 == 0), T0.AddMinutes(i), TimeSpan.FromMilliseconds(3), scanRunning: i % 2 == 0, i == 3 ? new InvalidOperationException("boom") : null);
        }

        var described = _queue.Describe();
        var recent = Assert.IsType<System.Collections.Generic.List<System.Collections.Generic.IDictionary<string, object?>>>(described["Recent"]);
        Assert.Equal(LibraryChangeQueue.MaxRecent, recent.Count);
        Assert.Equal(T0.AddMinutes(11).UtcDateTime, recent[0]["Utc"]);
        Assert.Equal(12L, described["Batches"]);
        Assert.Equal(6L, described["FullPasses"]);
        Assert.Equal(1L, described["Failures"]);
        Assert.Equal("InvalidOperationException: boom", described["LastError"]);
        Assert.Equal(T0.AddMinutes(10).UtcDateTime, described["LastFullPassUtc"]);
    }

    private static ItemChange Change(LibraryChangeKind kind, Guid id, BaseItemKind? itemKind = BaseItemKind.Movie, ItemUpdateType reason = default, int at = 0)
        => new() { Kind = kind, ItemId = id, ItemKind = itemKind, UpdateReason = reason, Utc = T0.AddSeconds(at) };

    private void Offer(ItemChange change) => _queue.Offer(change, toList: true, toRemovedFolders: false);
}
