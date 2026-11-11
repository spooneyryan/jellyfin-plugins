using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.AutoCollections.AutoCollectionTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The definitions' and the state's stores: a newer version's definition read leniently and kept as it was, the
/// deleted-user policy, remap, references, and the state starting over when unreadable.
/// </summary>
public sealed class AutoCollectionStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A definition a newer Smart Search wrote: a field this version doesn't know and enum values it doesn't know.
    private static string NewerFile(Guid newer, Guid current, Guid creator) => $$"""
        {
          "Version": 1,
          "Definitions": [
            {
              "Id": "{{newer}}", "Name": "Hand-made plus", "CollectionId": "{{Guid.NewGuid()}}",
              "Source": { "Mode": "Smart", "SavedQueryId": "{{Guid.NewGuid()}}" },
              "Presentation": { "SortMode": "Shuffle", "Lock": true, "OverviewMode": 3 },
              "ManualEdits": { "Mode": "Respect", "Pinned": [], "Excluded": [] },
              "MembershipMode": "AddOnly",
              "CreatedByUserId": "{{creator}}", "CreatedUtc": "2026-11-01T10:00:00Z", "UpdatedByUserId": "{{creator}}", "UpdatedUtc": "2026-11-01T10:00:00Z"
            },
            {
              "Id": "{{current}}", "Name": "Current", "Source": { "Mode": "Reference", "SavedQueryId": "{{Guid.NewGuid()}}" },
              "CreatedByUserId": "{{creator}}", "CreatedUtc": "2026-11-02T10:00:00Z", "UpdatedByUserId": "{{creator}}", "UpdatedUtc": "2026-11-02T10:00:00Z"
            }
          ]
        }
        """;

    [Fact]
    public async Task ANewerDefinition_LoadsWithoutQuarantine_IsMarked_AndKeepsWhatItSaidThroughOtherWrites()
    {
        using var folder = new TempDirectory();
        var (newer, current, creator) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await File.WriteAllTextAsync(Path.Combine(folder.Path, AutoCollectionStore.FileName), NewerFile(newer, current, creator), Ct);
        using var store = Store(folder.Path);

        Assert.Equal(StoreStatus.Ok, store.Documents.Status);
        Assert.False(store.IsReadOnly);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.corrupt-*"));
        var definition = store.Find(newer)!;
        Assert.True(definition.Source.Mode.IsUnknown);
        Assert.Equal("Smart", definition.Source.Mode.Raw);
        Assert.True(definition.Presentation.SortMode.IsUnknown);
        Assert.True(definition.Presentation.OverviewMode.IsUnknown);
        Assert.Equal("MembershipMode", definition.FromNewerVersion());
        Assert.Null(store.Find(current)!.FromNewerVersion());

        // Another definition's change rewrites the file: the newer one's values and field come back as they were.
        await store.UpdateDefinitionAsync(current, d => d with { Name = "Renamed" }, Ct);
        var written = JsonDocument.Parse(await File.ReadAllTextAsync(store.Documents.FilePath, Ct)).RootElement.GetProperty("Definitions")
            .EnumerateArray().Single(d => d.GetProperty("Id").GetGuid() == newer);
        Assert.Equal("Smart", written.GetProperty("Source").GetProperty("Mode").GetString());
        Assert.Equal("Shuffle", written.GetProperty("Presentation").GetProperty("SortMode").GetString());
        Assert.Equal("3", written.GetProperty("Presentation").GetProperty("OverviewMode").GetString());
        Assert.Equal("AddOnly", written.GetProperty("MembershipMode").GetString());
        Assert.Equal("Renamed", store.Find(current)!.Name);
    }

    [Theory]
    [InlineData("\"Unknown\"", true)]
    [InlineData("\"reference\"", false)]
    [InlineData("\"1\"", true)]
    [InlineData("null", true)]
    public void TheLenientEnum_ReadsNamesOnly(string json, bool unknown)
    {
        var read = JsonSerializer.Deserialize<AutoCollectionSource>("{\"Mode\":" + json + "}", JsonStoreFormat.Options)!;

        Assert.Equal(unknown, read.Mode.IsUnknown);
        if (!unknown)
        {
            Assert.Equal(SmartSearchAutoCollectionSourceMode.Reference, read.Mode.Value);
            Assert.Contains("\"Mode\": \"Reference\"", JsonSerializer.Serialize(read, JsonStoreFormat.Options), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AFileOfANewerSchema_IsReadOnly_AndEveryDefinitionFailsClosed()
    {
        using var folder = new TempDirectory();
        var creator = Admin();
        var query = SavedQuery(creator.Id, Q());
        var definition = Definition(creator.Id, query.Id);
        await File.WriteAllTextAsync(
            Path.Combine(folder.Path, AutoCollectionStore.FileName),
            JsonSerializer.Serialize(new AutoCollectionsDocument { Version = 2, Definitions = [definition] }, JsonStoreFormat.Options),
            Ct);
        using var store = Store(folder.Path);

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Documents.Status);
        var check = Checks.CheckForRun(definition, Context([query], [creator], readOnly: store.IsReadOnly));
        Assert.Equal(SmartSearchAutoCollectionStatus.NeedsNewerVersion, check.Status);
        Assert.Equal(AutoCollectionReasons.StoreReadOnly, check.Reason);
    }

    [Fact]
    public async Task ADeletedUser_ChangesNoDefinition_UnderAnyPolicy_AndRemapMovesEveryRole()
    {
        using var folder = new TempDirectory();
        using var store = Store(folder.Path);
        var (alice, bob, carol) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var a = Definition(alice, Guid.NewGuid(), "A") with { EvaluationUserId = bob };
        var b = Definition(bob, Guid.NewGuid(), "B") with { EvaluationUserId = bob };
        var c = Definition(carol, Guid.NewGuid(), "C");
        await store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [a, b, c]), Ct);

        Assert.Equal(new Dictionary<Guid, int> { [alice] = 1, [bob] = 2, [carol] = 1 }, store.CountRecordsByUser());
        foreach (var policy in Enum.GetValues<DeletedUserPolicy>())
        {
            Assert.False(store.WouldChangeRecords(alice, policy));
            Assert.Equal(0, await store.HandleUserDeletedAsync(alice, policy, Ct));
        }

        Assert.Equal(3, store.Current.Definitions.Count);

        var dave = Guid.NewGuid();
        Assert.Equal(2, await store.RemapUsersAsync(new Dictionary<Guid, Guid> { [bob] = dave }, Ct));
        Assert.Equal(dave, store.Find(a.Id)!.EvaluationUserId);
        Assert.Equal(alice, store.Find(a.Id)!.CreatedByUserId);
        Assert.Equal(dave, store.Find(b.Id)!.CreatedByUserId);
        Assert.Equal(dave, store.Find(b.Id)!.EvaluationUserId);
        Assert.Equal(carol, store.Find(c.Id)!.CreatedByUserId);
    }

    [Fact]
    public async Task References_AreTheCreatorsFollowersOfTheirOwnQuery_NotSnapshots_AndCollectionsAreListed()
    {
        using var folder = new TempDirectory();
        using var store = Store(folder.Path);
        var user = Guid.NewGuid();
        var record = SavedQuery(user, Q());
        var query = record.Id;
        var box = Guid.NewGuid();
        var referencing = Definition(user, query, "A", collectionId: box);
        var copy = Snapshot(user, Q(), "B") with { Source = Snapshot(user, Q()).Source with { SavedQueryId = query } };
        await store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [referencing, copy, Definition(user, Guid.NewGuid(), "C")]), Ct);

        Assert.Equal(referencing.Id, Assert.Single(store.ReferencesTo(record)).Id);
        Assert.Empty(store.ReferencesTo(record with { OwnerUserId = Guid.NewGuid() })); // its creator no longer owns it
        Assert.Empty(store.ReferencesTo(record with { Id = Guid.NewGuid() }));
        Assert.Equal([box], store.CollectionIds());
        Assert.Equal(referencing.Id, store.FindByCollection(box)!.Id);
        Assert.Null(store.FindByCollection(Guid.Empty));
    }

    [Fact]
    public async Task TheGate_KeepsOtherChangesOut_UntilItIsLetGo()
    {
        using var folder = new TempDirectory();
        using var store = Store(folder.Path);
        var held = await store.AcquireAsync(Ct);
        var other = store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [Definition(Guid.NewGuid(), null, "Other")]), Ct);
        await Task.Delay(50, Ct);
        Assert.False(other.IsCompleted);

        await store.UpdateHeldAsync(held, d => AutoCollectionStore.WithDefinitions(d, [Definition(Guid.NewGuid(), null, "Mine")]), Ct);
        held.Dispose();
        await other;

        Assert.Equal("Other", Assert.Single(store.Current.Definitions).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateHeldAsync(held, d => d, Ct));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Version\":1,\"States\":[1,2]}")]
    public async Task AnUnreadableStateFile_StartsEmpty_AtOnce(string content)
    {
        using var folder = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(folder.Path, AutoCollectionStateStore.FileName), content, Ct);
        using var state = StateStore(folder.Path);
        var id = Guid.NewGuid();

        await state.UpdateAsync(id, s => (s ?? new AutoCollectionState()) with { Status = SmartSearchAutoCollectionStatus.Ok, LastMemberCount = 3 }, Ct);

        Assert.False(state.Documents.IsReadOnly);
        Assert.Equal(3, state.Find(id)!.LastMemberCount);
        Assert.NotEmpty(Directory.GetFiles(folder.Path, AutoCollectionStateStore.FileName + ".corrupt-*"));
    }

    [Fact]
    public async Task ANewerStateFile_IsLeftAlone_AndTheStateIsKeptInMemory()
    {
        using var folder = new TempDirectory();
        var file = Path.Combine(folder.Path, AutoCollectionStateStore.FileName);
        await File.WriteAllTextAsync(file, "{\"Version\":9,\"States\":{}}", Ct);
        using var state = StateStore(folder.Path);
        var id = Guid.NewGuid();

        await state.UpdateAsync(id, _ => new AutoCollectionState { Status = SmartSearchAutoCollectionStatus.Running }, Ct);
        Assert.Equal(1, await state.PruneAsync([], Ct));
        await state.UpdateAsync(id, _ => new AutoCollectionState { Status = SmartSearchAutoCollectionStatus.Ok }, Ct);

        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, state.Find(id)!.Status.Value);
        Assert.Equal("{\"Version\":9,\"States\":{}}", await File.ReadAllTextAsync(file, Ct));
    }

    [Fact]
    public async Task TheStatePrune_DropsTheStatesOfDeletedDefinitions()
    {
        using var folder = new TempDirectory();
        using var state = StateStore(folder.Path);
        var (kept, gone) = (Guid.NewGuid(), Guid.NewGuid());
        await state.UpdateAsync(kept, _ => new AutoCollectionState(), Ct);
        await state.UpdateAsync(gone, _ => new AutoCollectionState(), Ct);

        Assert.Equal(1, await state.PruneAsync([kept], Ct));
        Assert.NotNull(state.Find(kept));
        Assert.Null(state.Find(gone));
    }

    [Fact]
    public void EveryPartOfADefinition_KeepsUnknownFields()
    {
        var json = "{\"Name\":\"x\",\"Source\":{\"Mode\":\"Reference\",\"A\":1},\"Presentation\":{\"B\":2},\"ManualEdits\":{\"Pinned\":[{\"ItemId\":\"" + Guid.NewGuid() + "\",\"C\":3}]},\"Safety\":{\"D\":4}}";

        var definition = JsonSerializer.Deserialize<AutoCollectionDefinition>(json, JsonStoreFormat.Options)!;

        Assert.Equal("Source.A", definition.FromNewerVersion());
        var written = JsonSerializer.Serialize(definition, JsonStoreFormat.Options);
        foreach (var field in new[] { "\"A\"", "\"B\"", "\"C\"", "\"D\"" })
        {
            Assert.Contains(field, written, StringComparison.Ordinal);
        }

        Assert.Equal("Presentation.B", (definition with { Source = new AutoCollectionSource() }).FromNewerVersion());
        Assert.Equal("ManualEdits.Pinned.C", (definition with { Source = new AutoCollectionSource(), Presentation = new AutoCollectionPresentation() }).FromNewerVersion());
        Assert.Equal("Safety.D", (definition with { Source = new AutoCollectionSource(), Presentation = new AutoCollectionPresentation(), ManualEdits = new AutoCollectionManualEdits() }).FromNewerVersion());
    }
}
