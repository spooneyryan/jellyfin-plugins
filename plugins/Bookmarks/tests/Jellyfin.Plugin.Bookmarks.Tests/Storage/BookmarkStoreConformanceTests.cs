using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Storage.Migrations;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// The kit's store conformance suite on <c>bookmarks.json</c> at schema 2: the file Bookmarks
/// 1.0.0 wrote is upgraded by the real 1→2 migration, and the schema 2 fixture loads unchanged.
/// </summary>
public sealed class BookmarkStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new BookmarkStoreConformance();
}

internal sealed class BookmarkStoreConformance : StoreConformance<BookmarkStoreDocument>
{
    private static readonly DateTime _created = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    // The plugin's migration list, as the plugin builds it; the suite compares it by reference to use the plugin's factory.
    private static readonly IReadOnlyList<IJsonMigration> _migrations = BookmarkStore.Migrations(
        new BookmarkStoreMigration1To2(Path.Combine(Path.GetTempPath(), "bookmarks-conformance", "clips"), () => null, _ => null));

    public override int CurrentVersion => BookmarkStoreDocument.CurrentVersion;

    public override string StoreFileName => "bookmarks.json";

    public override IReadOnlyList<IJsonMigration> Migrations => _migrations;

    public override IReadOnlyList<StoreFixture> Fixtures =>
    [
        new("bookmarks.json written by Bookmarks 1.0.0", StoreFixtures.Read(StoreFixtures.Bookmarks100), 1, 10),
        new("bookmarks.json written by the Bookmarks 1.0 development build", StoreFixtures.Read(StoreFixtures.Bookmarks10DevSandbox), 1, StoreFixtures.Bookmarks10DevSandboxRecords),
        new("bookmarks.json at schema 2 with every field", StoreFixtures.Read(StoreFixtures.Schema2), 2, StoreFixtures.Schema2Records)
    ];

    public override BookmarkStoreDocument CreateEmpty() => BookmarkStoreDocument.CreateEmpty();

    public override BookmarkStoreDocument AddRecord(BookmarkStoreDocument document, int number)
        => BookmarkStore.WithBookmarks(document, [.. document.Bookmarks, CreateBookmark(number)]);

    public override int CountRecords(BookmarkStoreDocument document) => document.Bookmarks.Count;

    public override BookmarkStoreDocument CreateDocument(int records)
        => BookmarkStore.WithBookmarks(CreateEmpty(), Enumerable.Range(0, records).Select(CreateBookmark));

    public override JsonDocumentStore<BookmarkStoreDocument> CreateStore(
        string file,
        int currentVersion,
        Func<BookmarkStoreDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
    {
        // Bookmarks' own factory whenever the suite doesn't vary the version, so it checks exactly what the plugin runs.
        return currentVersion == CurrentVersion && ReferenceEquals(migrations, _migrations)
            ? BookmarkStore.CreateDocumentStore(file, migrations, logger, timeProvider)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);
    }

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        // Bookmark.Extra keeps it.
        var bookmark = (JsonObject)((JsonArray)root["Bookmarks"]!)[0]!;
        bookmark[name] = value;
        return true;
    }

    public override void BreakDeserialization(JsonObject root)
    {
        // A clip status this version doesn't know: the realistic failure.
        var clip = (root["Bookmarks"] as JsonArray)?.OfType<JsonObject>().Select(b => b["Clip"]).OfType<JsonObject>().FirstOrDefault();
        if (clip is not null)
        {
            clip["Status"] = "Exploded";
            return;
        }

        base.BreakDeserialization(root);
    }

    // Shaped like real schema 2 data: every other record has a finished clip, some are named, imported, detached,
    // relinked or clip-only tombstones.
    private static Bookmark CreateBookmark(int number)
    {
        var start = (number % 5400) * TestData.OneSecond;
        var item = new Guid("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5");
        var created = _created.AddSeconds(number);
        return new Bookmark
        {
            Id = new Guid(number, 0x14bc, 0x4e9a, 0xa9, 0xc6, 0x07, 0xc0, 0xb4, 0x6a, 0x92, 0x10),
            UserId = new Guid("9fa5c677-8cf7-4a94-bff0-c82a60581ccf"),
            ItemId = item,
            MediaSourceId = number % 2 == 0 ? "ba7ef8f50757f5f60cae61b9b7fd19f5" : null,
            AudioStreamIndex = number % 2 == 0 ? 1 : null,
            StartTicks = start,
            EndTicks = number % 2 == 0 ? start + (15 * TestData.OneSecond) : null,
            Name = number % 3 == 0 ? "Bank job " + number : null,
            Note = number % 3 == 1 ? "Bookmark " + number + ": the reflection in the window" : null,
            ItemName = "Long Test Movie (2021)",
            Identity = new IdentitySnapshot
            {
                ItemId = item,
                Kind = "Movie",
                Name = "Long Test Movie",
                Year = 2021,
                RunTimeTicks = 5400 * TestData.OneSecond,
                ProviderIds = number % 4 == 0 ? new Dictionary<string, string> { ["Tmdb"] = "900001" } : new Dictionary<string, string>(),
                Path = @"C:\media\Long Test Movie (2021)\Long Test Movie (2021).mkv"
            },
            Origin = number % 5 == 0 ? BookmarkOrigin.JellyfinEnhanced : BookmarkOrigin.Native,
            AttachedUtc = created,
            DetachedUtc = number % 7 == 0 ? created.AddDays(1) : null,
            RelinkedUtc = number % 11 == 0 ? created.AddDays(2) : null,
            RelinkStrength = number % 11 == 0 ? MatchStrength.Strong : null,
            DeletedUtc = number % 2 == 0 && number % 13 == 0 ? created.AddDays(3) : null,
            CreatedAt = created,
            UpdatedAt = created,
            Clip = number % 2 == 0
                ? new ClipInfo
                {
                    Status = BookmarkClipStatus.Completed,
                    Mode = BookmarkClipMode.Fast,
                    StartTicks = start,
                    EndTicks = start + (15 * TestData.OneSecond),
                    RequestedAt = created,
                    CompletedAt = created.AddSeconds(1),
                    FilePath = @"C:\clips\admin\Long Test Movie (2021) - Bookmark " + number + ".mkv",
                    FileSize = 698007,
                    FileStartTicks = start,
                    FileEndTicks = start + (15 * TestData.OneSecond),
                    EstimatedBytes = 700_000
                }
                : null
        };
    }
}
