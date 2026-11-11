using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Tests.Storage;

/// <summary>
/// The kit's store conformance suite on
/// <c>accessroles.json</c>, schema 1: atomic writes, <c>.bak</c> recovery, the corrupt-file quarantine, read-only for a
/// newer version and for a failed migration, extension data, concurrent writes, snapshots, restore and export.
/// </summary>
public sealed class AccessRolesStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new AccessRolesStoreConformance();
}

/// <summary>
/// <c>accessroles.json</c> for the conformance suite: a record is an assignment (with its ledger entry and the user's
/// name, and every third one a role and a known library), so every kind of record goes through every check.
/// </summary>
internal sealed class AccessRolesStoreConformance : StoreConformance<AccessRolesDocument>
{
    public override int CurrentVersion => AccessRolesDocument.CurrentVersion;

    public override string StoreFileName => AccessRolesPaths.StoreFileName;

    public override IReadOnlyList<IJsonMigration> Migrations => AccessRolesStore.Migrations;

    public override IReadOnlyList<StoreFixture> Fixtures =>
    [
        new("the schema 1 example", StoreFixtures.Read(StoreFixtures.SchemaExample), 1, StoreFixtures.SchemaExampleRecords),
        new("values of a newer build", StoreFixtures.Read(StoreFixtures.NewerValues), 1, StoreFixtures.NewerValuesRecords)
    ];

    public override AccessRolesDocument CreateEmpty() => AccessRolesDocument.CreateEmpty();

    public override AccessRolesDocument AddRecord(AccessRolesDocument document, int number) => TestDocuments.AddRecord(document, number);

    // All at once: one at a time copies every list and the ledger again for each record.
    public override AccessRolesDocument CreateDocument(int records) => TestDocuments.WithRecords(CreateEmpty(), Enumerable.Range(0, records).ToList());

    public override int CountRecords(AccessRolesDocument document) => document.Assignments.Count;

    // The plugin's own store at the current version; the suite's extra-version runs go through the base. The suite's
    // expected messages name a recovery on the settings page, which Access Roles' status page doesn't offer: only that
    // wording differs (AccessRolesDocumentTests checks the plugin's own).
    public override JsonDocumentStore<AccessRolesDocument> CreateStore(
        string file,
        int currentVersion,
        Func<AccessRolesDocument> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => currentVersion == CurrentVersion && migrations.Count == 0
            ? AccessRolesStore.CreateDocumentStore(file, logger, timeProvider, recoveryPage: true)
            : base.CreateStore(file, currentVersion, createEmpty, migrations, logger, timeProvider);

    public override bool AddUnknownRecordField(JsonObject root, string name, JsonNode value)
    {
        ((JsonObject)root["Assignments"]![0]!)[name] = value;
        return true;
    }
}
