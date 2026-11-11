using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// The kit's store conformance suite on the developer library demo's <c>library-demo.json</c>.
/// </summary>
public sealed class LibraryDemoStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new LibraryDemoStoreConformance();
}

internal sealed class LibraryDemoStoreConformance : StoreConformance<LibraryDemoDocument>
{
    private static readonly DateTime _created = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    public override int CurrentVersion => LibraryDemoDocument.CurrentVersion;

    public override string StoreFileName => "library-demo.json";

    public override LibraryDemoDocument CreateEmpty() => LibraryDemoDocument.CreateEmpty();

    public override LibraryDemoDocument AddRecord(LibraryDemoDocument document, int number)
    {
        var itemId = new Guid(number, 0x1d, 0x4c, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66);
        return LibraryDemoRecords.WithRecords(
            document,
            [
                .. document.Records,
                new LibraryDemoRecord
                {
                    Id = new Guid(number, 0x4b1d, 0x4c2e, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
                    UserId = new Guid(number % 7, 0x0a, 0x4b, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
                    ItemId = itemId,
                    Identity = new IdentitySnapshot
                    {
                        ItemId = itemId,
                        Kind = "Episode",
                        Name = "Episode " + number + ": Amélie, café scène ✂ 日本語",
                        SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "900401" },
                        Season = 1,
                        Episode = number,
                        RunTimeTicks = 300_000_000
                    },
                    Note = "note " + number,
                    MediaSourceId = itemId.ToString("N"),
                    CreatedAt = _created.AddSeconds(number),
                    AttachedUtc = _created.AddSeconds(number),
                    DetachedUtc = number % 3 == 0 ? _created.AddDays(1) : null,
                    DetachedBy = number % 3 == 0 ? "Container" : null,
                    RelinkStrength = number % 5 == 0 ? MatchStrength.Strong : null
                }
            ]);
    }

    public override int CountRecords(LibraryDemoDocument document) => document.Records.Count;

    public override JsonDocumentStore<LibraryDemoDocument> CreateStore(
        string file,
        int currentVersion,
        Func<LibraryDemoDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => currentVersion == CurrentVersion && migrations.Count == 0
            ? LibraryDemoRecords.CreateStore(file, logger, timeProvider)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        ((JsonObject)root["Records"]![0]!)[name] = value;
        return true;
    }
}
