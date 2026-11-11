using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Storage;

/// <summary>
/// The import helpers a plugin's adapter uses: <see cref="ImportMerge"/>, <see cref="ImportUserMap"/> and
/// <see cref="ImportRequest"/>.
/// </summary>
public sealed class ImportHelpersTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid D = Guid.Parse("dddddddd-0000-4000-8000-000000000004");
    private static readonly Guid E = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    private static readonly HashSet<Guid> NoneDeleted = [];

    [Fact]
    public void Merge_ARepeatedIncomingIdCountsOnce_ItsLastCopyWins()
    {
        var existing = new[] { FxNote.Create(1) };
        var incoming = new[] { FxNote.Create(2) with { Text = "first" }, FxNote.Create(2) with { Text = "second" } };

        var result = ImportMerge.Merge(existing, incoming, n => n.Id, n => n.UpdatedAt, ImportMerge.JsonContent<FxNote>());

        Assert.Equal(["Note 1: the reflection in the window, and the car outside", "second"], result.Records.Select(r => r.Text));
        Assert.Equal(new ImportCounts { Incoming = 1, Added = 1 }, result.Counts);
    }

    [Fact]
    public void JsonContent_ComparesListsAndExtensionDataByContent()
    {
        var comparer = ImportMerge.JsonContent<FxNote>();
        var note = FxNote.Create(3);

        Assert.True(comparer.Equals(note, note with { Tags = [.. note.Tags] }));
        Assert.False(comparer.Equals(note, note with { Tags = ["other"] }));
        Assert.False(comparer.Equals(note, note with { Extra = new() { ["X"] = System.Text.Json.JsonSerializer.SerializeToElement(1) } }));
        Assert.False(comparer.Equals(note, null));
    }

    [Fact]
    public void UserMap_ById_KeepsKnownIds()
    {
        var map = ImportUserMap.Build(
            new Dictionary<Guid, int> { [A] = 3, [B] = 1, [Guid.Empty] = 5 },
            [new ExportUser(A, "alice"), new ExportUser(B, "bob")],
            new Dictionary<Guid, string> { [A] = "Alice" },
            NoneDeleted,
            MapUsersBy.Id);

        Assert.True(map.TryMap(A, out var a));
        Assert.Equal(A, a);
        Assert.False(map.TryMap(B, out _));
        Assert.Equal([new ImportUserMapping(A, A, "Alice", 3)], map.Mapped);
        Assert.Equal([new ImportUnknownUser(B, "bob", 1, ImportUnknownUser.NotOnThisServer)], map.Unknown);
    }

    [Fact]
    public void UserMap_ById_TheSameIdIsTheSameAccount_DeletedUsersAreReportedAsSuch()
    {
        // A: deleted on the exporting server, but this server (a restore from before) still has the account: the same person.
        var map = ImportUserMap.Build(
            new Dictionary<Guid, int> { [A] = 1, [B] = 2, [C] = 3 },
            [new ExportUser(A, "alice", Deleted: true), new ExportUser(B, "bob", Deleted: true), new ExportUser(C, "carol")],
            new Dictionary<Guid, string> { [A] = "alice" },
            new HashSet<Guid> { C },
            MapUsersBy.Id);

        Assert.Equal([new ImportUserMapping(A, A, "alice", 1)], map.Mapped);
        Assert.Equal(
            [new ImportUnknownUser(B, "bob", 2, ImportUnknownUser.DeletedOnExportingServer), new ImportUnknownUser(C, "carol", 3, ImportUnknownUser.DeletedOnThisServer)],
            map.Unknown);
    }

    [Fact]
    public void UserMap_ByName_IgnoresCase_AndNeedsANameAndOneMatch()
    {
        var newA = Guid.NewGuid();
        var twin1 = Guid.NewGuid();
        var twin2 = Guid.NewGuid();
        var map = ImportUserMap.Build(
            new Dictionary<Guid, int> { [A] = 1, [B] = 2, [C] = 3 },
            [new ExportUser(A, " ALICE "), new ExportUser(B, "twin")],
            new Dictionary<Guid, string> { [newA] = "alice", [twin1] = "Twin", [twin2] = "twin" },
            NoneDeleted,
            MapUsersBy.Name);

        Assert.Equal([new ImportUserMapping(A, newA, "alice", 1)], map.Mapped);
        Assert.Equal([C, B], map.Unknown.Select(u => u.Id));
        Assert.Equal([ImportUnknownUser.NoKnownName, ImportUnknownUser.NoUserWithThisName], map.Unknown.Select(u => u.Reason));
        Assert.Equal(MapUsersBy.Name, map.By);
    }

    [Fact]
    public void UserMap_ByName_NeverGivesADeletedUsersRecordsToANewUserOfTheSameName()
    {
        // This server has a new alice, bob, carol and dave. The export's alice was deleted here (the kit's marks), its bob
        // on the exporting server (Users[].Deleted); two of its users are called carol; of its two daves, one was deleted.
        var server = new Dictionary<Guid, string> { [Guid.NewGuid()] = "alice", [Guid.NewGuid()] = "bob", [Guid.NewGuid()] = "carol" };
        var dave = Guid.NewGuid();
        server[dave] = "dave";
        var carol2 = Guid.NewGuid();
        var oldDave = Guid.NewGuid();

        var map = ImportUserMap.Build(
            new Dictionary<Guid, int> { [A] = 1, [B] = 2, [C] = 3, [carol2] = 1, [D] = 4, [oldDave] = 5 },
            [new ExportUser(A, "alice"), new ExportUser(B, "Bob", Deleted: true), new ExportUser(C, "carol"), new ExportUser(carol2, "Carol"), new ExportUser(D, "dave"), new ExportUser(oldDave, "dave", Deleted: true)],
            server,
            new HashSet<Guid> { A, E },
            MapUsersBy.Name);

        Assert.Equal([new ImportUserMapping(D, dave, "dave", 4)], map.Mapped);
        Assert.False(map.TryMap(A, out _));
        Assert.False(map.TryMap(B, out _));
        var reasons = map.Unknown.ToDictionary(u => u.Id, u => u.Reason);
        Assert.Equal(ImportUnknownUser.DeletedOnThisServer, reasons[A]);
        Assert.Equal(ImportUnknownUser.DeletedOnExportingServer, reasons[B]);
        Assert.Equal(ImportUnknownUser.SameNameTwice, reasons[C]);
        Assert.Equal(ImportUnknownUser.SameNameTwice, reasons[carol2]);
        Assert.Equal(ImportUnknownUser.DeletedOnExportingServer, reasons[oldDave]);
        Assert.Equal(5, map.Unknown.Count);
    }

    [Theory]
    [InlineData(null, null, null, "Merge", true, "Id")]
    [InlineData("replace", false, "NAME", "Replace", false, "Name")]
    [InlineData(" Merge ", true, " id ", "Merge", true, "Id")]
    public void ImportRequest_ReadsTheQuery(string? mode, bool? dryRun, string? by, string expectedMode, bool expectedDryRun, string expectedBy)
    {
        var request = ImportRequest.Parse(mode, dryRun, by);

        Assert.Equal(expectedMode, request.Mode.ToString());
        Assert.Equal(expectedDryRun, request.DryRun);
        Assert.Equal(expectedBy, request.MapUsersBy.ToString());
    }

    [Theory]
    [InlineData("Append", null)]
    [InlineData("1", null)]
    [InlineData(null, "Email")]
    [InlineData(null, "0")]
    public void ImportRequest_RefusesOtherValues(string? mode, string? by)
    {
        Assert.Equal(PluginErrorKind.Invalid, Assert.Throws<PluginException>(() => ImportRequest.Parse(mode, null, by)).Kind);
    }
}
