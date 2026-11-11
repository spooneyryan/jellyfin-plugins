using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>The kit's store conformance suite on <c>auto-collections.json</c>.</summary>
public sealed class AutoCollectionStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new AutoCollectionStoreConformance();
}

/// <summary>The kit's store conformance suite on <c>auto-collections-state.json</c>.</summary>
public sealed class AutoCollectionStateStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new AutoCollectionStateStoreConformance();
}

internal sealed class AutoCollectionStoreConformance : StoreConformance<AutoCollectionsDocument>
{
    public override int CurrentVersion => AutoCollectionsDocument.CurrentVersion;

    public override string StoreFileName => AutoCollectionStore.FileName;

    public override AutoCollectionsDocument CreateEmpty() => AutoCollectionsDocument.CreateEmpty();

    public override AutoCollectionsDocument AddRecord(AutoCollectionsDocument document, int number)
    {
        var creator = new Guid(number % 5, 0x0a, 0x4b, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66);
        using var snapshot = JsonDocument.Parse(AutoCollectionTestData.Q("{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"Amélie ✂ 日本語 " + number + "\"}"));
        var item = new Guid(number, 0x1111, 0x2222, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa);
        var definition = new AutoCollectionDefinition
        {
            Id = new Guid(number, 0x4b1d, 0x4c2e, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
            Name = "Collection " + number + ": café scène",
            Enabled = number % 3 != 0,
            Source = number % 2 == 0
                ? new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Reference, SavedQueryId = Guid.NewGuid() }
                : new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot, Snapshot = snapshot.RootElement.Clone(), SnapshotTakenUtc = AutoCollectionTestData.Created },
            EvaluationUserId = number % 4 == 0 ? creator : null,
            CollectionId = number % 5 == 0 ? null : Guid.NewGuid(),
            Presentation = new AutoCollectionPresentation { SortMode = SmartSearchSortMode.QueryOrder, OverviewMode = SmartSearchOverviewMode.Managed, Overview = "Über " + number },
            ManualEdits = new AutoCollectionManualEdits
            {
                Pinned = [new AutoCollectionItemEntry { ItemId = item, Identity = new IdentitySnapshot { ItemId = item, Kind = "Movie", Name = "Pinned " + number, Year = 1982 }, AddedUtc = AutoCollectionTestData.Created }]
            },
            Safety = new AutoCollectionSafety { AllowEmpty = number % 2 == 1, MaxRemovalPercent = number % 2 == 0 ? 30 : null },
            CreatedByUserId = creator,
            CreatedUtc = AutoCollectionTestData.Created.AddSeconds(number),
            UpdatedByUserId = creator,
            UpdatedUtc = AutoCollectionTestData.Created.AddSeconds(number)
        };
        return AutoCollectionStore.WithDefinitions(document, [.. document.Definitions, definition]) with
        {
            KnownUsers = document.KnownUsers.With(creator, "user " + (number % 5))
        };
    }

    public override int CountRecords(AutoCollectionsDocument document) => document.Definitions.Count;

    public override JsonDocumentStore<AutoCollectionsDocument> CreateStore(
        string file,
        int currentVersion,
        Func<AutoCollectionsDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => currentVersion == CurrentVersion && migrations.Count == 0
            ? AutoCollectionStore.CreateStore(file, logger, timeProvider)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        ((JsonObject)root["Definitions"]![0]!)[name] = value;
        return true;
    }
}

internal sealed class AutoCollectionStateStoreConformance : StoreConformance<AutoCollectionsStateDocument>
{
    public override int CurrentVersion => AutoCollectionsStateDocument.CurrentVersion;

    public override string StoreFileName => AutoCollectionStateStore.FileName;

    public override AutoCollectionsStateDocument CreateEmpty() => AutoCollectionsStateDocument.CreateEmpty();

    public override AutoCollectionsStateDocument AddRecord(AutoCollectionsStateDocument document, int number)
    {
        var states = new Dictionary<Guid, AutoCollectionState>(document.States)
        {
            [new Guid(number, 0x4b1d, 0x4c2e, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66)] = new AutoCollectionState
            {
                Status = number % 2 == 0 ? SmartSearchAutoCollectionStatus.Ok : SmartSearchAutoCollectionStatus.QueryInvalid,
                Reason = number % 2 == 0 ? null : "CycleWithAutoCollection",
                LastRunUtc = AutoCollectionTestData.Created.AddMinutes(number),
                LastReason = SmartSearchAutoCollectionRunReason.PostScan,
                LastMemberCount = number,
                LastError = "Fehler ✂ " + number,
                History = [new AutoCollectionHistoryEntry { Utc = AutoCollectionTestData.Created, Reason = SmartSearchAutoCollectionRunReason.Manual, Added = number, Outcome = "Written", RemovedIds = [Guid.NewGuid()] }]
            }
        };
        return document with { States = states };
    }

    public override int CountRecords(AutoCollectionsStateDocument document) => document.States.Count;

    public override JsonDocumentStore<AutoCollectionsStateDocument> CreateStore(
        string file,
        int currentVersion,
        Func<AutoCollectionsStateDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => currentVersion == CurrentVersion && migrations.Count == 0
            ? AutoCollectionStateStore.CreateStore(file, logger, timeProvider)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        foreach (var state in ((JsonObject)root["States"]!))
        {
            ((JsonObject)state.Value!)[name] = value;
            return true;
        }

        return false;
    }
}
