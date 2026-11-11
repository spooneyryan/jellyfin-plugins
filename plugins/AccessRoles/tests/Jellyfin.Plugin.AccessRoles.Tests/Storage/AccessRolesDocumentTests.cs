using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Storage;

/// <summary>
/// Schema 1 of <c>accessroles.json</c>: the design's example reads field by field, a full document round-trips
/// in the design's shape, values and fields of a newer build are kept and flagged (the lenient rule), and a fresh
/// install gets its file at the first start.
/// </summary>
public sealed class AccessRolesDocumentTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    private string StoreFile => _directory.Combine("accessroles", AccessRolesPaths.StoreFileName);

    [Fact]
    public void TheSchemaExample_ReadsFieldByField()
    {
        using var store = OpenWith(StoreFixtures.Read(StoreFixtures.SchemaExample));
        var document = store.Current;

        Assert.Equal(StoreStatus.Ok, store.Status);
        var role = Assert.Single(document.Roles);
        Assert.Equal(new Guid("5b0e7d2a-3c4f-4a1b-9e8d-1f2a3b4c5d6e"), role.Id);
        Assert.Equal("Kid", role.Name);
        Assert.Equal("Children's accounts", role.Description);
        Assert.Equal(
            [AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.Home, AccessRolesLock.Controls, AccessRolesLock.QuickConnect, AccessRolesLock.EnhancedPanel, AccessRolesLock.AdminSurfaces, AccessRolesLock.UserDirectory],
            role.KnownLocks);
        Assert.True(role.Libraries.Mode.Is(AccessRolesRoleLibraryMode.Only));
        var reference = Assert.Single(role.Libraries.Refs);
        Assert.Equal(("Kids Movies", @"D:\Kids", false), (reference.NameAtSelection, Assert.Single(reference.Locations), reference.Orphaned));
        Assert.Equal(new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc), role.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, role.UpdatedUtc.Kind);

        var assignment = Assert.Single(document.Assignments);
        Assert.Equal(new Guid("a1c4e5f6-0718-4293-a4b5-c6d7e8f90a1b"), assignment.UserId);
        Assert.Equal([role.Id], assignment.RoleIds);
        Assert.Equal(AccessRolesLockOverride.Unlock, assignment.LockOverrides[AccessRolesLock.QuickConnect].Value);
        Assert.True(assignment.Libraries.Mode.Is(AccessRolesUserLibraryMode.Inherit));
        Assert.Empty(assignment.Libraries.Add);
        Assert.False(assignment.Libraries.ConfirmNoLibraries);
        Assert.True(assignment.State.Is(AccessRolesAssignmentState.Managed));
        Assert.False(assignment.HasUnknownValues);

        Assert.Equal([role.Id], document.DefaultRoleIds);
        var library = Assert.Single(document.KnownLibraries);
        Assert.Equal((reference.Id, "Kids Movies", true, AccessRolesLibraryState.Active), (library.Id, library.Name, library.Enabled, library.State.Value!.Value));
        Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), library.CreatedUtc);
        Assert.Empty(document.PendingRenames);

        var ledger = document.Ledger[assignment.UserId];
        Assert.True(ledger.OriginalEnableAllFolders);
        Assert.Empty(ledger.OriginalEnabledFolders);
        Assert.True(ledger.OriginalEnableUserPreferenceAccess);
        Assert.NotNull(ledger.LastWrite);
        Assert.False(ledger.LastWrite!.EnableAllFolders);
        Assert.Equal([reference.Id], ledger.LastWrite.EnabledFolders);
        Assert.Null(ledger.LastWrite.EnableUserPreferenceAccess);
        Assert.Equal("kid1", document.KnownUsers.NameOf(assignment.UserId));
    }

    [Fact]
    public async Task AFullDocument_RoundTrips_InTheDesignedShape()
    {
        var full = TestDocuments.WithRecords(AccessRolesDocument.CreateEmpty(), [0, 1, 2, 3, 4, 5, 51]);
        using (var store = AccessRolesStore.Create(StoreFile, NullLogger.Instance))
        {
            await store.UpdateAsync(_ => full, TestContext.Current.CancellationToken);
        }

        var written = await File.ReadAllTextAsync(StoreFile, Encoding.UTF8, TestContext.Current.CancellationToken);
        using (var reopened = AccessRolesStore.Create(StoreFile, NullLogger.Instance))
        {
            Assert.Equal(StoreStatus.Ok, reopened.Status);
            Assert.Equal(Json(full), Json(reopened.Current));
        }

        // The design's property names, in its order; enums by name; user-id keys as Jellyfin writes them ("N").
        var root = JsonNode.Parse(written)!.AsObject();
        Assert.Equal(["Version", "Roles", "Assignments", "DefaultRoleIds", "KnownLibraries", "PendingRenames", "Ledger", "PreferenceMirror", "KnownUsers"], root.Select(p => p.Key));
        Assert.Equal(["OriginalEnableUserPreferenceAccess", "WrittenUtc"], root["PreferenceMirror"]![TestDocuments.UserId(4).ToString("N")]!.AsObject().Select(p => p.Key));
        Assert.Equal(1, (int)root["Version"]!);
        var role = root["Roles"]![0]!.AsObject();
        Assert.Equal(["Id", "Name", "Description", "Locks", "Libraries", "CreatedUtc", "UpdatedUtc"], role.Select(p => p.Key));
        Assert.Equal(["Profile", "Home", "AdminSurfaces"], role["Locks"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("AllExcept", (string)role["Libraries"]!["Mode"]!);
        var assignment = root["Assignments"]![1]!.AsObject();
        Assert.Equal(["UserId", "RoleIds", "LockOverrides", "Libraries", "State", "UpdatedUtc"], assignment.Select(p => p.Key));
        Assert.Equal("Lock", (string)assignment["LockOverrides"]!["QuickConnect"]!);
        Assert.Equal("All", (string)assignment["Libraries"]!["Mode"]!);
        Assert.Equal("Managed", (string)assignment["State"]!);
        Assert.Equal(["OriginalEnableAllFolders", "OriginalEnabledFolders", "OriginalEnableUserPreferenceAccess", "CapturedUtc", "LastWrite"], root["Ledger"]![TestDocuments.UserId(0).ToString("N")]!.AsObject().Select(p => p.Key));
        Assert.All(root["Ledger"]!.AsObject().Select(p => p.Key), key => Assert.Matches("^[0-9a-f]{32}$", key));
        Assert.Equal("user3", (string)root["KnownUsers"]![TestDocuments.UserId(3).ToString("N")]!);
        Assert.Equal("2026-10-01T18:00:00Z", (string)root["Roles"]![0]!["CreatedUtc"]!);
    }

    [Fact]
    public async Task ValuesAndFieldsOfANewerBuild_AreKept_Flagged_AndIgnored()
    {
        using (var store = OpenWith(StoreFixtures.Read(StoreFixtures.NewerValues)))
        {
            var document = store.Current;
            Assert.Equal(StoreStatus.Ok, store.Status);

            var role = Assert.Single(document.Roles);
            Assert.Equal(["Playback", "Downloads", "Home"], role.Locks.Select(l => l.Name));
            Assert.Equal([AccessRolesLock.Playback, AccessRolesLock.Home], role.KnownLocks);
            Assert.True(role.HasUnknownLibraryMode);
            Assert.Equal("Smart", role.Libraries.Mode.Name);

            var night1 = document.Assignments[0];
            Assert.True(night1.HasUnknownValues);
            Assert.Equal("Paused", night1.State.Name);
            Assert.Null(night1.State.Value);
            Assert.Equal("Sometimes", night1.LockOverrides[LenientEnum<AccessRolesLock>.Parse("Downloads")].Name);
            Assert.Equal(AccessRolesLockOverride.Lock, night1.LockOverrides[AccessRolesLock.Profile].Value);
            var night2 = document.Assignments[1];
            Assert.True(night2.HasUnknownValues);
            Assert.True(night2.State.Is(AccessRolesAssignmentState.Managed));
            Assert.Equal("Everything", night2.Libraries.Mode.Name);
            Assert.Equal("Archived", Assert.Single(document.KnownLibraries).State.Name);

            // A change elsewhere in the document keeps every unknown value and field.
            await store.UpdateAsync(d => d with { DefaultRoleIds = [role.Id] }, TestContext.Current.CancellationToken);
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(StoreFile, TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal("Teen", (string)root["Presets"]![0]!["Name"]!);
        var role2 = root["Roles"]![0]!;
        Assert.Equal(["Playback", "Downloads", "Home"], role2["Locks"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("21:00", (string)role2["Schedule"]!["From"]!);
        Assert.Equal("Smart", (string)role2["Libraries"]!["Mode"]!);
        Assert.Equal("unwatched", (string)role2["Libraries"]!["Rule"]!);
        var assignment = root["Assignments"]![0]!;
        Assert.Equal("Paused", (string)assignment["State"]!);
        Assert.Equal("kept for the newer build", (string)assignment["Note"]!);
        Assert.Equal(3, (int)assignment["Libraries"]!["Quota"]!);
        Assert.Equal("Sometimes", (string)assignment["LockOverrides"]!["Downloads"]!);
        Assert.Equal("Lock", (string)assignment["LockOverrides"]!["Profile"]!);
        Assert.Equal(2, (int)root["KnownLibraries"]![0]!["Shelf"]!);
        Assert.Equal(1, (int)root["PendingRenames"]![0]!["Attempts"]!);
        Assert.Equal("x", (string)root["Ledger"]!["b2d5f607182943a4b5c6d7e8f90a1b2c"]!["OriginalBlockedTags"]![0]!);
    }

    [Fact]
    public void NullsAndMissingCollections_ReadAsEmpty_AndNullEntriesAreDropped()
    {
        const string File1 = """
            {"Version":1,"Roles":null,"Assignments":[null,{"UserId":"a1c4e5f6-0718-4293-a4b5-c6d7e8f90a1b","RoleIds":null,"LockOverrides":null,"Libraries":null,"State":null}],
             "KnownLibraries":[{"Id":"3c1d9f8e-7b6a-4c5d-8e9f-0a1b2c3d4e5f","Name":null,"Locations":null}],"Ledger":null,"KnownUsers":null}
            """;
        using var store = OpenWith(Encoding.UTF8.GetBytes(File1));
        var document = store.Current;

        Assert.Equal(StoreStatus.Ok, store.Status);
        Assert.Empty(document.Roles);
        var assignment = Assert.Single(document.Assignments);
        Assert.Empty(assignment.RoleIds);
        Assert.Empty(assignment.LockOverrides);
        Assert.True(assignment.Libraries.Mode.Is(AccessRolesUserLibraryMode.Inherit));

        // A null state fails closed: an unknown value, as a newer build's would be.
        Assert.False(assignment.State.IsKnown);
        Assert.True(assignment.HasUnknownValues);
        Assert.Empty(document.DefaultRoleIds);
        Assert.Equal((string.Empty, 0), (document.KnownLibraries[0].Name, document.KnownLibraries[0].Locations.Count));
        Assert.Empty(document.PendingRenames);
        Assert.Empty(document.Ledger);
        Assert.Empty(document.KnownUsers);
    }

    [Fact]
    public void AMissingField_TakesItsDefault()
    {
        using var store = OpenWith(Encoding.UTF8.GetBytes("""{"Assignments":[{"UserId":"a1c4e5f6-0718-4293-a4b5-c6d7e8f90a1b"}],"Roles":[{"Name":"r"}],"KnownLibraries":[{"Name":"l"}]}"""));
        var document = store.Current;

        Assert.Equal(1, document.Version);
        Assert.True(document.Assignments[0].State.Is(AccessRolesAssignmentState.Managed));
        Assert.False(document.Assignments[0].HasUnknownValues);
        Assert.True(document.Roles[0].Libraries.Mode.Is(AccessRolesRoleLibraryMode.NotManaged));
        Assert.True(document.KnownLibraries[0].Enabled);
        Assert.True(document.KnownLibraries[0].State.Is(AccessRolesLibraryState.Active));
        Assert.Null(document.KnownLibraries[0].CreatedUtc);
    }

    [Fact]
    public void AStateThatIsNotAName_MakesTheFileUnreadable()
    {
        // Lenient means names a newer build may write, not any JSON: an object where a name belongs is a broken file. Since
        // the kit's JSON store hardening (merged from main) such a file never starts empty on its own: with no .bak or
        // snapshot to take its place the store is read-only (ReadOnlyDataUnreadable), and the file stays where it is.
        using var store = OpenWith(Encoding.UTF8.GetBytes("""{"Version":1,"Assignments":[{"UserId":"a1c4e5f6-0718-4293-a4b5-c6d7e8f90a1b","State":{"x":1}}]}"""));

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        Assert.True(store.IsReadOnly);
        Assert.True(File.Exists(StoreFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StoreFile)!, "*.corrupt-*"));

        // The status page offers no recovery, so the banner names the way out without one.
        Assert.EndsWith("To go on, stop the server, put a readable copy of accessroles.json back (or move what couldn't be read aside, to start empty), and start the server again.", store.Documents.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("settings page", store.Documents.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TimesWithoutAZone_AreTakenAsUtc()
    {
        using var store = OpenWith(Encoding.UTF8.GetBytes("""{"Roles":[{"Name":"r","CreatedUtc":"2026-10-01T18:00:00","UpdatedUtc":"2026-10-01T20:00:00+02:00"}]}"""));
        var role = store.Current.Roles[0];

        Assert.Equal(new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc), role.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, role.CreatedUtc.Kind);
        Assert.Equal(new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc), role.UpdatedUtc);
    }

    [Fact]
    public async Task CreateFileIfMissing_WritesTheEmptySchema1FileOnce()
    {
        using var store = AccessRolesStore.Create(StoreFile, NullLogger.Instance);

        Assert.True(await store.CreateFileIfMissingAsync(TestContext.Current.CancellationToken));
        Assert.False(await store.CreateFileIfMissingAsync(TestContext.Current.CancellationToken));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(StoreFile, TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal(1, (int)root["Version"]!);
        Assert.Empty(root["Roles"]!.AsArray());
        Assert.Empty(root["Assignments"]!.AsArray());
        Assert.Empty(root["Ledger"]!.AsObject());
        Assert.Equal([StoreFile], Directory.GetFiles(Path.GetDirectoryName(StoreFile)!));
    }

    [Fact]
    public async Task CreateFileIfMissing_LeavesAnExistingFileAlone()
    {
        var content = StoreFixtures.Read(StoreFixtures.SchemaExample);
        using var store = OpenWith(content);

        Assert.False(await store.CreateFileIfMissingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(content, await File.ReadAllBytesAsync(StoreFile, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"Version":2,"Roles":[]}""", nameof(StoreStatus.ReadOnlyNewerVersion))]
    [InlineData("not json", nameof(StoreStatus.ReadOnlyDataUnreadable))]
    [InlineData("  ", nameof(StoreStatus.RecoveredEmpty))]
    public async Task CreateFileIfMissing_WritesNothing_WhileTheStoreIsntOk(string content, string statusName)
    {
        // A newer version's file, an unreadable one left in place, and an empty one moved aside: Degraded mode
        // must find them as they are.
        var status = Enum.Parse<StoreStatus>(statusName);
        using var store = OpenWith(Encoding.UTF8.GetBytes(content));

        Assert.Equal(status, store.Status);
        Assert.False(await store.CreateFileIfMissingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(status != StoreStatus.RecoveredEmpty, File.Exists(StoreFile));
    }

    [Fact]
    public void Migrations_AreNone_ForSchema1()
    {
        Assert.Empty(AccessRolesStore.Migrations);
        Assert.Equal(1, AccessRolesDocument.CurrentVersion);
    }

    public void Dispose() => _directory.Dispose();

    private static string Json(AccessRolesDocument document) => JsonSerializer.Serialize(document, JsonStoreFormat.Options);

    private AccessRolesStore OpenWith(byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        File.WriteAllBytes(StoreFile, content);
        return AccessRolesStore.Create(StoreFile, NullLogger.Instance);
    }
}
