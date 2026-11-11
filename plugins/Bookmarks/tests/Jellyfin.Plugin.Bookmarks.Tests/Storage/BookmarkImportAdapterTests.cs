using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// The import check, through the kit's import (<see cref="JsonStoreImport"/>) into a real
/// bookmark store: clip paths and clip folders from an export are never trusted. A record keeps its clip file only when the
/// path passes <c>ClipFileGuard</c> against this server's clip folders; otherwise its file fields are dropped and the report
/// lists it; a tombstone without a file left is not imported; this server keeps its own <c>ClipRoots</c> and
/// <c>PendingFileDeletes</c>, also for an older export whose upgrade seeded folders from its clip files.
/// </summary>
public sealed class BookmarkImportAdapterTests : IDisposable
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid BobId = new("ff71b896-f205-45ff-936a-c2393f6091f6");

    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly BookmarkStore _store;
    private readonly FakeClipFileSystem _fileSystem = new();

    public BookmarkImportAdapterTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(_root);
        _store = ClipFileSupport.StoreWithClipRoots(StoreFile, _root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _directory.Combine("bookmarks.json");

    [Fact]
    public async Task AnExportsClipPaths_AreKeptOnlyInsideThisServersClipFolders()
    {
        var inside = Clip("inside.mp4");
        var insideTombstone = Clip("kept.mkv");
        var elsewhere = _directory.CreateFile(Path.Combine("elsewhere", "Heat (1995).mkv"));
        _directory.CreateFile(Path.Combine("clips", "linked", "via junction.mp4"));
        _fileSystem.ReparsePoints.Add(Path.Combine(_root, "linked"));
        var a = Record(AliceId, inside);
        var b = Record(AliceId, Path.Combine(_root, "alice", "gone.mp4"));
        var c = Record(AliceId, elsewhere);
        var d = Record(AliceId, elsewhere) with { DeletedUtc = DateTime.UtcNow };
        var e = Record(AliceId, insideTombstone) with { DeletedUtc = DateTime.UtcNow };
        var f = Record(AliceId, Path.Combine(_root, "linked", "via junction.mp4"));
        var g = Record(AliceId, filePath: null);
        var export = Document([a, b, c, d, e, f, g]) with
        {
            ClipRoots = [_directory.Combine("elsewhere"), _root],
            PendingFileDeletes = [new PendingFileDelete { Path = elsewhere, SinceUtc = DateTime.UtcNow, Attempts = 1 }]
        };

        var report = await ImportAsync(Envelope(export), dryRun: false);

        Assert.Equal(7, report.Records.Incoming);
        Assert.Equal(6, report.Records.Added);
        Assert.Equal(1, report.Records.Skipped);
        Assert.Equal(inside, _store.Get(a.Id)?.Clip?.FilePath);
        Assert.Equal(b.Clip!.FilePath, _store.Get(b.Id)?.Clip?.FilePath);
        Assert.Equal(insideTombstone, _store.Get(e.Id)?.Clip?.FilePath);
        Assert.Null(_store.Get(d.Id));
        foreach (var dropped in new[] { c, f })
        {
            var clip = _store.Get(dropped.Id)?.Clip;
            Assert.NotNull(clip);
            Assert.Null(clip.FilePath);
            Assert.Null(clip.FileSize);
            Assert.Null(clip.FileStartTicks);
            Assert.Null(clip.FileEndTicks);
            Assert.Null(clip.MissingSinceUtc);
            Assert.Equal(BookmarkClipStatus.Completed, clip.Status);
        }

        Assert.Null(_store.Get(g.Id)?.Clip);
        Assert.Equal([_root], _store.ClipRoots);
        Assert.Empty(_store.PendingFileDeletes);
        Assert.Contains($"2 record(s) kept without their clip file: the clip file path is outside this server's clip folders ({c.Id:D}, {f.Id:D}).", report.Notes);
        Assert.Contains($"1 kept clip(s) of deleted bookmarks not imported: the clip file path is outside this server's clip folders ({d.Id:D}).", report.Notes);
        Assert.Contains("The export's 1 other clip folder(s) were not imported; this server keeps its own.", report.Notes);
        Assert.Contains("The export's 1 clip file(s) waiting to be deleted were not imported: they are the exporting server's files.", report.Notes);
        Assert.All(new[] { inside, insideTombstone, elsewhere }, p => Assert.True(File.Exists(p)));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task AClipPathInOneOfThisServersClipFolders_IsKept_WhileThatFolderIsntThere()
    {
        // An unplugged drive never makes a clip forgotten: the path lies in this server's clip folder, and every
        // later use of the file goes through the guard again. (Such a path once read as a missing file, which is kept
        // too.)
        var usb = _directory.Combine("usb");
        await _store.AddClipRootAsync(usb, Ct);
        var record = Record(AliceId, Path.Combine(usb, "alice", "on the drive.mp4"));

        var report = await ImportAsync(Envelope(Document([record])), dryRun: false);

        Assert.Equal(1, report.Records.Added);
        Assert.Equal(record.Clip!.FilePath, _store.Get(record.Id)?.Clip?.FilePath);
        Assert.Equal(record.Clip.FileSize, _store.Get(record.Id)?.Clip?.FileSize);
        Assert.DoesNotContain(report.Notes, n => n.Contains("kept without their clip file", StringComparison.Ordinal));
        Assert.False(Directory.Exists(usb));
    }

    [Fact]
    public async Task ASchema1Export_WithClipsOutside_AddsNoFolder_AndKeepsNoFilePath()
    {
        // The kit runs the 1→2 upgrade on an older export's data, which seeds ClipRoots from its clip files'
        // folders (here the sandbox's); the adapter throws those away and checks the paths against this server's folders.
        var fixture = JsonNode.Parse(StoreFixtures.Read(StoreFixtures.Bookmarks100))!.AsObject();
        var envelope = Envelope(fixture, schemaVersion: 1);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        var dryRun = await ImportAsync(envelope, dryRun: true);
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Equal(1, dryRun.MigratedFrom);

        var report = await ImportAsync(envelope, dryRun: false);

        var records = _store.GetAll();
        Assert.Equal(report.Records.Added, records.Count);
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Null(r.Clip?.FilePath));
        Assert.Contains(records, r => r.Clip is not null);
        Assert.Equal([_root], _store.ClipRoots);
        Assert.Contains(report.Notes, n => n.Contains("kept without their clip file", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("other clip folder(s) were not imported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReplace_KeepsThisServersClipFoldersPendingDeletesAndJournalPosition()
    {
        var mine = Record(AliceId, Clip("mine.mp4"));
        await _store.AddAsync(mine, Ct);
        await _store.AddPendingFileDeleteAsync(Clip("waiting.mp4"), Ct);
        var journal = Guid.NewGuid();
        await _store.UpdateDocumentAsync(d => d with { MoveJournalId = journal, LastMoveSeq = 7 }, Ct);
        var theirs = Record(AliceId, Clip("theirs.mp4"));
        var export = Document([theirs]) with { ClipRoots = [@"D:\Clips"], MoveJournalId = Guid.NewGuid(), LastMoveSeq = 99 };

        var report = await ImportAsync(Envelope(export), dryRun: false, mode: "Replace");

        Assert.Equal(1, report.Records.Removed);
        Assert.Equal([theirs.Id], _store.GetAll().Select(b => b.Id));
        Assert.Equal([_root], _store.ClipRoots);
        Assert.Equal(Path.Combine(_root, "alice", "waiting.mp4"), Assert.Single(_store.PendingFileDeletes).Path);
        Assert.Equal(journal, _store.Documents.Current.MoveJournalId);
        Assert.Equal(7, _store.Documents.Current.LastMoveSeq);
        Assert.True(File.Exists(mine.Clip!.FilePath)); // an import never deletes a file
    }

    [Fact]
    public async Task UsersAreMapped_AndRecordsOfUnknownUsersSkipped_ByTheKit()
    {
        var otherAlice = Guid.NewGuid();
        var alices = Record(otherAlice, Clip("alice.mp4"));
        var unknown = Record(Guid.NewGuid(), Clip("unknown.mp4"));
        var envelope = Envelope(Document([alices, unknown])) with
        {
            Users = [new ExportUser(otherAlice, "alice"), new ExportUser(unknown.UserId, "carol")]
        };

        var report = await ImportAsync(envelope, dryRun: false, mapUsersBy: "Name");

        Assert.Equal(AliceId, _store.Get(alices.Id)?.UserId);
        Assert.Equal(alices.Clip!.FilePath, _store.Get(alices.Id)?.Clip?.FilePath);
        Assert.Null(_store.Get(unknown.Id));
        Assert.Equal(1, report.Records.SkippedUnknownUser);
        Assert.Equal("alice", _store.KnownUsers.NameOf(AliceId));
    }

    [Fact]
    public async Task TheExportsPreferencesAndLedger_AreNotImported_AndTheReportSaysSo()
    {
        var export = Document([]) with
        {
            UserPreferences = new Dictionary<Guid, BookmarkUserPreferences> { [AliceId] = new() { AskForName = true } },
            ImportLedger = new Dictionary<string, BookmarkImportLedgerEntry> { ["je:x:Bm_1"] = new() { BookmarkId = Guid.NewGuid(), Outcome = "Imported" } }
        };

        var report = await ImportAsync(Envelope(export), dryRun: false);

        Assert.Empty(_store.Documents.Current.UserPreferences);
        Assert.Empty(_store.ImportLedger);
        Assert.Contains("The export's naming preferences (1 user(s)) and Jellyfin Enhanced import ledger (1 entries) were not imported; this server keeps its own.", report.Notes);
    }

    [Fact]
    public void AnExportWithNothingOutside_HasNoNotes_AndChangesNothingTwice()
    {
        var adapter = new BookmarkImportAdapter(_fileSystem);
        var record = Record(AliceId, Clip("a.mp4"));
        var users = ImportUserMap.Build(new Dictionary<Guid, int> { [AliceId] = 1 }, [new ExportUser(AliceId, "alice")], new Dictionary<Guid, string> { [AliceId] = "alice" }, new HashSet<Guid>(), MapUsersBy.Id);

        var first = adapter.Apply(_store.Documents.Current, Document([record]) with { ClipRoots = [_root] }, ImportMode.Merge, users);
        var again = adapter.Apply(first.Document, Document([record]), ImportMode.Merge, users);

        Assert.Empty(first.Notes);
        Assert.Equal(1, first.Counts.Added);
        Assert.Same(first.Document, again.Document);
        Assert.Equal(1, again.Counts.Unchanged);
        Assert.Equal(new Dictionary<Guid, int> { [AliceId] = 1 }, adapter.CountRecordsByUser(first.Document));
    }

    [Fact]
    public void AnExportsClipJobs_AreEnded_AtTheExportsTime_KeepingTheEarlierFile_AndOnlyTheLiveOnesWrittenAreNoted()
    {
        // No clip worker runs a job that comes in with an import, so a Queued or Running clip is imported
        // Failed (a tombstone's as its file); at the export's time, so the same export imported again changes nothing.
        var exported = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var adapter = new BookmarkImportAdapter(_fileSystem, exportedUtc: exported);
        var previous = Clip("previous.mp4");
        var running = Record(AliceId, previous);
        running = running with { Clip = running.Clip! with { Status = BookmarkClipStatus.Running, CompletedAt = null, EstimatedBytes = 1000 } };
        var queued = Record(AliceId, filePath: null) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, Mode = BookmarkClipMode.Fast, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond, RequestedAt = exported.AddMinutes(-1) }
        };
        var tombstone = Record(AliceId, Clip("kept.mkv")) with { DeletedUtc = exported.AddDays(-1) };
        tombstone = tombstone with { Clip = tombstone.Clip! with { Status = BookmarkClipStatus.Running } };
        var users = ImportUserMap.Build(new Dictionary<Guid, int> { [AliceId] = 3 }, [new ExportUser(AliceId, "alice")], new Dictionary<Guid, string> { [AliceId] = "alice" }, new HashSet<Guid>(), MapUsersBy.Id);

        var first = adapter.Apply(_store.Documents.Current, Document([running, queued, tombstone]), ImportMode.Merge, users);

        var ended = first.Document.Bookmarks.Single(b => b.Id == running.Id).Clip!;
        Assert.Equal((BookmarkClipStatus.Failed, BookmarkImportAdapter.ImportedClipJobMessage, (DateTime?)exported), (ended.Status, ended.Error, ended.CompletedAt));
        Assert.Equal((previous, 4L, 1000L), (ended.FilePath, ended.FileSize, ended.EstimatedBytes));
        var endedQueued = first.Document.Bookmarks.Single(b => b.Id == queued.Id).Clip!;
        Assert.Equal((BookmarkClipStatus.Failed, (string?)null), (endedQueued.Status, endedQueued.FilePath));
        var kept = first.Document.Bookmarks.Single(b => b.Id == tombstone.Id).Clip!;
        Assert.Equal((BookmarkClipStatus.Completed, (string?)null, tombstone.Clip!.FilePath), (kept.Status, kept.Error, kept.FilePath));
        Assert.Equal(0, BookmarkImportAdapter.CountClipsBeingMade(first.Document.Bookmarks));
        var note = Assert.Single(first.Notes, n => n.StartsWith("2 clip(s) were being made when the export was made; an import doesn't make clips, so they are marked failed, and their owners can make them again (", StringComparison.Ordinal));
        Assert.Contains(running.Id.ToString("D"), note, StringComparison.Ordinal);
        Assert.Contains(queued.Id.ToString("D"), note, StringComparison.Ordinal);
        Assert.DoesNotContain(tombstone.Id.ToString("D"), note, StringComparison.Ordinal);

        // The same export again: the stored copies are the ended ones, unchanged, with no note.
        var again = new BookmarkImportAdapter(_fileSystem, exportedUtc: exported).Apply(first.Document, Document([running, queued, tombstone]), ImportMode.Merge, users);
        Assert.Same(first.Document, again.Document);
        Assert.Equal(3, again.Counts.Unchanged);
        Assert.DoesNotContain(again.Notes, n => n.Contains("being made", StringComparison.Ordinal));
    }

    [Fact]
    public void ATombstonesJob_IsNotAClipBeingMade_SinceTheWorkerNeverRunsOne()
    {
        // CountClipsBeingMade counts what the clip worker runs, so a tombstone left with a job state (another
        // version, a hand edit) never blocks an import or a restore.
        var tombstone = Record(AliceId, Clip("kept.mkv")) with { DeletedUtc = DateTime.UtcNow };
        tombstone = tombstone with { Clip = tombstone.Clip! with { Status = BookmarkClipStatus.Running } };
        var live = Record(AliceId, filePath: null) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, RequestedAt = DateTime.UtcNow } };

        Assert.Equal(0, BookmarkImportAdapter.CountClipsBeingMade([tombstone]));
        Assert.Equal(1, BookmarkImportAdapter.CountClipsBeingMade([tombstone, live]));
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    private static BookmarkStoreDocument Document(IEnumerable<Bookmark> records) => BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty(), records);

    private static Bookmark Record(Guid userId, string? filePath)
    {
        var bookmark = TestData.CreateBookmark(userId, 60, 90) with { Name = "Bank job" };
        return filePath is null ? bookmark : bookmark with
        {
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed,
                Mode = BookmarkClipMode.Accurate,
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                RequestedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                FilePath = filePath,
                FileSize = 4,
                FileStartTicks = 60 * TestData.OneSecond,
                FileEndTicks = 90 * TestData.OneSecond,
                MissingSinceUtc = DateTime.UtcNow
            }
        };
    }

    private static ExportEnvelope Envelope(BookmarkStoreDocument document)
        => Envelope(JsonSerializer.SerializeToNode(document, JsonStoreFormat.Options)!.AsObject(), BookmarkStoreDocument.CurrentVersion);

    private static ExportEnvelope Envelope(JsonObject data, int schemaVersion) => new()
    {
        Plugin = "Bookmarks",
        PluginId = BookmarksKit.Options.PluginId,
        PluginVersion = "1.1.0.0",
        SchemaVersion = schemaVersion,
        ExportedUtc = DateTime.UtcNow,
        ServerId = "another server",
        Users = [new ExportUser(AliceId, "alice"), new ExportUser(BobId, "bob")],
        Data = data
    };

    private string Clip(string name) => _directory.CreateFile(Path.Combine("clips", "alice", name), "clip");

    private Task<ImportReport> ImportAsync(ExportEnvelope envelope, bool dryRun, string mode = "Merge", string mapUsersBy = "Id")
        => JsonStoreImport.RunAsync(
            _store.Documents,
            envelope,
            ImportRequest.Parse(mode, dryRun, mapUsersBy),
            new BookmarkImportAdapter(_fileSystem),
            BookmarksKit.Options,
            new Dictionary<Guid, string> { [AliceId] = "alice", [BobId] = "bob" },
            new HashSet<Guid>(),
            _directory.Combine("snapshots"),
            NullLogger.Instance,
            Ct);
}
