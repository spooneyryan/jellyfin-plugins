using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.Users;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Storage;

/// <summary>
/// Export and import through <see cref="JsonStoreImport"/>: a dry run by default, Merge and Replace after a
/// pre-operation snapshot, users mapped by id or name with unknown ones skipped and reported, older data migrated, and
/// the refusals.
/// </summary>
public sealed class JsonStoreImportTests : IDisposable
{
    private static readonly Guid Alex = Guid.Parse("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid Viewer = Guid.Parse("0038ee0f-9abc-4d55-9ef5-abcf8520c9bf");
    private static readonly Guid NewAlex = Guid.Parse("7e57a000-0000-4000-8000-000000000001");
    private static readonly Guid Gone = Guid.Parse("bcd54fb1-0000-4000-8000-000000000009");
    private static readonly Guid NewViewer2 = Guid.Parse("7e57a000-0000-4000-8000-000000000002");

    private readonly TempDirectory _data = new();
    private readonly ListLogger _logger = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _stores = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Snapshots => _data.Combine("snapshots");

    public void Dispose()
    {
        _stores.ForEach(s => s.Dispose());
        _data.Dispose();
    }

    [Fact]
    public async Task ADryRun_IsTheDefault_AndWritesNothing()
    {
        var store = await StoreWithAsync("target.json", Note(1, Alex), Note(2, Alex));
        var before = await File.ReadAllBytesAsync(store.FilePath, Ct);
        var envelope = Envelope(Note(2, Alex, "edited", minutes: 5), Note(3, Alex));

        var report = await RunAsync(store, envelope, ImportRequest.Parse(null, null, null));

        Assert.True(report.DryRun);
        Assert.Equal(ImportMode.Merge, report.Mode);
        Assert.Equal(new ImportCounts { Incoming = 2, Added = 1, Updated = 1 }, report.Records);
        Assert.Null(report.SafetySnapshot);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, Ct));
        Assert.False(Directory.Exists(Snapshots));
    }

    [Fact]
    public async Task Merge_AddsAndUpdates_KeepsNewerLocalEdits_RemovesNothing_AfterASnapshot()
    {
        var store = await StoreWithAsync("target.json", Note(1, Alex), Note(2, Alex, "local edit", minutes: 30), Note(3, Alex), Note(9, Alex));
        var envelope = Envelope(Note(1, Alex), Note(2, Alex, "older edit", minutes: 10), Note(3, Alex, "newer", minutes: 60), Note(4, Alex));

        var report = await RunAsync(store, envelope, new ImportRequest(ImportMode.Merge, DryRun: false, MapUsersBy.Id));

        Assert.Equal(new ImportCounts { Incoming = 4, Added = 1, Updated = 1, Unchanged = 1, KeptNewer = 1 }, report.Records);
        Assert.Equal(["Note 1", "local edit", "newer", "Note 9", "Note 4"], store.Current.Notes.Select(n => n.Text));
        var safety = Path.Combine(Snapshots, SnapshotOperations.Folder, report.SafetySnapshot!);
        Assert.StartsWith("target-import-", report.SafetySnapshot, StringComparison.Ordinal);
        Assert.Equal(4, JsonNode.Parse(await File.ReadAllBytesAsync(safety, Ct))!["Notes"]!.AsArray().Count);
        Assert.Single(_logger.At(Microsoft.Extensions.Logging.LogLevel.Information), e => e.Message.StartsWith("Imported an export", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replace_MakesTheExportsRecordsTheStoredOnes()
    {
        var store = await StoreWithAsync("target.json", Note(1, Alex), Note(2, Alex, "local"), Note(9, Alex));
        var envelope = Envelope(Note(1, Alex), Note(2, Alex, "theirs", minutes: -5), Note(4, Alex));

        var report = await RunAsync(store, envelope, new ImportRequest(ImportMode.Replace, DryRun: false, MapUsersBy.Id));

        Assert.Equal(new ImportCounts { Incoming = 3, Added = 1, Updated = 1, Unchanged = 1, Removed = 1 }, report.Records);
        Assert.Equal(["Note 1", "theirs", "Note 4"], store.Current.Notes.Select(n => n.Text));
        Assert.NotNull(report.SafetySnapshot);
    }

    [Fact]
    public async Task ARealImportTheAdapterRefusesBeforeTheWrite_TakesNoSnapshot_AndWritesNothing()
    {
        var store = await StoreWithAsync("target.json", Note(1, Alex));
        var before = await File.ReadAllBytesAsync(store.FilePath, Ct);
        var adapter = new RefusingAdapter();

        await Assert.ThrowsAsync<PluginException>(() => RunAsync(store, Envelope(Note(2, Alex)), new ImportRequest(ImportMode.Replace, DryRun: false, MapUsersBy.Id), adapter: adapter));

        Assert.Equal(1, adapter.Checked);
        Assert.Equal(0, adapter.Applied);
        Assert.False(Directory.Exists(Snapshots));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, Ct));

        // A dry run doesn't ask it; a real run it lets through goes on to the snapshot and the write.
        adapter.Refuse = false;
        await RunAsync(store, Envelope(Note(2, Alex)), ImportRequest.Parse(null, null, null), adapter: adapter);
        Assert.Equal(1, adapter.Checked);
        var report = await RunAsync(store, Envelope(Note(2, Alex)), new ImportRequest(ImportMode.Merge, DryRun: false, MapUsersBy.Id), adapter: adapter);
        Assert.Equal(2, adapter.Checked);
        Assert.NotNull(report.SafetySnapshot);
        Assert.Equal(2, store.Current.Notes.Count);
    }

    [Fact]
    public async Task UsersById_UnknownUsersAreSkippedAndReported()
    {
        var store = await StoreWithAsync("target.json");
        var envelope = Envelope([new ExportUser(Alex, "alex"), new ExportUser(Viewer, "viewer")], Note(1, Alex), Note(2, Viewer), Note(3, Viewer));

        var report = await RunAsync(store, envelope, new ImportRequest(ImportMode.Merge, DryRun: false, MapUsersBy.Id), new Dictionary<Guid, string> { [Alex] = "alex" });

        Assert.Equal([new ImportUserMapping(Alex, Alex, "alex", 1)], report.MappedUsers);
        Assert.Equal([new ImportUnknownUser(Viewer, "viewer", 2, ImportUnknownUser.NotOnThisServer)], report.UnknownUsers);
        Assert.Equal(2, report.Records.SkippedUnknownUser);
        Assert.Equal(1, report.Records.Added);
        Assert.Equal([Alex], store.Current.Notes.Select(n => n.UserId));
    }

    [Fact]
    public async Task UsersByName_MapToThisServersIds_IgnoringCase()
    {
        var store = await StoreWithAsync("target.json");
        var envelope = Envelope([new ExportUser(Alex, "Alex"), new ExportUser(Viewer, null)], Note(1, Alex), Note(2, Viewer));

        var report = await RunAsync(store, envelope, new ImportRequest(ImportMode.Merge, DryRun: false, MapUsersBy.Name), new Dictionary<Guid, string> { [NewAlex] = "alex", [Viewer] = "viewer" });

        Assert.Equal([new ImportUserMapping(Alex, NewAlex, "alex", 1)], report.MappedUsers);
        Assert.Equal([new ImportUnknownUser(Viewer, null, 1, ImportUnknownUser.NoKnownName)], report.UnknownUsers);
        Assert.Equal([NewAlex], store.Current.Notes.Select(n => n.UserId));
    }

    [Fact]
    public async Task UsersByName_ADeletedUsersRecords_AreNeverGivenToANewUserWithTheName()
    {
        // Regression: Gone was deleted with KeepAsOrphan and holds a tombstone (a clip file); a new user took the
        // name viewer2, the name Gone had.
        var source = await StoreWithAsync("source.json", Note(1, Alex), Note(2, Gone, "the deleted user's clip"));
        var server = new Dictionary<Guid, string> { [Alex] = "alex", [NewViewer2] = "viewer2" };
        var deletedHere = new HashSet<Guid> { Gone };
        var export = ExportEnvelope.Parse(ExportEnvelope.Create(
            TestKit.Options,
            source.ExportData(),
            ExportUser.For(source.Current.Notes.Select(n => n.UserId), KnownUsers.Empty.With(Gone, "viewer2"), server, deletedHere),
            exportedUtc: _clock.GetUtcNow().UtcDateTime).ToUtf8Bytes());
        Assert.Contains(export.Users, u => u.Id == Gone && u.Deleted && u.Name == "viewer2");

        // Onto another server, where the new viewer2 exists: Replace, not a dry run.
        var target = Open("target.json");
        var report = await RunAsync(target, export, new ImportRequest(ImportMode.Replace, DryRun: false, MapUsersBy.Name), server);

        Assert.Equal([new ImportUserMapping(Alex, Alex, "alex", 1)], report.MappedUsers);
        Assert.Equal([new ImportUnknownUser(Gone, "viewer2", 1, ImportUnknownUser.DeletedOnExportingServer)], report.UnknownUsers);
        Assert.Equal(1, report.Records.SkippedUnknownUser);
        Assert.DoesNotContain(target.Current.Notes, n => n.UserId == NewViewer2);

        // On the same server, even an export without the flag (older, or edited): the kit's marks say Gone was deleted here.
        var unflagged = export with { Users = export.Users.Select(u => u with { Deleted = false }).ToList() };
        var again = await RunAsync(Open("same.json"), unflagged, new ImportRequest(ImportMode.Merge, DryRun: true, MapUsersBy.Name), server, deletedHere);
        Assert.Equal([new ImportUnknownUser(Gone, "viewer2", 1, ImportUnknownUser.DeletedOnThisServer)], again.UnknownUsers);
        var described = Assert.IsAssignableFrom<IDictionary<string, object?>>(again.ToDictionary()["Users"]);
        Assert.Equal("DeletedOnThisServer", Assert.Single(Assert.IsAssignableFrom<IEnumerable<IDictionary<string, object?>>>(described["Unknown"]))["Reason"]);

        // Without either, the name matches: exactly what the flag and the marks prevent.
        var unmarked = await RunAsync(Open("unmarked.json"), unflagged, new ImportRequest(ImportMode.Merge, DryRun: true, MapUsersBy.Name), server);
        Assert.Contains(unmarked.MappedUsers, m => m.Id == Gone && m.NewId == NewViewer2);
    }

    [Fact]
    public async Task AnExportOfAnOlderSchema_IsMigrated()
    {
        var store = await StoreWithAsync("target.json");
        var envelope = Envelope(Array.Empty<FxNote>()) with { SchemaVersion = 1, Data = Assert.IsType<JsonObject>(JsonNode.Parse(FxNotesStoreConformance.Version1Fixture)) };

        var report = await RunAsync(store, envelope, new ImportRequest(ImportMode.Replace, DryRun: false, MapUsersBy.Id));

        Assert.Equal(1, report.MigratedFrom);
        Assert.Equal(["first", "second"], store.Current.Notes.Select(n => n.Text));
    }

    [Fact]
    public async Task Refusals_ANewerSchema_AnotherPlugin_AReadOnlyStore()
    {
        var store = await StoreWithAsync("target.json", Note(1, Alex));
        var newer = Envelope(Note(2, Alex)) with { SchemaVersion = 4 };
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => RunAsync(store, newer, ImportRequest.Parse(null, false, null)))).Kind);

        var other = Envelope(Note(2, Alex)) with { PluginId = Guid.NewGuid() };
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => RunAsync(store, other, ImportRequest.Parse(null, false, null)))).Kind);

        var mismatch = Envelope(Note(2, Alex)) with { SchemaVersion = 2 };
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => RunAsync(store, mismatch, ImportRequest.Parse(null, false, null)))).Kind);

        await File.WriteAllTextAsync(_data.Combine("locked.json"), "{ \"Version\": 9, \"Notes\": [] }", Ct);
        var locked = Open("locked.json");
        var preview = await RunAsync(locked, Envelope(Note(2, Alex)), ImportRequest.Parse(null, null, null));
        Assert.Equal(1, preview.Records.Added);
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => RunAsync(locked, Envelope(Note(2, Alex)), ImportRequest.Parse(null, false, null)))).Kind);
        Assert.Single(store.Current.Notes);
    }

    [Fact]
    public async Task ExportThenImportIntoAnEmptyStore_RoundTrips()
    {
        var source = await StoreWithAsync("source.json", Note(1, Alex), Note(2, Viewer, "Amélie, café ✂ 日本語"), Note(3, Alex) with { Extra = new() { ["Colour"] = JsonSerializer.SerializeToElement("red") } });
        var envelope = ExportEnvelope.Create(
            TestKit.Options,
            source.ExportData(),
            ExportUser.For(source.Current.Notes.Select(n => n.UserId), KnownUsers.Empty, new Dictionary<Guid, string> { [Alex] = "alex", [Viewer] = "viewer" }, new HashSet<Guid>()),
            archive: [new JsonObject { ["Id"] = "archived" }],
            exportedUtc: _clock.GetUtcNow().UtcDateTime);
        var file = ExportEnvelope.Parse(envelope.ToUtf8Bytes());
        var target = Open("copy.json");

        var report = await RunAsync(target, file, new ImportRequest(ImportMode.Replace, DryRun: false, MapUsersBy.Name), new Dictionary<Guid, string> { [Alex] = "alex", [Viewer] = "viewer" });

        Assert.Equal(3, report.Records.Added);
        Assert.Equal(source.ExportData().Data.ToJsonString(), target.ExportData().Data.ToJsonString());
        Assert.Equal(new ArchiveImportResult(1, 0, report.Archive.Note), report.Archive);
        Assert.NotNull(report.Archive.Note);
        Assert.False(report.SettingsIncluded);
        Assert.Equal("Not in the export.", report.ToDictionary()["Settings"]);
    }

    [Fact]
    public async Task TheArchivesUsers_AreMatchedToo_AlsoAUserWithOnlyArchivedRecords_WhileOnlyLiveRecordsCountAsSkipped()
    {
        // Since Bookmarks imports its archive, a user whose records are all archived must be found like the others,
        // by id and by name; a user not found skips only their live records in the counts.
        var store = Open("target.json");
        var envelope = Envelope([new ExportUser(Alex, "alex"), new ExportUser(Viewer, "viewer"), new ExportUser(Gone, "gone")], Note(1, Alex), Note(2, Gone)) with
        {
            Archive = [Archived(Viewer), Archived(Viewer), Archived(Gone)]
        };
        var adapter = new ArchivingAdapter();

        var byId = await RunAsync(store, envelope, new ImportRequest(ImportMode.Merge, DryRun: true, MapUsersBy.Id), adapter: adapter);
        Assert.Equal(new[] { Alex, Viewer }.Order(), byId.MappedUsers.Select(u => u.Id).Order());
        Assert.Equal(2, byId.MappedUsers.Single(u => u.Id == Viewer).Records);
        var gone = Assert.Single(byId.UnknownUsers);
        Assert.Equal(Gone, gone.Id);
        Assert.Equal(2, gone.Records);
        Assert.Equal(1, byId.Records.SkippedUnknownUser);
        Assert.Equal(2, byId.Archive.Imported);

        var byName = await RunAsync(store, envelope, new ImportRequest(ImportMode.Merge, DryRun: true, MapUsersBy.Name), new Dictionary<Guid, string> { [NewAlex] = "Alex", [NewViewer2] = "viewer" }, adapter: adapter);
        Assert.Equal(NewViewer2, byName.MappedUsers.Single(u => u.Id == Viewer).NewId);
        Assert.Equal(2, byName.Archive.Imported);

        // An adapter without an archive counts none: the user with only archived records isn't looked for.
        var plain = await RunAsync(store, envelope, new ImportRequest(ImportMode.Merge, DryRun: true, MapUsersBy.Id));
        Assert.DoesNotContain(plain.MappedUsers, u => u.Id == Viewer);
        Assert.Equal(0, plain.Archive.Imported);
    }

    private static JsonObject Archived(Guid user) => new() { ["Id"] = Guid.NewGuid().ToString("D"), ["UserId"] = user.ToString("D") };

    private async Task<ImportReport> RunAsync(JsonDocumentStore<FxNotesDocument> store, ExportEnvelope envelope, ImportRequest request, IReadOnlyDictionary<Guid, string>? server = null, IReadOnlySet<Guid>? deleted = null, JsonImportAdapter<FxNotesDocument>? adapter = null)
        => await JsonStoreImport.RunAsync(
            store,
            envelope,
            request,
            adapter ?? new FxNotesImportAdapter(),
            TestKit.Options,
            server ?? new Dictionary<Guid, string> { [Alex] = "alex", [Viewer] = "viewer" },
            deleted ?? new HashSet<Guid>(),
            Snapshots,
            _logger,
            Ct);

    private static FxNote Note(int number, Guid user, string? text = null, int minutes = 0)
    {
        var note = FxNote.Create(number) with { UserId = user, Text = text ?? ("Note " + number) };
        return note with { UpdatedAt = note.UpdatedAt.AddMinutes(minutes) };
    }

    private static ExportEnvelope Envelope(params FxNote[] notes) => Envelope([new ExportUser(Alex, "alex"), new ExportUser(Viewer, "viewer")], notes);

    private static ExportEnvelope Envelope(IReadOnlyList<ExportUser> users, params FxNote[] notes) => new()
    {
        Plugin = "Kit Test",
        PluginId = TestKit.PluginId,
        PluginVersion = "1.0.0.0",
        SchemaVersion = FxNotesDocument.CurrentVersion,
        ExportedUtc = new DateTime(2026, 10, 1, 17, 0, 0, DateTimeKind.Utc),
        Users = users,
        Data = (JsonObject)JsonSerializer.SerializeToNode(new FxNotesDocument { Notes = notes }, JsonStoreFormat.Options)!
    };

    private JsonDocumentStore<FxNotesDocument> Open(string name)
    {
        var store = new JsonDocumentStore<FxNotesDocument>(_data.Combine(name), FxNotesDocument.CurrentVersion, FxNotesDocument.CreateEmpty, FxNotesDocument.Migrations, _logger, timeProvider: _clock);
        _stores.Add(store);
        return store;
    }

    private async Task<JsonDocumentStore<FxNotesDocument>> StoreWithAsync(string name, params FxNote[] notes)
    {
        var store = Open(name);
        await store.UpdateAsync(d => d with { Notes = notes }, Ct);
        return store;
    }

    // FxNotes with an archive: archived records count for their users, and those of mapped users are imported.
    private sealed class ArchivingAdapter : JsonImportAdapter<FxNotesDocument>
    {
        private readonly FxNotesImportAdapter _notes = new();

        public override IReadOnlyDictionary<Guid, int> CountRecordsByUser(FxNotesDocument document) => _notes.CountRecordsByUser(document);

        public override IReadOnlyDictionary<Guid, int> CountArchivedRecordsByUser(IReadOnlyList<JsonObject> records, int schemaVersion)
            => records.Select(r => Guid.Parse(r["UserId"]!.GetValue<string>())).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

        public override ImportApplied<FxNotesDocument> Apply(FxNotesDocument current, FxNotesDocument incoming, ImportMode mode, ImportUserMap users)
            => _notes.Apply(current, incoming, mode, users);

        public override Task<ArchiveImportResult> ImportArchiveAsync(IReadOnlyList<JsonObject> records, int schemaVersion, ImportUserMap users, ImportMode mode, bool dryRun, CancellationToken cancellationToken)
            => Task.FromResult(new ArchiveImportResult(records.Count, records.Count(r => users.TryMap(Guid.Parse(r["UserId"]!.GetValue<string>()), out _)), null));
    }

    // FxNotes whose check before the write refuses while Refuse is set; it counts the checks and the applies.
    private sealed class RefusingAdapter : JsonImportAdapter<FxNotesDocument>
    {
        private readonly FxNotesImportAdapter _notes = new();

        public bool Refuse { get; set; } = true;

        public int Checked { get; private set; }

        public int Applied { get; private set; }

        public override IReadOnlyDictionary<Guid, int> CountRecordsByUser(FxNotesDocument document) => _notes.CountRecordsByUser(document);

        public override void CheckBeforeWrite(FxNotesDocument current, FxNotesDocument incoming, ImportMode mode, ImportUserMap users)
        {
            Checked++;
            if (Refuse)
            {
                throw PluginException.Conflict("Refused before the write.");
            }
        }

        public override ImportApplied<FxNotesDocument> Apply(FxNotesDocument current, FxNotesDocument incoming, ImportMode mode, ImportUserMap users)
        {
            Applied++;
            return _notes.Apply(current, incoming, mode, users);
        }
    }

    private sealed class FxNotesImportAdapter : JsonImportAdapter<FxNotesDocument>
    {
        private static readonly IEqualityComparer<FxNote> SameContent = ImportMerge.JsonContent<FxNote>();

        public override IReadOnlyDictionary<Guid, int> CountRecordsByUser(FxNotesDocument document)
            => document.Notes.GroupBy(n => n.UserId).ToDictionary(g => g.Key, g => g.Count());

        public override ImportApplied<FxNotesDocument> Apply(FxNotesDocument current, FxNotesDocument incoming, ImportMode mode, ImportUserMap users)
        {
            var mapped = incoming.Notes
                .Select(n => users.TryMap(n.UserId, out var id) ? n with { UserId = id } : null)
                .OfType<FxNote>()
                .ToList();
            var merged = mode == ImportMode.Replace
                ? ImportMerge.Replace(current.Notes, mapped, n => n.Id, SameContent)
                : ImportMerge.Merge(current.Notes, mapped, n => n.Id, n => n.UpdatedAt, SameContent);
            var changed = merged.Counts.Added + merged.Counts.Updated + merged.Counts.Removed > 0;
            return new ImportApplied<FxNotesDocument>(changed ? current with { Notes = merged.Records } : current, merged.Counts, []);
        }
    }
}
