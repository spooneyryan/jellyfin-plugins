using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// The kit's store conformance suite on the developer user data demo's <c>userdata-demo.json</c>.
/// </summary>
public sealed class UserDataDemoStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new UserDataDemoStoreConformance();
}

internal sealed class UserDataDemoStoreConformance : StoreConformance<UserDataDemoDocument>
{
    private static readonly DateTime _created = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    public override int CurrentVersion => UserDataDemoDocument.CurrentVersion;

    public override string StoreFileName => "userdata-demo.json";

    public override UserDataDemoDocument CreateEmpty() => UserDataDemoDocument.CreateEmpty();

    public override UserDataDemoDocument AddRecord(UserDataDemoDocument document, int number)
    {
        var userId = new Guid(number % 7, 0x0a, 0x4b, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66);
        return UserDataDemoOwner.WithRecords(
            document,
            [
                .. document.Records,
                new UserDataDemoRecord
                {
                    Id = new Guid(number, 0x4b1d, 0x4c2e, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
                    UserId = userId,
                    Text = "Record " + number + ": Amélie, café scène ✂ 日本語",
                    HasFile = number % 2 == 0,
                    DeletedUtc = number % 5 == 0 ? _created.AddDays(1) : null,
                    CreatedAt = _created.AddSeconds(number),
                    UpdatedAt = _created.AddSeconds(number)
                }
            ]) with
        {
            KnownUsers = document.KnownUsers.With(userId, "user " + (number % 7))
        };
    }

    public override int CountRecords(UserDataDemoDocument document) => document.Records.Count;

    public override JsonDocumentStore<UserDataDemoDocument> CreateStore(
        string file,
        int currentVersion,
        Func<UserDataDemoDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => currentVersion == CurrentVersion && migrations.Count == 0
            ? UserDataDemoOwner.CreateStore(file, logger, timeProvider)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        ((JsonObject)root["Records"]![0]!)[name] = value;
        return true;
    }
}
