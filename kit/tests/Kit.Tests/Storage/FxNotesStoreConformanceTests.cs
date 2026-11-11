using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;

namespace Jellyfin.Plugin.Kit.Tests.Storage;

/// <summary>
/// The kit's own run of the store conformance suite, on a document at schema version 3 with migrations
/// 1 → 2 → 3, fixtures of every version and extension data on its records.
/// </summary>
public sealed class FxNotesStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new FxNotesStoreConformance();
}

internal sealed class FxNotesStoreConformance : StoreConformance<FxNotesDocument>
{
    /// <summary>Version 1: no <c>Version</c> property (it didn't exist yet), the text in <c>Body</c>.</summary>
    public const string Version1Fixture = """
        {
          "Notes": [
            { "Id": "00000001-4b1d-4c2e-9a0f-112233445566", "UserId": "9fa5c677-8cf7-4a94-bff0-c82a60581ccf", "ItemId": "ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5", "Body": "first", "Kind": "Plain", "StartTicks": 10000000, "CreatedAt": "2026-01-01T10:00:00Z", "UpdatedAt": "2026-01-01T10:00:00Z" },
            { "Id": "00000002-4b1d-4c2e-9a0f-112233445566", "UserId": "9fa5c677-8cf7-4a94-bff0-c82a60581ccf", "ItemId": "ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5", "Body": "second", "Kind": "Pinned", "StartTicks": 20000000, "EndTicks": 30000000, "CreatedAt": "2026-01-02T10:00:00Z", "UpdatedAt": "2026-01-03T10:00:00Z" }
          ]
        }
        """;

    /// <summary>Version 2: <c>Text</c> and an optional <c>Tag</c>, plus a document field and a record field version 3 doesn't know.</summary>
    public const string Version2Fixture = """
        {
          "Version": 2,
          "Notes": [
            { "Id": "00000001-4b1d-4c2e-9a0f-112233445566", "UserId": "9fa5c677-8cf7-4a94-bff0-c82a60581ccf", "ItemId": "ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5", "Text": "first", "Tag": "favourite", "Kind": "Plain", "StartTicks": 10000000, "CreatedAt": "2026-01-01T10:00:00Z", "UpdatedAt": "2026-01-01T10:00:00Z", "Colour": "red" },
            { "Id": "00000002-4b1d-4c2e-9a0f-112233445566", "UserId": "9fa5c677-8cf7-4a94-bff0-c82a60581ccf", "ItemId": "ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5", "Text": "second", "Kind": "Pinned", "StartTicks": 20000000, "CreatedAt": "2026-01-02T10:00:00Z", "UpdatedAt": "2026-01-02T10:00:00Z" },
            { "Id": "00000003-4b1d-4c2e-9a0f-112233445566", "UserId": "0038ee0f-9abc-4d55-9ef5-abcf8520c9bf", "ItemId": "ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5", "Text": "third", "Kind": "Plain", "StartTicks": 30000000, "CreatedAt": "2026-01-03T10:00:00Z", "UpdatedAt": "2026-01-03T10:00:00Z" }
          ],
          "UserPreferences": { "9fa5c677-8cf7-4a94-bff0-c82a60581ccf": { "AskForName": true } }
        }
        """;

    /// <summary>Version 3, the current version.</summary>
    public const string Version3Fixture = """
        {
          "Version": 3,
          "Notes": [
            { "Id": "00000001-4b1d-4c2e-9a0f-112233445566", "UserId": "9fa5c677-8cf7-4a94-bff0-c82a60581ccf", "ItemId": "ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5", "Text": "only", "Tags": [ "a", "b" ], "Kind": "Pinned", "StartTicks": 10000000, "CreatedAt": "2026-01-01T10:00:00Z", "UpdatedAt": "2026-01-01T10:00:00Z" }
          ]
        }
        """;

    public override int CurrentVersion => FxNotesDocument.CurrentVersion;

    public override string StoreFileName => "notes.json";

    public override IReadOnlyList<IJsonMigration> Migrations => FxNotesDocument.Migrations;

    public override IReadOnlyList<StoreFixture> Fixtures =>
    [
        new("synthetic version 1, without Version", Encoding.UTF8.GetBytes(Version1Fixture), 1, 2),
        new("synthetic version 2", Encoding.UTF8.GetBytes(Version2Fixture), 2, 3),
        new("synthetic version 3", Encoding.UTF8.GetBytes(Version3Fixture), 3, 1)
    ];

    public override FxNotesDocument CreateEmpty() => FxNotesDocument.CreateEmpty();

    public override FxNotesDocument AddRecord(FxNotesDocument document, int number) => document.Add(FxNote.Create(number));

    public override int CountRecords(FxNotesDocument document) => document.Notes.Count;

    public override FxNotesDocument CreateDocument(int records)
        => FxNotesDocument.CreateEmpty() with { Notes = Enumerable.Range(0, records).Select(FxNote.Create).ToList() };

    public override void BreakDeserialization(JsonObject root)
    {
        // An enum value this version doesn't know: the realistic failure.
        if (root["Notes"] is JsonArray { Count: > 0 } notes && notes[0] is JsonObject note)
        {
            note["Kind"] = "Exploded";
            return;
        }

        base.BreakDeserialization(root);
    }

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        var note = (JsonObject)((JsonArray)root["Notes"]!)[0]!;
        note[name] = value;
        return true;
    }
}
