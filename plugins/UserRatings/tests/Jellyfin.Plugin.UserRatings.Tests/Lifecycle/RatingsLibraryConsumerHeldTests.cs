using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The fixes of 0.3.11.0, on the same fixture. A rating the mass-detach guard holds moves only to an item in
/// the library folder it was held in (a copy of the film in another library, which the member can open, leaves it held),
/// never through the conflict rule (the member's own rating on the copy stays, and nothing is marked Ambiguous; the
/// same inside its folder, and for a former member's), and is released from its hold only once it moved; a copy the member
/// can't open leaves it held; and everything is where it was once the original is back. A former member's held
/// rating, which the kit resolves without a view, is held to its folder too since 0.3.13.
/// A relink or a merge lands on an item's primary version only when the rating's member can open it.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    // Heat in Movies 2, held by the guard with Ronin (a drive letter given to another volume); "Heat 4K" in Movies shares
    // its TMDb id and alice can open it. The daily resolve leaves alice's rating held on Heat, and Heat back has it.
    [Fact]
    public async Task AGuardHeldRating_WithACopyInAnotherLibrary_StaysHeldOnItsOwnItem_AndIsThereWhenTheOriginalIsBack()
    {
        var (heat, _, heat4k, onHeat, monitor) = await GuardHeldWithACopyElsewhereAsync();

        for (var day = 0; day < 2; day++)
        {
            await DailyAsync();
        }

        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((heat.Id, true), (after.ItemId, after.IsAttached));
        AssertMemberFieldsKept(onHeat, after);
        Assert.Null(_fx.Store.GetAttached(_alice.Id, heat4k.Id));
        Assert.Equal([onHeat.Id], HeldRatingIds(heat.Id));
        Assert.Empty(_consumer.GetDetached());
        Assert.Empty(_entries);

        // The drive back: the original item is there again under its id.
        _library.Add(heat);
        _library.RaiseAdded(heat);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await DailyAsync();
        Assert.Equal((heat.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        Assert.DoesNotContain(Held(), h => (Guid)h["ItemId"]! == heat.Id);
    }

    // The copy in a library alice can't open: the held rating stays held on its own id (the kit's view, no leak).
    [Fact]
    public async Task AGuardHeldRating_WithACopyTheMemberCantOpen_StaysHeld()
    {
        var hidden = AddMovie("Heat (1995) 4K", _shows, "949");
        _library.Hide(_alice, hidden);
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onHeat = await RateAsync(heat, _alice, 9);
        await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        for (var day = 0; day < 2; day++)
        {
            await DailyAsync();
        }

        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((heat.Id, true), (after.ItemId, after.IsAttached));
        Assert.Equal([onHeat.Id], HeldRatingIds(heat.Id));
    }

    // The held rating older than alice's rating of the copy, or newer: the copy in another library, which alice
    // rated too. Before the fix the move went through the conflict rule, which detached one of the two as Ambiguous (when
    // newer: the copy's own rating, in a library that was fine), and nothing put either back when Heat returned. Now nothing
    // moves: neither is Ambiguous, the copy keeps alice's own rating, and Heat back has the other (the next test's case too).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGuardHeldRating_WhoseMemberRatedTheCopyInAnotherLibrary_StaysHeld_AndTheCopyKeepsTheMembersOwnRating(bool heldIsNewer)
    {
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var heat4k = AddMovie("Heat (1995) 4K", _movies, "949");
        var onHeat = await RateAsync(heat, _alice, 9, updated: Created.AddDays(heldIsNewer ? 1 : 0));
        var on4k = await RateAsync(heat4k, _alice, 6, updated: Created.AddDays(heldIsNewer ? 0 : 1));
        var onRonin = await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(2, Held().Count(h => (bool)h["MassDetach"]!));

        for (var day = 0; day < 2; day++)
        {
            await DailyAsync();
        }

        AssertWhereTheyWere();
        Assert.Equal([onHeat.Id], HeldRatingIds(heat.Id));
        Assert.Empty(_consumer.GetDetached());
        Assert.Equal(0, (int)Lifecycle("Ambiguous")["Count"]!);
        Assert.Empty(_entries);

        // The drive back with the original's ids.
        _library.Add(heat);
        _library.Add(ronin);
        _library.RaiseAdded(heat);
        _library.RaiseAdded(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        for (var day = 0; day < 2; day++)
        {
            await DailyAsync();
        }

        AssertWhereTheyWere();
        Assert.Equal((ronin.Id, true), (_fx.Store.Get(onRonin.Id)!.ItemId, _fx.Store.Get(onRonin.Id)!.IsAttached));
        Assert.Empty(Held());
        Assert.Empty(_consumer.GetDetached());
        Assert.Empty(_entries);

        void AssertWhereTheyWere()
        {
            var held = _fx.Store.Get(onHeat.Id)!;
            var copy = _fx.Store.Get(on4k.Id)!;
            Assert.Equal((heat.Id, true, (string?)null), (held.ItemId, held.IsAttached, held.DetachReason));
            Assert.Equal((heat4k.Id, true, (string?)null, 6), (copy.ItemId, copy.IsAttached, copy.DetachReason, copy.Stars2));
            Assert.Equal(on4k.Id, _fx.Store.GetAttached(_alice.Id, heat4k.Id)!.Id);
        }
    }

    // The case above, then the drive back in one batch, no daily run: the rating is on Heat.
    [Fact]
    public async Task AGuardHeldRatingWhoseMemberRatedTheCopy_TheOriginalBack_HasItsRatingAgain_InTheBatch()
    {
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var heat4k = AddMovie("Heat (1995) 4K", _movies, "949");
        var onHeat = await RateAsync(heat, _alice, 9, updated: Created);
        var on4k = await RateAsync(heat4k, _alice, 6, updated: Created.AddDays(1));
        await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await DailyAsync();

        _library.Add(heat);
        _library.Add(ronin);
        _library.RaiseAdded(heat);
        _library.RaiseAdded(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((heat.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        Assert.Equal((heat4k.Id, true), (_fx.Store.Get(on4k.Id)!.ItemId, _fx.Store.Get(on4k.Id)!.IsAttached));
        Assert.Empty(Held());
    }

    // Since 0.3.13: a former member's held rating (the server doesn't know them) is resolved
    // without a view, but the library-folder rule applies to every held rating, so a copy in another library
    // leaves it held, as a known member's: the guard errs towards holding, and the original's folder coming back gets it.
    [Fact]
    public async Task AFormerMembersGuardHeldRating_StaysHeld_ACopyInAnotherLibraryDoesntTakeIt()
    {
        var gone = ServicesFixture.NewUser("gone");
        var hidden = AddMovie("Heat (1995) 4K", _shows, "949");
        _library.Hide(_alice, hidden);
        _library.Hide(_bob, hidden);
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onHeat = await RateAsync(heat, gone, 9);
        await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        await DailyAsync();

        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((heat.Id, true), (after.ItemId, after.IsAttached));
        Assert.Equal([onHeat.Id], HeldRatingIds(heat.Id));
    }

    // The conflict rule never takes a held rating, whoever's: a former member who rated the copy too keeps both
    // ratings where they are, nothing Ambiguous, the held one still held.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFormerMembersGuardHeldRating_NeverGoesThroughTheConflictRule_WhenTheyRatedTheCopyToo(bool heldIsNewer)
    {
        var gone = ServicesFixture.NewUser("gone");
        var copy = AddMovie("Heat (1995) 4K", _shows, "949");
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onHeat = await RateAsync(heat, gone, 9, updated: Created.AddDays(heldIsNewer ? 1 : 0));
        var onCopy = await RateAsync(copy, gone, 6, updated: Created.AddDays(heldIsNewer ? 0 : 1));
        await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        for (var day = 0; day < 2; day++)
        {
            await DailyAsync();
        }

        Assert.Equal((heat.Id, true, (string?)null), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached, _fx.Store.Get(onHeat.Id)!.DetachReason));
        Assert.Equal((copy.Id, true, (string?)null), (_fx.Store.Get(onCopy.Id)!.ItemId, _fx.Store.Get(onCopy.Id)!.IsAttached, _fx.Store.Get(onCopy.Id)!.DetachReason));
        Assert.Equal([onHeat.Id], HeldRatingIds(heat.Id));
        Assert.Empty(_consumer.GetDetached());
        Assert.Empty(_entries);
    }

    // Inside the held folder too: the renamed film's batch finds alice's own new rating on it, so her held
    // rating stays held, on its own item and still listed (Data → Detached & ambiguous counts it), and hers on the renamed
    // film stays. With a rated item in the folder again the next sweep lets the guard go (a rated item there proves the
    // folder), and the rating is an ordinary detached one from then on, under the conflict rule as any.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGuardHeldRating_NeverGoesThroughTheConflictRule_WhenItsMemberRatedTheRenamedFilm_AndStaysListedAsHeld(bool heldIsNewer)
    {
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onHeat = await RateAsync(heat, _alice, 9, updated: Created.AddDays(heldIsNewer ? 1 : 0));
        var onRonin = await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(2, Held().Count(h => (bool)h["MassDetach"]!));

        var renamed = AddMovie("Heat 1995", _movies2, "949");
        var onRenamed = await RateAsync(renamed, _alice, 4, updated: Created.AddDays(heldIsNewer ? 0 : 1));
        _library.RaiseAdded(renamed);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((heat.Id, true, (string?)null), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached, _fx.Store.Get(onHeat.Id)!.DetachReason));
        Assert.Equal((renamed.Id, true, 4), (_fx.Store.Get(onRenamed.Id)!.ItemId, _fx.Store.Get(onRenamed.Id)!.IsAttached, _fx.Store.Get(onRenamed.Id)!.Stars2));
        Assert.Equal([onHeat.Id], HeldRatingIds(heat.Id));
        Assert.Equal([onRonin.Id], HeldRatingIds(ronin.Id));
        Assert.Equal(0, (int)Lifecycle("LastBatch")["Ambiguous"]!);
        Assert.Empty(_entries);

        // The next sweep: Movies 2 holds a rated item again, so the two detach as Missing; the daily resolve then keeps the
        // newer of alice's two on the renamed film and leaves the older Ambiguous, as for any detached rating.
        await DailyAsync();
        Assert.Empty(Held());
        var (winner, loser) = heldIsNewer ? (onHeat, onRenamed) : (onRenamed, onHeat);
        Assert.Equal((renamed.Id, true), (_fx.Store.Get(winner.Id)!.ItemId, _fx.Store.Get(winner.Id)!.IsAttached));
        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(loser.Id)!.DetachReason);
    }

    // A rename inside the held folder with two held ratings on one film: the one whose member has no rating on the renamed
    // film moves, the other stays held, and the held item lists only that one.
    [Fact]
    public async Task AGuardHeldItem_WhoseRatingsMoveInPart_IsStillHeld_ListingOnlyTheRatingLeft()
    {
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var alices = await RateAsync(heat, _alice, 9);
        var bobs = await RateAsync(heat, _bob, 5);
        await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(new[] { alices.Id, bobs.Id }.Order(), HeldRatingIds(heat.Id).Order());

        var renamed = AddMovie("Heat 1995", _movies2, "949");
        var alicesNew = await RateAsync(renamed, _alice, 3, updated: Created.AddDays(2));
        _library.RaiseAdded(renamed);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((renamed.Id, true), (_fx.Store.Get(bobs.Id)!.ItemId, _fx.Store.Get(bobs.Id)!.IsAttached));
        Assert.Equal((heat.Id, true), (_fx.Store.Get(alices.Id)!.ItemId, _fx.Store.Get(alices.Id)!.IsAttached));
        Assert.Equal((renamed.Id, true), (_fx.Store.Get(alicesNew.Id)!.ItemId, _fx.Store.Get(alicesNew.Id)!.IsAttached));
        Assert.Equal([alices.Id], HeldRatingIds(heat.Id));
    }

    // The item a rating comes back to was merged into a primary version in a library alice can't open. Bob's rating
    // goes to the primary; alice's stays on the version she can open, so it never disappears from her view.
    [Fact]
    public async Task ARelinkOntoAMergedVersion_LandsOnThePrimaryOnlyWhenTheMemberCanOpenIt()
    {
        var version = AddMovie("Heat (1995) - 4K", _movies, "949");
        var primary = AddMovie("Heat (1995)", _shows, "949");
        _library.Hide(_alice, primary);
        await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), _bob, 7);
        var alices = await RateAsync(version, _alice, 9);
        var bobs = await RateAsync(version, _bob, 6);
        var monitor = Monitor();
        _library.Remove(version);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(RatingValues.DetachReason.Removed, _fx.Store.Get(alices.Id)!.DetachReason);

        // Back under its id (an Exact match by id), merged into the primary meanwhile.
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        version.PrimaryVersionId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        _library.Add(version);
        _library.RaiseAdded(version);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((version.Id, true), (_fx.Store.Get(alices.Id)!.ItemId, _fx.Store.Get(alices.Id)!.IsAttached));
        Assert.Equal((primary.Id, true), (_fx.Store.Get(bobs.Id)!.ItemId, _fx.Store.Get(bobs.Id)!.IsAttached));
    }

    // A merge seen in an update (the batch's Updated row): the same rule, and alice's rating on the version takes
    // its fresh snapshot.
    [Fact]
    public async Task AnItemMergedIntoAPrimaryTheMemberCantOpen_KeepsThatMembersRatingOnTheVersion()
    {
        var version = AddMovie("Heat (1995) - 4K", _movies, "949");
        var primary = AddMovie("Heat (1995)", _shows, "949");
        _library.Hide(_alice, primary);
        var alices = await RateAsync(version, _alice, 9);
        var bobs = await RateAsync(version, _bob, 6);
        var monitor = Monitor();

        version.PrimaryVersionId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        version.ProviderIds["Imdb"] = "tt0113277";
        _library.RaiseUpdated(version, MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((version.Id, true), (_fx.Store.Get(alices.Id)!.ItemId, _fx.Store.Get(alices.Id)!.IsAttached));
        Assert.Equal("tt0113277", _fx.Store.Get(alices.Id)!.Identity.ProviderIds["Imdb"]);
        Assert.Equal((primary.Id, true), (_fx.Store.Get(bobs.Id)!.ItemId, _fx.Store.Get(bobs.Id)!.IsAttached));
        var relinks = _fx.Store.GetModerationLog(0, 10).Where(l => l.Action == RatingValues.ModerationAction.Relink).ToList();
        Assert.Equal(bobs.Id, Assert.Single(relinks).RatingId);
    }

    // Heat and Ronin in Movies 2, held by the guard; "Heat 4K" in Movies with Heat's TMDb id (the first test's case).
    private async Task<(Movie Heat, Movie Ronin, Movie Heat4k, RatingRecord OnHeat, LibraryChangeMonitor Monitor)> GuardHeldWithACopyElsewhereAsync()
    {
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var heat4k = AddMovie("Heat (1995) 4K", _movies, "949");
        var onHeat = await RateAsync(heat, _alice, 9);
        await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(2, Held().Count(h => (bool)h["MassDetach"]!));
        Assert.True(_fx.Store.Get(onHeat.Id)!.IsAttached);
        return (heat, ronin, heat4k, onHeat, monitor);
    }

    // The daily maintenance's two lifecycle steps, in their order: the verify sweep, then the re-resolution.
    private async Task DailyAsync()
    {
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        await _detached.ResolveDetachedAsync(_consumer, Ct);
    }

    private List<Guid> HeldRatingIds(Guid itemId)
        => Assert.IsAssignableFrom<IEnumerable<Guid>>(Assert.Single(Held(), h => (Guid)h["ItemId"]! == itemId)["RatingIds"]).ToList();
}
