using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The archive, on the same fixture: a rating detached
/// longer than <c>DetachedRetentionDays</c> moves into the kit's archive whole and leaves the live table without a tombstone,
/// and the Reattach step puts it back, as it was, on its item found again (under the conflict rule when its member rated
/// that item since). A retention of 0 keeps it live, a held rating is never archived, and a rating relinked before the step
/// stays live.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    [Fact]
    public async Task ADetachedRating_OlderThanTheRetention_MovesToTheArchiveWhole_AndComesBackAsItWasWhenItsItemDoes()
    {
        var (heat, onHeat) = await DetachedHeatAsync(retentionDays: 90);
        var detached = _fx.Store.Get(onHeat.Id)!;

        _fx.Clock.Advance(TimeSpan.FromDays(89));
        var early = await _detached.ArchiveDueAsync(_consumer, Ct);
        Assert.Equal((null, 0), (early.Skipped, early.Archived));
        Assert.NotNull(_fx.Store.Get(onHeat.Id));

        _fx.Clock.Advance(TimeSpan.FromDays(2));
        var due = await _detached.ArchiveDueAsync(_consumer, Ct);
        Assert.Equal(1, due.Archived);
        Assert.Null(_fx.Store.Get(onHeat.Id));
        Assert.Null(_fx.Store.GetTombstone(onHeat.Id));
        Assert.False(_consumer.ContainsRecord(onHeat.Id.ToString()));
        Assert.Empty(_consumer.GetDetached());
        var line = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Equal((onHeat.Id.ToString(), (Guid?)_alice.Id, heat.Id), (line.Id, line.UserId, line.Identity.ItemId));
        Assert.Equal(detached, ArchivedRating.FromArchive(line)! with { Identity = detached.Identity });
        Assert.Equal((detached.Identity.Path, detached.Identity.Kind), (line.Identity.Path, line.Identity.Kind));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {onHeat.Id} archived", StringComparison.Ordinal));

        // Heat back in its library under another folder (another id, the same TMDb id): the Reattach step puts the rating back.
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        var reattach = await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        Assert.Equal(1, reattach.Relinked);
        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((back.Id, true), (after.ItemId, after.IsAttached));
        AssertMemberFieldsKept(onHeat, after);
        Assert.Empty(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {onHeat.Id} moved from item {heat.Id} to item {back.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARetentionOf0_KeepsDetachedRatingsLive()
    {
        var (_, onHeat) = await DetachedHeatAsync(retentionDays: 0);

        _fx.Clock.Advance(TimeSpan.FromDays(400));
        var run = await _detached.ArchiveDueAsync(_consumer, Ct);

        Assert.Equal("RetentionOff", run.Skipped);
        Assert.False(_fx.Store.Get(onHeat.Id)!.IsAttached);
        Assert.False(File.Exists(_detached.ArchiveOf(_consumer).FilePath));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(90, 90)]
    [InlineData(int.MaxValue, RatingsLibraryConsumer.MaxRetentionDays)]
    public void DetachedRetentionDays_IsTheSetting_ReadAtEveryCall_WithinItsBounds(int setting, int expected)
    {
        Create();
        _retentionDays = setting;
        Assert.Equal(expected, _consumer.DetachedRetentionDays);
    }

    // The guard holds Heat and Ronin (every rated item of Movies 2 gone at once): held ratings are attached, so no archive.
    [Fact]
    public async Task AGuardHeldRating_IsNeverArchived_HoweverLongItIsHeld()
    {
        _retentionDays = 90;
        var (heat, _, _, onHeat, _) = await GuardHeldWithACopyElsewhereAsync();

        _fx.Clock.Advance(TimeSpan.FromDays(400));
        var run = await _detached.ArchiveDueAsync(_consumer, Ct);

        Assert.Equal(0, run.Archived);
        Assert.Equal((heat.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        Assert.Contains(onHeat.Id, HeldRatingIds(heat.Id));
        Assert.False(File.Exists(_detached.ArchiveOf(_consumer).FilePath));
    }

    // Alice rated the film again on its new item after the old rating was archived, and the archived one is newer: the
    // conflict rule decides, as for a detached rating; her rating on the item is live again as Ambiguous, so nothing is lost
    // and the archive's copy is done with.
    [Fact]
    public async Task ANewerArchivedRating_WhoseMemberRatedItsItemSince_ComesBackUnderTheConflictRule()
    {
        var (_, onHeat) = await DetachedHeatAsync(retentionDays: 90, updated: Created.AddDays(20));
        _fx.Clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal(1, (await _detached.ArchiveDueAsync(_consumer, Ct)).Archived);
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        var again = await RateAsync(back, _alice, stars2: 4, updated: Created.AddDays(10));
        _library.RaiseAdded(back);

        await _detached.ReattachFromArchiveAsync(_consumer, Ct);

        Assert.Equal((back.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(again.Id)!.DetachReason);
        Assert.Empty(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Single(_entries);
        Assert.Equal(0, (int)Lifecycle("ArchiveSuperseded")["Count"]!);
    }

    // The same with the archived rating older: back, it would only lose the conflict rule,
    // be detached as Ambiguous with its old detach time, archived again the next day and restored again, a line or two of
    // review text more each day. It stays in the archive, counted in Diagnostics, and the archive doesn't grow in 3 days.
    [Fact]
    public async Task AnOlderArchivedRating_WhoseMemberRatedItsItemSince_StaysArchived_AndDoesntPingPongDaily()
    {
        var (_, onHeat) = await DetachedHeatAsync(retentionDays: 90, updated: Created.AddDays(1));
        _fx.Clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal(1, (await _detached.ArchiveDueAsync(_consumer, Ct)).Archived);
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        var again = await RateAsync(back, _alice, stars2: 4, updated: Created.AddDays(10));
        _library.RaiseAdded(back);

        await _detached.ReattachFromArchiveAsync(_consumer, Ct);

        Assert.Null(_fx.Store.Get(onHeat.Id));
        Assert.Equal((back.Id, true, (string?)null), (_fx.Store.Get(again.Id)!.ItemId, _fx.Store.Get(again.Id)!.IsAttached, _fx.Store.Get(again.Id)!.DetachReason));
        Assert.Equal(onHeat.Id.ToString("D", CultureInfo.InvariantCulture), Assert.Single(_detached.ArchiveOf(_consumer).Read().Records).Id);
        Assert.Empty(_entries);
        var superseded = Lifecycle("ArchiveSuperseded");
        Assert.Equal((1, Now), ((int)superseded["Count"]!, (DateTime)superseded["LastRunUtc"]!));

        var lines = (await File.ReadAllLinesAsync(_detached.ArchiveOf(_consumer).FilePath, Ct)).Length;
        var archivedAgain = 0;
        for (var day = 0; day < 3; day++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            archivedAgain += (await _detached.ArchiveDueAsync(_consumer, Ct)).Archived;
            await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        }

        Assert.Equal((0, lines), (archivedAgain, (await File.ReadAllLinesAsync(_detached.ArchiveOf(_consumer).FilePath, Ct)).Length));
        Assert.Null(_fx.Store.Get(onHeat.Id));
        Assert.Empty(_entries);
        Assert.Equal(1, (int)Lifecycle("ArchiveSuperseded")["Count"]!);
    }

    // A rating relinked between the list and the write (its item back under its own id, or detached again later) stays
    // live: only a rating still detached as it was listed is archived. The store lists it, then the change lands.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARatingNoLongerDetachedAsListed_IsNotArchived(bool attachedSince)
    {
        var (heat, onHeat) = await DetachedHeatAsync(retentionDays: 90);
        _fx.Clock.Advance(TimeSpan.FromDays(91));
        var now = _fx.Clock.GetUtcNow().UtcDateTime;
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(false);
        store.Setup(s => s.GetDetached()).Returns(() =>
        {
            var listed = _fx.Store.GetDetached();
            var row = _fx.Store.Get(onHeat.Id)!;
            _fx.Store.UpsertAsync(attachedSince ? row with { DetachedUtc = null, DetachReason = null, AttachedUtc = now } : row with { DetachedUtc = now.AddDays(-1) }, Ct).GetAwaiter().GetResult();
            return listed;
        });
        store.Setup(s => s.WriteAsync(It.IsAny<Func<IRatingsWriter, IReadOnlyList<ArchivedRecord>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<IRatingsWriter, IReadOnlyList<ArchivedRecord>> work, CancellationToken ct) => _fx.Store.WriteAsync(work, ct));
        var journal = new MoveJournalReader(JournalFile, _logger);
        using var consumer = new RatingsLibraryConsumer(_host.Object, store.Object, _library.Object, _users.Object, journal, () => _verifier, () => _detached, _activity.Object, _logger, _fx.Clock, () => 90);

        var appended = 0;
        var archived = await consumer.ArchiveAsync(now.AddDays(-90), r => appended += r.Count, Ct);

        Assert.Empty(archived);
        Assert.Equal(0, appended);
        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((heat.Id, attachedSince), (after.ItemId, after.IsAttached));
    }

    // The archive's write failing leaves the rating in the live table (the append runs inside the store's transaction).
    [Fact]
    public async Task AnArchiveThatCantBeWritten_LeavesTheRatingLive()
    {
        var (_, onHeat) = await DetachedHeatAsync(retentionDays: 90);
        _fx.Clock.Advance(TimeSpan.FromDays(91));

        await Assert.ThrowsAsync<IOException>(() => _consumer.ArchiveAsync(_fx.Clock.GetUtcNow().UtcDateTime, _ => throw new IOException("disk full"), Ct));

        Assert.False(_fx.Store.Get(onHeat.Id)!.IsAttached);
    }

    // An archived rating back on an item merged into a primary its member can't open stays on the version;
    // the next test: on one the member can open, it lands on the primary.
    [Fact]
    public async Task ARestoreFromTheArchive_OntoAMergedVersion_LandsOnThePrimaryOnlyWhenTheMemberCanOpenIt()
    {
        var (heat, onHeat) = await ArchivedHeatAsync();
        var primary = AddMovie("Heat (1995) primary", _shows, "949");
        _library.Hide(_alice, primary);
        heat.PrimaryVersionId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        _library.Add(heat);
        _library.RaiseAdded(heat);

        await _detached.ReattachFromArchiveAsync(_consumer, Ct);

        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((heat.Id, true), (after.ItemId, after.IsAttached));
        Assert.Empty(_detached.ArchiveOf(_consumer).Read().Records);
    }

    [Fact]
    public async Task ARestoreFromTheArchive_OntoAMergedVersion_LandsOnThePrimaryTheMemberCanOpen()
    {
        var (heat, onHeat) = await ArchivedHeatAsync();
        var primary = AddMovie("Heat (1995) primary", _movies, "949");
        heat.PrimaryVersionId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        _library.Add(heat);
        _library.RaiseAdded(heat);

        await _detached.ReattachFromArchiveAsync(_consumer, Ct);

        Assert.Equal((primary.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
    }

    // Alice's review of Heat, archived after 91 days detached.
    private async Task<(Movie Heat, RatingRecord OnHeat)> ArchivedHeatAsync()
    {
        var (heat, onHeat) = await DetachedHeatAsync(retentionDays: 90);
        _fx.Clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal(1, (await _detached.ArchiveDueAsync(_consumer, Ct)).Archived);
        Assert.Null(_fx.Store.Get(onHeat.Id));
        return (heat, onHeat);
    }

    // Alice's review of Heat in Movies, and Ronin there rated too, so Heat's removal alone is an ordinary detach (Removed).
    private async Task<(Movie Heat, RatingRecord OnHeat)> DetachedHeatAsync(int retentionDays, DateTime? updated = null)
    {
        _retentionDays = retentionDays;
        var heat = AddMovie("Heat (1995)", _movies, "949");
        var ronin = AddMovie("Ronin (1998)", _movies, "8195");
        Create();
        var monitor = Monitor();
        var onHeat = await RateAsync(heat, _alice, stars2: 9, text: "Synthetic review of the heist.", updated: updated);
        await RateAsync(ronin, _bob, stars2: 6);
        _library.Remove(heat);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(RatingValues.DetachReason.Removed, _fx.Store.Get(onHeat.Id)!.DetachReason);
        return (heat, onHeat);
    }
}
