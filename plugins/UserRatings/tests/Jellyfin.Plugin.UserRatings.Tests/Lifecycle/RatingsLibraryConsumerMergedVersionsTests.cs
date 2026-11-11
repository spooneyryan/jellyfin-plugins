using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The merged-version maintenance step: versions merged while the server was off never reach a batch's
/// step 3, so the daily step moves their ratings to the primary as step 3 would (the conflict rule, and only to a primary
/// the member can open), once. Since 0.3.13 a chain of merged versions is followed to its end (a cycle or a chain longer than
/// <see cref="RatingsLibraryConsumer.MaxVersionHops"/> leaves the rating on its version), and a member the server can't be
/// asked about can open no primary.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    [Fact]
    public async Task TheMergedVersionsStep_MovesTheRatingsOfAVersionMergedWhileTheServerWasOff_UnderTheConflictRule_Once()
    {
        var primary = AddMovie("Heat (1995)", _movies, "949");
        var version = AddMovie("Heat (1995) - 4K", _movies, "949");
        var unmerged = AddMovie("Ronin (1998)", _movies, "8195");
        var alices = await RateAsync(version, _alice, 9, "A great heist.");
        var bobOnPrimary = await RateAsync(primary, _bob, 4, updated: Created.AddHours(1));
        var bobOnVersion = await RateAsync(version, _bob, 8, updated: Created.AddHours(2));
        var onRonin = await RateAsync(unmerged, _alice, 7);

        // Merged with no event (the server was off): only the step sees it.
        version.PrimaryVersionId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        var result = await _consumer.MergeVersionsAsync(Ct);

        Assert.Equal((primary.Id, true), (_fx.Store.Get(alices.Id)!.ItemId, _fx.Store.Get(alices.Id)!.IsAttached));
        Assert.Equal((primary.Id, true), (_fx.Store.Get(bobOnVersion.Id)!.ItemId, _fx.Store.Get(bobOnVersion.Id)!.IsAttached));
        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(bobOnPrimary.Id)!.DetachReason);
        Assert.Equal(unmerged.Id, _fx.Store.Get(onRonin.Id)!.ItemId);
        AssertMemberFieldsKept(alices, _fx.Store.Get(alices.Id)!);
        Assert.Equal((RatingsLibraryConsumer.MergedVersionsStep, 3, 1, 2, 0, 1), ((string)result["Pass"]!, (int)result["Checked"]!, (int)result["MergedItems"]!, (int)result["Moved"]!, (int)result["Stayed"]!, (int)result["Ambiguous"]!));
        var relinks = _fx.Store.GetModerationLog(0, 10).Where(l => l.Action == RatingValues.ModerationAction.Relink).ToList();
        Assert.Equal(2, relinks.Count);
        Assert.All(relinks, l => Assert.Equal((RatingsLibraryConsumer.MergedVersionReason, (Guid?)primary.Id), (l.Reason, l.ItemId)));
        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(1), Assert.Single(_entries).Name);

        // The next day: nothing on the version any more, nothing written, no second entry.
        var generation = _fx.Store.Generation;
        var again = await _consumer.MergeVersionsAsync(Ct);
        Assert.Equal((2, 0, 0), ((int)again["Checked"]!, (int)again["MergedItems"]!, (int)again["Moved"]!));
        Assert.Equal(generation, _fx.Store.Generation);
        Assert.Single(_entries);
    }

    [Fact]
    public async Task TheMergedVersionsStep_LeavesTheRatingOfAMemberWhoCantOpenThePrimary_OnTheVersion()
    {
        var version = AddMovie("Heat (1995) - 4K", _movies, "949");
        var primary = AddMovie("Heat (1995)", _shows, "949");
        _library.Hide(_alice, primary);
        var alices = await RateAsync(version, _alice, 9);
        var bobs = await RateAsync(version, _bob, 6);
        version.PrimaryVersionId = primary.Id.ToString("N", CultureInfo.InvariantCulture);

        var result = await _consumer.MergeVersionsAsync(Ct);

        Assert.Equal((version.Id, true), (_fx.Store.Get(alices.Id)!.ItemId, _fx.Store.Get(alices.Id)!.IsAttached));
        Assert.Equal((primary.Id, true), (_fx.Store.Get(bobs.Id)!.ItemId, _fx.Store.Get(bobs.Id)!.IsAttached));
        Assert.Equal((1, 1, 1, 0), ((int)result["MergedItems"]!, (int)result["Moved"]!, (int)result["Stayed"]!, (int)result["Ambiguous"]!));

        // Stable: alice's stays where she can see it, every day, and nothing is written for it.
        var generation = _fx.Store.Generation;
        var again = await _consumer.MergeVersionsAsync(Ct);
        Assert.Equal((1, 0, 1), ((int)again["MergedItems"]!, (int)again["Moved"]!, (int)again["Stayed"]!));
        Assert.Equal(generation, _fx.Store.Generation);
        Assert.Empty(_entries);
    }

    [Fact]
    public async Task TheMergedVersionsStep_LeavesReadOnlyDataAlone()
    {
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(true);
        var journal = new MoveJournalReader(JournalFile, _logger);
        using var consumer = new RatingsLibraryConsumer(_host.Object, store.Object, _library.Object, _users.Object, journal, () => _verifier, () => _detached, _activity.Object, _logger, _fx.Clock);

        var result = await consumer.MergeVersionsAsync(Ct);

        Assert.Equal((RatingsLibraryConsumer.MergedVersionsStep, "ReadOnly"), ((string)result["Pass"]!, (string)result["Skipped"]!));
    }

    // A chain of merged versions (core 10.11.5 leaves A → B when B is merged into C later): one run lands the
    // rating on C, the end of the chain, not on B for a day.
    [Fact]
    public async Task TheMergedVersionsStep_FollowsAChainToItsEnd_InOneRun()
    {
        var c = AddMovie("Heat (1995)", _movies, "949");
        var b = AddMovie("Heat (1995) - 1080p", _movies, "949");
        var a = AddMovie("Heat (1995) - 720p", _movies, "949");
        var onA = await RateAsync(a, _alice, 9);
        a.PrimaryVersionId = b.Id.ToString("N", CultureInfo.InvariantCulture);
        b.PrimaryVersionId = c.Id.ToString("N", CultureInfo.InvariantCulture);

        var first = await _consumer.MergeVersionsAsync(Ct);

        Assert.Equal((c.Id, true), (_fx.Store.Get(onA.Id)!.ItemId, _fx.Store.Get(onA.Id)!.IsAttached));
        Assert.Equal(1, (int)first["Moved"]!);
        Assert.Equal(0, (int)(await _consumer.MergeVersionsAsync(Ct))["Moved"]!);
    }

    // The chain's end is in a library alice can't open, so her rating stays on its version, not on B.
    [Fact]
    public async Task TheMergedVersionsStep_AppliesA2ToTheChainsEnd()
    {
        var c = AddMovie("Heat (1995)", _shows, "949");
        _library.Hide(_alice, c);
        var b = AddMovie("Heat (1995) - 1080p", _movies, "949");
        var a = AddMovie("Heat (1995) - 720p", _movies, "949");
        var onA = await RateAsync(a, _alice, 9);
        var bobsOnA = await RateAsync(a, _bob, 6);
        a.PrimaryVersionId = b.Id.ToString("N", CultureInfo.InvariantCulture);
        b.PrimaryVersionId = c.Id.ToString("N", CultureInfo.InvariantCulture);

        await _consumer.MergeVersionsAsync(Ct);

        Assert.Equal(a.Id, _fx.Store.Get(onA.Id)!.ItemId);
        Assert.Equal(c.Id, _fx.Store.Get(bobsOnA.Id)!.ItemId);
    }

    // A cycle (A → B → A), or a chain longer than MaxVersionHops, leaves the rating on its version, logged once an item.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheMergedVersionsStep_LeavesTheRatingOnItsVersion_OnACycleOrAChainTooLong_LoggedOnce(bool tooLong)
    {
        var a = AddMovie("Heat (1995) - 720p", _movies, "949");
        var onA = await RateAsync(a, _alice, 9);
        var chain = new List<Movie> { a };
        var length = tooLong ? RatingsLibraryConsumer.MaxVersionHops + 1 : 1;
        for (var i = 0; i < length; i++)
        {
            var next = AddMovie($"Heat (1995) - version {i}", _movies, "949");
            chain[^1].PrimaryVersionId = next.Id.ToString("N", CultureInfo.InvariantCulture);
            chain.Add(next);
        }

        if (!tooLong)
        {
            chain[^1].PrimaryVersionId = a.Id.ToString("N", CultureInfo.InvariantCulture);
        }

        for (var day = 0; day < 2; day++)
        {
            await _consumer.MergeVersionsAsync(Ct);
        }

        Assert.Equal(a.Id, _fx.Store.Get(onA.Id)!.ItemId);
        var warning = Assert.Single(_logger.At(LogLevel.Warning), e => e.Message.Contains(a.Id.ToString(), StringComparison.Ordinal) && e.Message.Contains("merged versions", StringComparison.Ordinal));
        Assert.Contains(tooLong ? "a chain longer than" : "a cycle", warning.Message, StringComparison.Ordinal);
    }

    // A chain exactly MaxVersionHops long is followed to its end.
    [Fact]
    public async Task TheMergedVersionsStep_FollowsAChainOfMaxVersionHops()
    {
        var a = AddMovie("Heat (1995) - 720p", _movies, "949");
        var onA = await RateAsync(a, _alice, 9);
        var last = a;
        for (var i = 0; i < RatingsLibraryConsumer.MaxVersionHops; i++)
        {
            var next = AddMovie($"Heat (1995) - version {i}", _movies, "949");
            last.PrimaryVersionId = next.Id.ToString("N", CultureInfo.InvariantCulture);
            last = next;
        }

        await _consumer.MergeVersionsAsync(Ct);

        Assert.Equal(last.Id, _fx.Store.Get(onA.Id)!.ItemId);
    }

    // A member the server can't be asked about (GetUserById throws) can open nothing: the rating stays on its version.
    [Fact]
    public async Task TheMergedVersionsStep_LeavesTheRatingOnItsVersion_WhenTheServerCantBeAskedAboutItsMember()
    {
        var primary = AddMovie("Heat (1995)", _movies, "949");
        var version = AddMovie("Heat (1995) - 4K", _movies, "949");
        var alices = await RateAsync(version, _alice, 9);
        var bobs = await RateAsync(version, _bob, 6);
        _users.Setup(u => u.GetUserById(_alice.Id)).Throws(new InvalidOperationException("The user store is unavailable."));
        version.PrimaryVersionId = primary.Id.ToString("N", CultureInfo.InvariantCulture);

        await _consumer.MergeVersionsAsync(Ct);

        Assert.Equal(version.Id, _fx.Store.Get(alices.Id)!.ItemId);
        Assert.Equal(primary.Id, _fx.Store.Get(bobs.Id)!.ItemId);
    }
}
