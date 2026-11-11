using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.AutoCollections.AutoCollectionTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The definitions' import, worked out without a server: users mapped by name, new definitions without a collection, the
/// ones already here keeping theirs, names made unique, the most auto-collections, and nothing removed.
/// </summary>
public sealed class AutoCollectionTransferTests
{
    private static readonly Guid There = Guid.NewGuid();
    private static readonly Guid Here = Guid.NewGuid();
    private static readonly Guid Gone = Guid.NewGuid();

    private static AutoCollectionTransfer.Plan Plan(IEnumerable<AutoCollectionDefinition> definitions, ImportMode mode = ImportMode.Merge)
    {
        var incoming = new AutoCollectionsDocument { Definitions = definitions.ToList() };
        var users = ImportUserMap.Build(
            AutoCollectionStore.CountRecordsByUser(incoming),
            [new ExportUser(There, "manager"), new ExportUser(Gone, "left")],
            new Dictionary<Guid, string> { [Here] = "Manager" },
            new HashSet<Guid>(),
            MapUsersBy.Name);
        return new AutoCollectionTransfer.Plan(incoming, users, new ImportRequest(mode, false, MapUsersBy.Name));
    }

    [Fact]
    public void ANewDefinition_HasNoCollection_ItsUsersMapped_AndOneHere_KeepsItsCollection()
    {
        var kept = Definition(Here, null, "Kept", collectionId: Guid.NewGuid());
        var current = new AutoCollectionsDocument { Definitions = [kept] };
        var fresh = Definition(There, null, "Fresh", collectionId: Guid.NewGuid()) with { EvaluationUserId = There };
        var keptThere = kept with { CreatedByUserId = There, UpdatedByUserId = There, CollectionId = Guid.NewGuid(), UpdatedUtc = kept.UpdatedUtc.AddHours(1), Enabled = false };

        var applied = AutoCollectionTransfer.Apply(current, Plan([fresh, keptThere]), 100);

        Assert.Equal(1, applied.Counts.Added);
        Assert.Equal(1, applied.Counts.Updated);
        var made = applied.Document.Definitions.Single(d => d.Id == fresh.Id);
        Assert.Null(made.CollectionId);
        Assert.Equal(Here, made.CreatedByUserId);
        Assert.Equal(Here, made.EvaluationUserId);
        var updated = applied.Document.Definitions.Single(d => d.Id == kept.Id);
        Assert.Equal(kept.CollectionId, updated.CollectionId);
        Assert.False(updated.Enabled);
        Assert.Contains(applied.Notes, n => n.Contains("Create on the Auto-collections tab", StringComparison.Ordinal));
        Assert.Contains(applied.Notes, n => n.StartsWith("1 auto-collections already here were updated: their collections change at their next run.", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImport_LeavesTheNamesOfDefinitionsItDoesntTouch_EvenTwoThatShareOne_AndNumbersOnlyWhatItImports()
    {
        // Two here that share a name (a rename in the server's editor once could make that), and the file doesn't hold them.
        var a = Definition(Here, null, "Horror", collectionId: Guid.NewGuid());
        var b = Definition(Here, null, "Horror", collectionId: Guid.NewGuid()) with { CreatedUtc = Created.AddMinutes(1) };
        var current = new AutoCollectionsDocument { Definitions = [a, b] };

        var unrelated = AutoCollectionTransfer.Apply(current, Plan([Definition(There, null, "Unrelated")]), 100);
        Assert.Equal(["Horror", "Horror", "Unrelated"], unrelated.Document.Definitions.Select(d => d.Name).Order());
        Assert.DoesNotContain(unrelated.Notes, n => n.Contains("renamed", StringComparison.Ordinal));

        // An imported one that takes their name gets the number; they keep theirs.
        var incoming = Definition(There, null, "horror");
        var applied = AutoCollectionTransfer.Apply(current, Plan([incoming]), 100);
        Assert.Equal("Horror", applied.Document.Definitions.Single(d => d.Id == a.Id).Name);
        Assert.Equal("Horror", applied.Document.Definitions.Single(d => d.Id == b.Id).Name);
        Assert.Equal("horror (2)", applied.Document.Definitions.Single(d => d.Id == incoming.Id).Name);
        Assert.Contains(applied.Notes, n => n.StartsWith("1 auto-collections were renamed", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImportedDefinition_ThatFollowsAQueryMissingHereOrNotItsCreators_IsKept_AndNamedInANote()
    {
        var mine = SavedQuery(Here, Q());
        var theirs = SavedQuery(Guid.NewGuid(), Q());
        var queries = new Jellyfin.Plugin.SmartSearch.Storage.SavedQueriesDocument { Queries = [mine, theirs] };
        var own = Definition(There, mine.Id, "Own query");
        var foreign = Definition(There, theirs.Id, "Someone else's query");
        var missing = Definition(There, Guid.NewGuid(), "Missing query");

        var applied = AutoCollectionTransfer.Apply(new AutoCollectionsDocument(), Plan([own, foreign, missing]), 100, queries);

        Assert.Equal(3, applied.Counts.Added);
        Assert.All(applied.Document.Definitions, d => Assert.Null(d.CollectionId));
        var note = Assert.Single(applied.Notes, n => n.Contains("isn't their creator's", StringComparison.Ordinal));
        Assert.StartsWith("2 imported auto-collections", note, StringComparison.Ordinal);
        Assert.Contains("Someone else's query", note, StringComparison.Ordinal);
        Assert.Contains("Missing query", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Own query", note, StringComparison.Ordinal);

        // Without the saved queries (a dry run, whose queries aren't imported yet) nothing is checked.
        Assert.DoesNotContain(AutoCollectionTransfer.Apply(new AutoCollectionsDocument(), Plan([foreign]), 100).Notes, n => n.Contains("isn't their creator's", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownCreator_IsSkipped_AnUnknownEvaluationUser_IsKeptAndSaidSo()
    {
        var orphan = Definition(Gone, null, "Orphan");
        var watcher = Definition(There, null, "Watcher") with { EvaluationUserId = Gone };

        var applied = AutoCollectionTransfer.Apply(new AutoCollectionsDocument(), Plan([orphan, watcher]), 100);

        Assert.Equal(1, applied.Counts.SkippedUnknownUser);
        var kept = Assert.Single(applied.Document.Definitions);
        Assert.Equal(Gone, kept.EvaluationUserId);
        Assert.Contains(applied.Notes, n => n.Contains("Evaluation user missing", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameTaken_GetsANumber_TheMostAreKept_AndReplaceRemovesNothing()
    {
        var mine = Definition(Here, null, "Horror");
        var current = new AutoCollectionsDocument { Definitions = [mine] };
        var incoming = new[] { Definition(There, null, "horror"), Definition(There, null, "Second"), Definition(There, null, "Third") };

        var applied = AutoCollectionTransfer.Apply(current, Plan(incoming, ImportMode.Replace), 3);

        Assert.Equal(["Horror", "Second", "horror (2)"], applied.Document.Definitions.Select(d => d.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(1, applied.Counts.Skipped);
        Assert.Equal(0, applied.Counts.Removed);
        Assert.Contains(applied.Notes, n => n.Contains("past the most", StringComparison.Ordinal));
        Assert.Contains(applied.Notes, n => n.Contains("renamed", StringComparison.Ordinal));
        Assert.Contains(applied.Notes, n => n.Contains("Replace doesn't remove", StringComparison.Ordinal));
        Assert.Contains(applied.Document.Definitions, d => d.Id == mine.Id && d.Name == "Horror");
    }

    [Fact]
    public void ARoundTrip_ChangesNothing()
    {
        var mine = Definition(Here, null, "Horror", collectionId: Guid.NewGuid());
        var current = new AutoCollectionsDocument { Definitions = [mine] };

        var applied = AutoCollectionTransfer.Apply(current, Plan([mine with { CreatedByUserId = There, UpdatedByUserId = There, CollectionId = Guid.NewGuid() }]), 100);

        Assert.Equal(1, applied.Counts.Unchanged);
        Assert.Equal(0, applied.Counts.Added + applied.Counts.Updated);
        Assert.Equal(mine.CollectionId, Assert.Single(applied.Document.Definitions).CollectionId);
    }
}
