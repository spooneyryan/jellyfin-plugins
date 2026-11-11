using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.Kit.Tests.Storage;

/// <summary>
/// A pretend plugin document at schema version 3, with two real migrations: version 1 stored the note text as
/// <c>Body</c>, version 2 renamed it to <c>Text</c> and had one optional <c>Tag</c>, version 3 has a <c>Tags</c> list.
/// Records carry extension data, as the kit asks of main records. Shaped like a bookmark (about 450 bytes a record), so
/// the timing test is representative.
/// </summary>
internal sealed record FxNotesDocument : IVersionedDocument
{
    public const int CurrentVersion = 3;

    public int Version { get; init; } = CurrentVersion;

    public IReadOnlyList<FxNote> Notes { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    public static IReadOnlyList<IJsonMigration> Migrations { get; } = [new FxNotesMigration1To2(), new FxNotesMigration2To3()];

    public static FxNotesDocument CreateEmpty() => new();

    public FxNotesDocument Add(FxNote note) => this with { Notes = [.. Notes, note] };
}

internal sealed record FxNote
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public Guid ItemId { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public FxNoteKind Kind { get; init; }

    public long StartTicks { get; init; }

    public long? EndTicks { get; init; }

    public string? Comment { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    public static FxNote Create(int number) => new()
    {
        Id = new Guid(number, 0x4b1d, 0x4c2e, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
        UserId = new Guid("9fa5c677-8cf7-4a94-bff0-c82a60581ccf"),
        ItemId = new Guid("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5"),
        Text = "Note " + number + ": the reflection in the window, and the car outside",
        Tags = number % 3 == 0 ? ["favourite", "rewatch"] : [],
        Kind = number % 2 == 0 ? FxNoteKind.Pinned : FxNoteKind.Plain,
        StartTicks = 10_000_000L * number,
        EndTicks = number % 2 == 0 ? (10_000_000L * number) + 150_000_000 : null,
        CreatedAt = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc).AddSeconds(number),
        UpdatedAt = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc).AddSeconds(number)
    };
}

internal enum FxNoteKind
{
    Plain,
    Pinned
}

/// <summary>Version 1 → 2: <c>Body</c> becomes <c>Text</c>.</summary>
internal sealed class FxNotesMigration1To2 : IJsonMigration
{
    public int FromVersion => 1;

    public void Apply(JsonObject root)
    {
        foreach (var note in (root["Notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (note.TryGetPropertyValue("Body", out var body))
            {
                note.Remove("Body");
                note["Text"] = body;
            }
        }
    }
}

/// <summary>Version 2 → 3: the optional <c>Tag</c> becomes the <c>Tags</c> list.</summary>
internal sealed class FxNotesMigration2To3 : IJsonMigration
{
    public int FromVersion => 2;

    public void Apply(JsonObject root)
    {
        foreach (var note in (root["Notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var tags = new JsonArray();
            if (note.TryGetPropertyValue("Tag", out var tag))
            {
                note.Remove("Tag");
                if (tag is not null)
                {
                    tags.Add(tag);
                }
            }

            note["Tags"] = tags;
        }
    }
}
