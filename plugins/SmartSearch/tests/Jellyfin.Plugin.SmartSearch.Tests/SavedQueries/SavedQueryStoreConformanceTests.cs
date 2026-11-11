using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Jellyfin.Plugin.SmartSearch.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// The kit's store conformance suite on <c>saved-queries.json</c>.
/// </summary>
public sealed class SavedQueryStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new SavedQueryStoreConformance();
}

internal sealed class SavedQueryStoreConformance : StoreConformance<SavedQueriesDocument>
{
    private static readonly DateTime _created = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    public override int CurrentVersion => SavedQueriesDocument.CurrentVersion;

    public override string StoreFileName => SavedQueryStore.FileName;

    public override SavedQueriesDocument CreateEmpty() => SavedQueriesDocument.CreateEmpty();

    public override SavedQueriesDocument AddRecord(SavedQueriesDocument document, int number)
    {
        var userId = new Guid(number % 7, 0x0a, 0x4b, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66);
        using var query = JsonDocument.Parse("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"Name\",\"Op\":\"Contains\",\"Value\":\"Amélie ✂ 日本語 " + number + "\"}]}}");
        return SavedQueryStore.WithQueries(
            document,
            [
                .. document.Queries,
                new SavedQueryRecord
                {
                    Id = new Guid(number, 0x4b1d, 0x4c2e, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
                    OwnerUserId = userId,
                    Name = "Query " + number + ": café scène",
                    Description = number % 2 == 0 ? "Described" : string.Empty,
                    Shared = number % 3 == 0,
                    Query = query.RootElement.Clone(),
                    QueryHash = "hash" + number,
                    CreatedUtc = _created.AddSeconds(number),
                    UpdatedUtc = _created.AddSeconds(number),
                    UpdatedByUserId = userId,
                    LastRunUtc = number % 4 == 0 ? _created.AddDays(1) : null
                }
            ]) with
        {
            KnownUsers = document.KnownUsers.With(userId, "user " + (number % 7))
        };
    }

    public override int CountRecords(SavedQueriesDocument document) => document.Queries.Count;

    public override JsonDocumentStore<SavedQueriesDocument> CreateStore(
        string file,
        int currentVersion,
        Func<SavedQueriesDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => currentVersion == CurrentVersion && migrations.Count == 0
            ? SavedQueryStore.CreateStore(file, logger, timeProvider)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        ((JsonObject)root["Queries"]![0]!)[name] = value;
        return true;
    }
}
