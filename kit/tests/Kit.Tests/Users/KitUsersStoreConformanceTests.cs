using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Kit.Tests.Users;

/// <summary>
/// The store conformance suite on the kit's own <c>kit-users.json</c>, the orphan marks of the
/// reconciler.
/// </summary>
public sealed class KitUsersStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new KitUsersStoreConformance();
}

internal sealed class KitUsersStoreConformance : StoreConformance<KitUsersDocument>
{
    public override int CurrentVersion => KitUsersDocument.CurrentVersion;

    public override string StoreFileName => KitUsersDocument.FileName;

    public override KitUsersDocument CreateEmpty() => KitUsersDocument.CreateEmpty();

    public override KitUsersDocument AddRecord(KitUsersDocument document, int number) => document with
    {
        Orphans =
        [
            .. document.Orphans,
            new OrphanMark
            {
                UserId = new Guid(number, 0x0b, 0x4c, 0x9a, 0x0f, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
                MarkedUtc = new DateTime(2026, 10, 1, 3, 10, 0, DateTimeKind.Utc).AddSeconds(number),
                Source = (OrphanMarkSource)(number % 3)
            }
        ]
    };

    public override int CountRecords(KitUsersDocument document) => document.Orphans.Count;

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        ((JsonObject)root["Orphans"]![0]!)[name] = value;
        return true;
    }
}
