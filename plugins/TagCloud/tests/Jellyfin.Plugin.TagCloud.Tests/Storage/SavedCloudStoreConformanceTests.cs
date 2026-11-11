using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Clouds;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagCloud.Tests.Storage;

/// <summary>
/// The kit's store conformance suite on the saved clouds' file.
/// </summary>
public sealed class SavedCloudStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new SavedCloudStoreConformance();
}

internal sealed class SavedCloudStoreConformance : StoreConformance<SavedCloudDocument>
{
    private static readonly DateTime Created = new(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);

    public override int CurrentVersion => SavedCloudDocument.CurrentVersion;

    public override string StoreFileName => SavedCloudOwner.FileName;

    // A file Tag Cloud 1.0.0 saved (schema 1): two users' clouds, one shared with libraries, narrowing words and a look of
    // every non-default choice, a duplicate of it, and a name outside ASCII. Generated with the tagcloud/v1.0.0 build.
    public override IReadOnlyList<StoreFixture> Fixtures =>
    [
        new("a 1.0.0 save", ReadFixture("clouds-1.0.0.json"), 1, 4)
    ];

    public override SavedCloudDocument CreateEmpty() => SavedCloudDocument.CreateEmpty();

    public override SavedCloudDocument AddRecord(SavedCloudDocument document, int number)
    {
        var owner = new Guid(number % 7, 0x0a, 0x4b, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66);
        return SavedCloudOwner.WithClouds(
            document,
            [
                .. document.Clouds,
                new SavedCloudRecord
                {
                    Id = new Guid(number, 0x4b1d, 0x4c2e, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
                    OwnerId = owner,
                    Name = "Cloud " + number + ": Amélie, café scène ✂ 日本語",
                    Shared = number % 2 == 0,
                    CreatedUtc = Created.AddSeconds(number),
                    UpdatedUtc = Created.AddSeconds(number),
                    Source = (TagCloudSource)(number % 6),
                    Scope = (TagCloudScope)(number % 3),
                    LibraryIds = [owner],
                    NarrowKeys = ["slasher", "los angeles, california"],
                    Look = new CloudLook { MaxWords = 250, Seed = number, Palette = "Rainbow" }
                }
            ]) with
        {
            KnownUsers = document.KnownUsers.With(owner, "user " + (number % 7))
        };
    }

    public override int CountRecords(SavedCloudDocument document) => document.Clouds.Count;

    public override JsonDocumentStore<SavedCloudDocument> CreateStore(
        string file,
        int currentVersion,
        Func<SavedCloudDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => currentVersion == CurrentVersion && migrations.Count == 0
            ? SavedCloudOwner.CreateStore(file, logger, timeProvider)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);

    private static byte[] ReadFixture(string name)
    {
        using var stream = typeof(SavedCloudStoreConformance).Assembly.GetManifestResourceStream("Fixtures." + name)
            ?? throw new InvalidOperationException("No embedded fixture " + name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        ((JsonObject)root["Clouds"]![0]!)[name] = value;
        return true;
    }
}
