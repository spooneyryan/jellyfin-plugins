using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The archive gaps closed beside moderation: Anonymize's rewrite matches the user's rating ids as Delete's does (so the
/// line under the id a member had before a remap goes, while the former member's re-appended line
/// stays), and an archived rating whose purge is pending is in neither the export nor the counts (<c>IsPurgePending</c>),
/// whether a deleted user's policy or a removed review left it pending.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    // Delete's case, for Anonymize: archived, back, given to a new id by Remap by name, then the member deleted under
    // Anonymize. The rating lives on, the former member's; the earlier line under the old id, the only one that ties the
    // review to it, goes.
    [Fact]
    public async Task AnonymizeAfterARemap_LeavesNoArchiveLineUnderTheOldId_AndTheRatingLivesOnTheFormerMembers()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, _alice.Id, "alice"); RatingsUserDataOwner.RecordName(w, _bob.Id, "bob"); return 0; }, Ct);
        await PadAsync(_bob, 10);
        var newAlice = ServicesFixture.NewUser("alice");
        ServerKnows(newAlice, _bob);
        var server = new Dictionary<Guid, string> { [newAlice.Id] = "alice", [_bob.Id] = "bob" };
        var reconciler = UsersReconciler(Owner(), () => server, DeletedUserPolicy.Anonymize);
        await reconciler.ReconcileAsync(Ct);
        var dry = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        await reconciler.RemapByNameAsync(dryRun: false, dry.PlanToken, Ct);
        Assert.Equal(newAlice.Id, _fx.Store.Get(onHeat.Id)!.UserId);
        var archiveFile = _detached.ArchiveOf(_consumer).FilePath;
        Assert.Contains(_alice.Id.ToString(), await File.ReadAllTextAsync(archiveFile, Ct), StringComparison.OrdinalIgnoreCase);

        server.Remove(newAlice.Id);
        ServerKnows(_bob);
        await reconciler.HandleUserDeletedAsync(newAlice.Id, Ct);

        var kept = _fx.Store.Get(onHeat.Id)!;
        Assert.NotEqual(newAlice.Id, kept.UserId);
        Assert.Equal(HeistReview, kept.ReviewText);
        var text = await File.ReadAllTextAsync(archiveFile, Ct);
        Assert.DoesNotContain(HeistReview, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_alice.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_alice.Id.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _consumer.Describe()["ArchivePurgesPending"]);
    }

    // Anonymize of a member with an archived rating: the former member's re-appended line stays, the deleted id's goes, and a
    // pending purge of 0.3.14 (no ids, no former member) still reads.
    [Fact]
    public async Task AnonymizeOfAnArchivedRating_KeepsTheFormerMembersLineOnly()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        ServerKnows(_bob);

        await Owner().HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Anonymize, Ct);

        var record = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Equal(onHeat.Id.ToString("D"), record.Id);
        Assert.NotEqual(_alice.Id, RatingsUserDataOwner.ArchivedUserIdOf(record));
        Assert.Single(_detached.ExportArchive(_consumer));
        var text = await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct);
        Assert.Equal(1, Occurrences(text, HeistReview));
        Assert.DoesNotContain(_alice.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);

        var old = RatingsUserDataOwner.PendingPurgesOf($"[{{\"UserId\":\"{_alice.Id}\",\"Ids\":[]}}]");
        Assert.Equal((_alice.Id, 0, (Guid?)null), (old[0].UserId, old[0].Ids.Count, old[0].KeepUserId));
    }

    // A deleted user's rewrite that failed: the archived rating is in no export and no count while it is pending.
    [Fact]
    public async Task AnArchivedRating_WhosePurgeIsPending_IsInNoExportAndNoCount()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        Assert.Single(_detached.ExportArchive(_consumer));
        Assert.Equal(1, Archived());
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        var archiveFile = _detached.ArchiveOf(_consumer).FilePath;
        var blocker = Directory.CreateDirectory(archiveFile + ".tmp");

        await reconciler.HandleUserDeletedAsync(_alice.Id, Ct);

        var line = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.True(_consumer.IsPurgePending(line));
        Assert.Empty(_detached.ExportArchive(_consumer));
        Assert.Equal(0, Archived());

        blocker.Delete();
        await owner.RunPendingPurgesAsync(Ct);
        Assert.Empty(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Equal(_alice.Id, _fx.Store.GetTombstone(onHeat.Id)!.UserId);
    }

    // The same for a rating whose removed review left its archive lines to go (ArchivedLinePurges' Meta value).
    [Fact]
    public async Task AnArchivedRating_WhoseReviewsLinesArePending_IsInNoExportAndNoCount()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var line = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.False(_consumer.IsPurgePending(line));

        await _fx.Store.WriteAsync(w => { w.SetMeta(ArchivedLinePurges.Key, $"[\"{onHeat.Id}\"]"); return 0; }, Ct);

        Assert.True(_consumer.IsPurgePending(line));
        Assert.Empty(_detached.ExportArchive(_consumer));
        Assert.Equal(0, Archived());
        Assert.Equal(1, _consumer.Describe()["ArchiveIdPurgesPending"]);

        // Run, it takes the line, and nothing is pending.
        var purges = new ArchivedLinePurges(_fx.Store, _logger, () => _detached.ArchiveOf(_consumer));
        var run = await purges.RunAsync(Ct);
        Assert.Equal((1, 1, 0), ((int)run["Pending"]!, (int)run["Done"]!, (int)run["Failed"]!));
        Assert.Empty(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Equal(0, _consumer.Describe()["ArchiveIdPurgesPending"]);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private int Archived()
    {
        var owners = (List<IDictionary<string, object?>>)_detached.Describe()["Owners"]!;
        return (int)owners.Single(o => Equals(o["Owner"], _consumer.Name))["Archived"]!;
    }
}
