using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Xunit;
using static Jellyfin.Plugin.Kit.Tests.Library.MoveJournalTests;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="DetachedRecords"/>: live detached records re-resolved with
/// the owner's policy and view; archive after retention (never a hard delete on a timer); reattach from the archive, at most
/// 500 per run, least recently checked first; the reconcile; <i>Purge</i> as the only hard delete, after a snapshot; the
/// archive in exports and imports; Diagnostics' <c>Detached</c>.
/// </summary>
public sealed class DetachedRecordsTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeLibrary _library = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday));
    private readonly TempDirectory _directory = new();
    private readonly ListLogger _logger = new();
    private readonly FakeRecordOwner _owner = new() { Name = "bookmarks", RelinkPolicy = RelinkPolicy.RuntimeWithin() };
    private readonly Dictionary<Guid, User> _users = [];
    private readonly DetachedRecords _records;

    public DetachedRecordsTests()
    {
        _records = RecordsOf(_owner);
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task ResolveDetached_RelinksARecordWhoseItemCameBack_AndLeavesTheOthers()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var back = _owner.Add(ItemIdentity.Capture(movie, _library.Object), detachedUtc: Monday.AddDays(-1));
        var gone = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Gone" }, detachedUtc: Monday.AddDays(-1));

        var result = await _records.ResolveDetachedAsync(_owner, TestContext.Current.CancellationToken);

        Assert.Null(back.DetachedUtc);
        Assert.Equal(MatchStrength.Exact, back.RelinkStrength);
        Assert.NotNull(gone.DetachedUtc);
        Assert.Equal((2, 1, 0), (result.Checked, result.Relinked, result.Ambiguous));
    }

    [Fact]
    public async Task ResolveDetached_NeverRelinksToACacheGhost_OfADeletedArtistFolder_ForAUsersRecordOrAServerWideOne()
    {
        // The sweep used to detach the album's records (its parent walk), and the daily re-resolution took them back to
        // the same id, which 10.11.5's item cache still has, so they flip-flopped every day.
        var root = _library.Add(new MediaBrowser.Controller.Entities.Folder { Name = "Music", Path = @"Q:\Music" });
        var artist = _library.Add(new MediaBrowser.Controller.Entities.Audio.MusicArtist { Name = "Artist A", Path = @"Q:\Music\Artist A" }, root);
        var album = _library.Add(new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Name = "Album A", Path = @"Q:\Music\Artist A\Album A" }, artist);
        var user = NewUser("owner");
        var owned = _owner.Add(ItemIdentity.Capture(album, _library.Object), detachedUtc: Monday.AddDays(-1), userId: user.Id);
        var serverWide = _owner.Add(ItemIdentity.Capture(album, _library.Object), detachedUtc: Monday.AddDays(-1));
        _library.Remove(artist, raiseEvent: false, cacheKeepsDescendants: true);

        var result = await _records.ResolveDetachedAsync(_owner, TestContext.Current.CancellationToken);

        Assert.Equal((2, 0), (result.Checked, result.Relinked));
        Assert.NotNull(owned.DetachedUtc);
        Assert.NotNull(serverWide.DetachedUtc);
        Assert.Equal(ResolveOutcome.Unmatched, owned.LastOutcome);
        Assert.Equal(ResolveOutcome.Unmatched, serverWide.LastOutcome);
    }

    [Fact]
    public async Task ResolveDetached_TheOwnersPolicyDecides_AndAnAmbiguousRecordIsCountedInDiagnostics()
    {
        // The edition pair: the 60 s copy is gone, the 90 s copy shares its TMDb id but fails Bookmarks' runtime check.
        var sixty = _library.AddMovie("Edition", 2001, TimeSpan.FromSeconds(60), null, ("Tmdb", "900301"));
        _library.AddMovie("Edition Extended", 2001, TimeSpan.FromSeconds(90), null, ("Tmdb", "900301"));
        var record = _owner.Add(ItemIdentity.Capture(sixty, _library.Object), detachedUtc: Monday.AddDays(-1));
        _library.Remove(sixty, raiseEvent: false);

        var result = await _records.ResolveDetachedAsync(_owner, TestContext.Current.CancellationToken);

        Assert.NotNull(record.DetachedUtc);
        Assert.Equal(ResolveOutcome.Ambiguous, record.LastOutcome);
        Assert.Equal(1, result.Ambiguous);
        Assert.Equal(1, Owner(_records.Describe())["Ambiguous"]);
    }

    [Fact]
    public async Task EachRecord_IsResolvedWithTheOwnersPolicyForIt_LiveAndArchived_AndTheDefaultIsTheOwnersPolicy()
    {
        // Bookmarks' own records never take a Strong candidate without a stored runtime (a 1.0 bookmark whose
        // video was gone before 1.1 looked it up), while a JE import that never attached does.
        var owner = new PerRecordPolicyOwner { Name = "bookmarks", RelinkPolicy = RelinkPolicy.RuntimeWithin(requireStoredRuntime: true) };
        var records = RecordsOf(owner);
        var candidate = _library.AddMovie("Lone Movie", 2011, TimeSpan.FromMinutes(95), null, ("Tmdb", "900003"));
        var own = owner.Add(WithoutRuntime(), detachedUtc: Monday.AddDays(-1));
        var imported = owner.Add(WithoutRuntime(), detachedUtc: Monday.AddDays(-1));
        owner.LenientIds.Add(imported.Id);

        var result = await records.ResolveDetachedAsync(owner, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1, 1), (result.Checked, result.Relinked, result.Ambiguous));
        Assert.NotNull(own.DetachedUtc);
        Assert.Equal(ResolveOutcome.Ambiguous, own.LastOutcome);
        Assert.Null(imported.DetachedUtc);
        Assert.Equal((candidate.Id, MatchStrength.Strong), (imported.Identity.ItemId, imported.RelinkStrength!.Value));
        Assert.Equal(new[] { own.Id, imported.Id }, owner.PolicyRequests.Select(r => r.Id));

        // Archived, the record is asked about again (as an ArchivedRecord) at every reattach.
        owner.DetachedRetentionDays = 1;
        _clock.Set(new DateTimeOffset(Monday.AddDays(2)));
        Assert.Equal(1, (await records.ArchiveDueAsync(owner, TestContext.Current.CancellationToken)).Archived);
        var kept = await records.ReattachFromArchiveAsync(owner, TestContext.Current.CancellationToken);
        Assert.Equal((1, 0, 1), (kept.Checked, kept.Relinked, kept.Ambiguous));
        Assert.IsType<ArchivedRecord>(owner.PolicyRequests[^1]);
        owner.LenientIds.Add(own.Id);
        var back = await records.ReattachFromArchiveAsync(owner, TestContext.Current.CancellationToken);
        Assert.Equal(1, back.Relinked);
        Assert.Equal(candidate.Id, owner.Find(own.Id)!.Identity.ItemId);

        // An owner that doesn't choose per record: its RelinkPolicy for every record (the interface's default).
        IDetachedRecordOwner plain = _owner;
        Assert.Same(_owner.RelinkPolicy, plain.GetRelinkPolicy(owner.PolicyRequests[0]));
    }

    [Fact]
    public async Task ResolveDetached_OfSomeRecords_ResolvesOnlyThose_AndAnEmptyListNothing()
    {
        // A batch's owner picks out the records its items could match; the others aren't looked up.
        var heat = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var ronin = _library.AddMovie("Ronin", 1998, TimeSpan.FromMinutes(122), null, ("Tmdb", "8195"));
        var picked = _owner.Add(ItemIdentity.Capture(heat, _library.Object), detachedUtc: Monday.AddDays(-1));
        var left = _owner.Add(ItemIdentity.Capture(ronin, _library.Object), detachedUtc: Monday.AddDays(-1));
        var record = Assert.Single(_owner.GetDetached(), r => r.Id == picked.Id);

        var none = await _records.ResolveDetachedAsync(_owner, [], TestContext.Current.CancellationToken);
        _library.IdLookups.Clear();
        var result = await _records.ResolveDetachedAsync(_owner, [record], TestContext.Current.CancellationToken);

        Assert.Equal((0, 0), (none.Checked, none.Relinked));
        Assert.Null(picked.DetachedUtc);
        Assert.NotNull(left.DetachedUtc);
        Assert.Equal((1, 1), (result.Checked, result.Relinked));
        Assert.DoesNotContain(ronin.Id, _library.IdLookups);
    }

    [Fact]
    public async Task ResolveDetached_AlsoResolvesTheRecordsTheOwnerHolds_WhileDiagnosticsCountOnlyTheDetached()
    {
        // User Ratings' mass-detach guard keeps ratings attached to items that are gone; a full re-resolution (daily,
        // at startup) takes them too, as detached since they were held. Diagnostics and the purge don't.
        var owner = new HeldRecordsOwner { Name = "ratings", RelinkPolicy = RelinkPolicy.Default, HeldSinceUtc = Monday.AddHours(-3) };
        var records = RecordsOf(owner);
        var heat = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var detached = owner.Add(ItemIdentity.Capture(heat, _library.Object), detachedUtc: Monday.AddDays(-1));
        var ronin = _library.AddMovie("Ronin", 1998, TimeSpan.FromMinutes(122), null, ("Tmdb", "8195"));
        var held = owner.Add(ItemIdentity.Capture(ronin, _library.Object));
        var other = owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Attached" });
        owner.HeldIds.Add(held.Id);
        _library.IdLookups.Clear();

        var result = await records.ResolveDetachedAsync(owner, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1), (result.Checked, result.Relinked));
        Assert.Null(detached.DetachedUtc);
        Assert.Contains(ronin.Id, _library.IdLookups);
        Assert.DoesNotContain(other.Identity.ItemId, _library.IdLookups);
        Assert.Equal(0, Owner(records.Describe())["Count"]);
    }

    [Fact]
    public async Task ResolveDetached_SeesOnlyWhatTheRecordsUserCanSee_AndLeavesAnUnknownUsersRecordAlone()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var viewer = NewUser("viewer");
        var kid = NewUser("kid");
        _library.Hide(kid, movie);
        var viewers = _owner.Add(ItemIdentity.Capture(movie, _library.Object), viewer.Id, Monday.AddDays(-1));
        var kids = _owner.Add(ItemIdentity.Capture(movie, _library.Object), kid.Id, Monday.AddDays(-1));
        var orphans = _owner.Add(ItemIdentity.Capture(movie, _library.Object), Guid.NewGuid(), Monday.AddDays(-1));

        var result = await _records.ResolveDetachedAsync(_owner, TestContext.Current.CancellationToken);

        Assert.Null(viewers.DetachedUtc);
        Assert.NotNull(kids.DetachedUtc);
        Assert.NotNull(orphans.DetachedUtc);
        Assert.Equal((1, 1), (result.Relinked, result.OwnerUnknown));
    }

    [Fact]
    public async Task EveryStep_LeavesAReadOnlyOwnerAlone()
    {
        _owner.IsReadOnly = true;
        _owner.DetachedRetentionDays = 1;
        _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-10));

        Assert.Equal("ReadOnly", (await _records.ResolveDetachedAsync(_owner, TestContext.Current.CancellationToken)).Skipped);
        Assert.Equal("ReadOnly", (await _records.ArchiveDueAsync(_owner, TestContext.Current.CancellationToken)).Skipped);
        Assert.Equal("ReadOnly", (await _records.ReattachFromArchiveAsync(_owner, TestContext.Current.CancellationToken)).Skipped);
        var purge = await Assert.ThrowsAsync<PluginException>(() => _records.PurgeAsync(_owner, null, true, TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Conflict, purge.Kind);
        Assert.Single(_owner.Records);
        Assert.False(File.Exists(_records.ArchiveOf(_owner).FilePath));
    }

    [Fact]
    public async Task Retention0_KeepsDetachedRecordsInTheLiveStore()
    {
        _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddYears(-3));

        var result = await _records.ArchiveDueAsync(_owner, TestContext.Current.CancellationToken);

        Assert.Equal("RetentionOff", result.Skipped);
        Assert.Single(_owner.Records);
    }

    [Fact]
    public async Task AfterRetention_ADetachedRecordMovesToTheArchive_IsExported_AndReattachesWhenItsItemReturns()
    {
        // The retention check: DetachedRetentionDays = 1, the clock moved on, maintenance run.
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var snapshot = ItemIdentity.Capture(movie, _library.Object);
        _library.Remove(movie, raiseEvent: false);
        var record = _owner.Add(snapshot, detachedUtc: Monday);
        record.Note = "the heist";
        var fresh = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(2));
        _owner.DetachedRetentionDays = 1;
        _clock.Set(new DateTimeOffset(Monday.AddDays(2)));

        var archived = await _records.ArchiveDueAsync(_owner, TestContext.Current.CancellationToken);

        Assert.Equal(1, archived.Archived);
        Assert.Null(_owner.Find(record.Id));
        Assert.NotNull(_owner.Find(fresh.Id));
        var export = Assert.Single(_records.ExportArchive(_owner));
        Assert.Equal("the heist", export["Note"]!.GetValue<string>());
        Assert.Equal(1, Owner(_records.Describe())["Archived"]);

        // A record whose purge the owner has pending is in no export and no count.
        _owner.PurgePending.Add(record.Id);
        Assert.Empty(_records.ExportArchive(_owner));
        Assert.Equal(0, Owner(_records.Describe())["Archived"]);
        _owner.PurgePending.Clear();

        // Not back yet: checked, still archived.
        var early = await _records.ReattachFromArchiveAsync(_owner, TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (early.Checked, early.Relinked));

        // The movie comes back with a new id (renamed folder): one Strong candidate by TMDb id, the runtime within 2 %.
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"C:\media\Movies\Heat (1995) renamed\Heat.mkv", ("Tmdb", "949"));
        var reattached = await _records.ReattachFromArchiveAsync(_owner, TestContext.Current.CancellationToken);

        Assert.Equal(1, reattached.Relinked);
        var back = _owner.Find(record.Id)!;
        Assert.Null(back.DetachedUtc);
        Assert.Equal("the heist", back.Note);
        Assert.Equal(MatchStrength.Strong, back.RelinkStrength);
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
        Assert.Empty(_records.ExportArchive(_owner));
    }

    [Fact]
    public async Task AFailedStoreWriteAfterTheArchive_MarksTheArchivedCopiesSuperseded()
    {
        var record = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-5));
        _owner.DetachedRetentionDays = 1;
        _owner.FailStoreWrite = true;

        await Assert.ThrowsAsync<IOException>(() => _records.ArchiveDueAsync(_owner, TestContext.Current.CancellationToken));

        Assert.NotNull(_owner.Find(record.Id));
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);

        // So a record its user deletes afterwards never comes back from the archive.
        _owner.Records.Clear();
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
        Assert.Contains("Superseded", await File.ReadAllTextAsync(_records.ArchiveOf(_owner).FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOwnerThatThrowsAfterItsStoreWrite_KeepsTheArchivedCopiesOfTheRecordsThatLeft()
    {
        // Every archived copy used to be superseded on any exception, so records that had left the store were
        // gone from exports, Diagnostics and the reattach too.
        var gone = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-5));
        gone.Note = "kept in the archive";
        _owner.DetachedRetentionDays = 1;
        _owner.FailAfterStoreWrite = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _records.ArchiveDueAsync(_owner, TestContext.Current.CancellationToken));

        Assert.Equal("Something after the store's write failed.", error.Message);
        Assert.Null(_owner.Find(gone.Id));
        Assert.Equal(new[] { gone.Id }, _records.ArchiveOf(_owner).Read().Records.Select(r => r.Id));
        Assert.Equal("kept in the archive", Assert.Single(_records.ExportArchive(_owner))["Note"]!.GetValue<string>());
        Assert.Equal(1, Owner(_records.Describe())["Archived"]);
        Assert.DoesNotContain("Superseded", await File.ReadAllTextAsync(_records.ArchiveOf(_owner).FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Single(_logger.At(Microsoft.Extensions.Logging.LogLevel.Warning), e => e.Message.Contains("left its store", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASupersedeThatFails_NeverHidesTheStoresError_AndTheReconcileCatchesUp()
    {
        var record = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-5));
        _owner.DetachedRetentionDays = 1;
        _owner.FailStoreWrite = true;
        FileStream? held = null;
        _owner.AfterAppend = () => held = new FileStream(_records.ArchiveOf(_owner).FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => _records.ArchiveDueAsync(_owner, TestContext.Current.CancellationToken));
            Assert.Equal("The store couldn't be written.", error.Message);
            Assert.Single(_logger.At(Microsoft.Extensions.Logging.LogLevel.Error), e => e.Message.Contains("couldn't be marked superseded", StringComparison.Ordinal));
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }

        // The record stayed live and its archived copy wasn't marked: the live copy counts, and the next start's reconcile
        // marks the other one.
        Assert.NotNull(_owner.Find(record.Id));
        Assert.Equal(0, Owner(_records.Describe())["Archived"]);
        await _records.ReconcileAsync(TestContext.Current.CancellationToken);
        _owner.Records.Clear();
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
    }

    [Fact]
    public async Task AnOwnersOwnReason_MovesItsRecordsIntoTheArchive_ExportedAndPurgedLikeTheOthers_ButNeverReattached()
    {
        // Bookmarks moves a deleted bookmark whose clip file has been missing longer than its retention into
        // the archive with the reason ClipMissing. Its item is still in the library, so a reattach would find it at once (an
        // Exact match): it must not, since no item is waited for.
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var record = _owner.Add(ItemIdentity.Capture(movie, _library.Object));
        record.Note = "the kept clip's note";
        var other = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var ct = TestContext.Current.CancellationToken;

        var result = await _records.ArchiveForReasonAsync(
            _owner,
            "ClipMissing",
            (append, _) =>
            {
                var written = append([Archived(record) with { DetachedUtc = Monday.AddDays(-31) }]);
                _owner.Records.Remove(record);
                return Task.FromResult(written);
            },
            ct);

        Assert.Equal(("ArchiveClipMissing", 1), (result.Step, result.Archived));
        Assert.Null(_owner.Find(record.Id));
        Assert.NotNull(_owner.Find(other.Id));
        var archived = Assert.Single(_records.ArchiveOf(_owner).Read().Records);
        Assert.Equal(("ClipMissing", Monday.AddDays(-31)), (archived.Reason, archived.DetachedUtc));
        Assert.Equal("the kept clip's note", Assert.Single(_records.ExportArchive(_owner))["Note"]!.GetValue<string>());
        var described = Owner(_records.Describe());
        Assert.Equal(1, described["Archived"]);
        Assert.Equal(new Dictionary<string, int>(StringComparer.Ordinal) { ["ClipMissing"] = 1 }, Assert.IsType<Dictionary<string, int>>(described["ArchivedFor"]));
        Assert.Contains((IEnumerable<IDictionary<string, object?>>)described["LastRuns"]!, r => Equals(r["Step"], "ArchiveClipMissing"));

        var reattach = await _records.ReattachFromArchiveAsync(_owner, ct);
        Assert.Equal((0, 0), (reattach.Checked, reattach.Relinked));
        Assert.Null(_owner.Find(record.Id));
        Assert.Single(_records.ArchiveOf(_owner).Read().Records);

        // The admin's Purge takes it like any archived record (the age is its reason's: here the clip missing since).
        Assert.Equal(0, (await _records.PurgeAsync(_owner, 40, includeArchive: true, ct)).Purged);
        Assert.Equal(1, (await _records.PurgeAsync(_owner, 30, includeArchive: true, ct)).Purged);
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
    }

    [Fact]
    public async Task AnOwnersOwnReason_ThatFailsAfterTheArchiveWasWritten_SupersedesOnlyTheCopiesOfRecordsTheStoreKept()
    {
        // The retention step's safety for the owner's own archiving: a store write that failed keeps the records live, and their
        // archived copies don't count; one that went through before the failure keeps its archived copy.
        var kept = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var gone = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var ct = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<IOException>(() => _records.ArchiveForReasonAsync(
            _owner,
            "ClipMissing",
            (append, _) =>
            {
                append([Archived(kept), Archived(gone)]);
                _owner.Records.Remove(gone);
                throw new IOException("The store couldn't be written.");
            },
            ct));

        Assert.Equal("The store couldn't be written.", error.Message);
        Assert.Equal(new[] { gone.Id }, _records.ArchiveOf(_owner).Read().Records.Select(r => r.Id));
        Assert.Contains("Superseded", await File.ReadAllTextAsync(_records.ArchiveOf(_owner).FilePath, ct), StringComparison.Ordinal);
        _owner.Records.Clear();
        Assert.Equal(new[] { gone.Id }, _records.ArchiveOf(_owner).Read().Records.Select(r => r.Id));
    }

    [Fact]
    public async Task AnOwnersOwnReason_TravelsWithTheExport_AndAnImportKeepsItAndItsTime_ForAnyOwner()
    {
        // The export used to carry only the record, so its reason came back only where an owner could tell it
        // from the record (Bookmarks' deleted bookmarks with a missing clip). The kit carries it now, with the time it began
        // (the archive line's detach time, which the record needn't hold), and an import into another server keeps both.
        var ct = TestContext.Current.CancellationToken;
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var reasoned = _owner.Add(ItemIdentity.Capture(movie, _library.Object));
        reasoned.Note = "the reasoned one";
        var detached = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-100));
        _owner.DetachedRetentionDays = 1;
        Assert.Equal(1, (await _records.ArchiveDueAsync(_owner, ct)).Archived);
        await _records.ArchiveForReasonAsync(
            _owner,
            "Gone",
            (append, _) =>
            {
                var written = append([Archived(reasoned) with { DetachedUtc = Monday.AddDays(-31) }]);
                _owner.Records.Remove(reasoned);
                return Task.FromResult(written);
            },
            ct);

        var export = _records.ExportArchive(_owner);

        var withReason = Assert.Single(export, e => e["Id"]!.GetValue<string>() == reasoned.Id);
        Assert.Equal("Gone", withReason[DetachedRecords.ExportReasonProperty]!.GetValue<string>());
        Assert.Equal(Monday.AddDays(-31), withReason[DetachedRecords.ExportReasonSinceProperty]!.GetValue<DateTime>());
        Assert.Equal("the reasoned one", withReason["Note"]!.GetValue<string>());
        var withoutReason = Assert.Single(export, e => e["Id"]!.GetValue<string>() == detached.Id);
        Assert.False(withoutReason.ContainsKey(DetachedRecords.ExportReasonProperty) || withoutReason.ContainsKey(DetachedRecords.ExportReasonSinceProperty));

        // Through the export's JSON into another server's archive, the records built from the record alone, as a generic
        // owner's import adapter builds them.
        var file = JsonNode.Parse(new JsonArray(export.Select(e => (JsonNode?)e.DeepClone()).ToArray()).ToJsonString())!.AsArray();
        var otherOwner = new FakeRecordOwner { Name = "bookmarks", RelinkPolicy = RelinkPolicy.RuntimeWithin() };
        var other = RecordsOf(otherOwner, "other server");
        var incoming = file.Select(node => DetachedRecords.ReadExported(node!.AsObject()))
            .Select(entry => entry.WithExportedFacts(Archived(entry.Record.Deserialize<FakeRecord>(JsonStoreFormat.Options)!)))
            .ToList();
        Assert.Equal(2, other.ImportArchived(otherOwner, incoming, dryRun: false).Count);

        var imported = other.ArchiveOf(otherOwner).Read().Records;
        var importedReasoned = Assert.Single(imported, r => r.Id == reasoned.Id);
        Assert.Equal(("Gone", Monday.AddDays(-31)), (importedReasoned.Reason, importedReasoned.DetachedUtc));
        Assert.False(importedReasoned.Record.ContainsKey(DetachedRecords.ExportReasonProperty) || importedReasoned.Record.ContainsKey(DetachedRecords.ExportReasonSinceProperty));
        var importedDetached = Assert.Single(imported, r => r.Id == detached.Id);
        Assert.Equal((null, Monday.AddDays(-100)), (importedDetached.Reason, importedDetached.DetachedUtc));
        Assert.Equal(new Dictionary<string, int>(StringComparer.Ordinal) { ["Gone"] = 1 }, Assert.IsType<Dictionary<string, int>>(Owner(other.Describe())["ArchivedFor"]));

        // There, too, the reasoned record is never reattached, although its video is in the library; the export of that server
        // carries the reason again.
        Assert.Equal(1, (await other.ReattachFromArchiveAsync(otherOwner, ct)).Checked);
        Assert.Null(otherOwner.Find(reasoned.Id));
        Assert.Equal("Gone", Assert.Single(other.ExportArchive(otherOwner), e => e["Id"]!.GetValue<string>() == reasoned.Id)[DetachedRecords.ExportReasonProperty]!.GetValue<string>());
    }

    [Fact]
    public void AnExportedEntry_WithoutAReason_OrWithOneThatIsntOne_IsTheRecordAlone()
    {
        var record = new JsonObject { ["Id"] = "a", ["Note"] = "kept" };
        var plain = DetachedRecords.ReadExported(record);
        Assert.Equal((null, null), (plain.Reason, plain.ReasonSinceUtc));
        Assert.True(JsonNode.DeepEquals(record, plain.Record));

        var odd = DetachedRecords.ReadExported(new JsonObject
        {
            ["Id"] = "b",
            [DetachedRecords.ExportReasonProperty] = "Clip missing",
            [DetachedRecords.ExportReasonSinceProperty] = "2026-10-01T00:00:00Z"
        });
        Assert.Equal((null, null), (odd.Reason, odd.ReasonSinceUtc));
        Assert.Equal(["Id"], odd.Record.Select(p => p.Key));

        // A reason without a time keeps the time the adapter's record gives; an offset is read as UTC.
        var built = Archived(new FakeRecord { Id = "c", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday });
        Assert.Equal(("Gone", Monday), DetachedRecords.ReadExported(new JsonObject { ["Id"] = "c", [DetachedRecords.ExportReasonProperty] = "Gone" }).WithExportedFacts(built) is var noTime ? (noTime.Reason, noTime.DetachedUtc) : default);
        var offset = DetachedRecords.ReadExported(new JsonObject { ["Id"] = "c", [DetachedRecords.ExportReasonProperty] = "Gone", [DetachedRecords.ExportReasonSinceProperty] = "2026-10-01T02:00:00+02:00" });
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), offset.ReasonSinceUtc);
        Assert.Equal(DateTimeKind.Utc, offset.ReasonSinceUtc!.Value.Kind);
        Assert.Same(built, plain.WithExportedFacts(built));
    }

    [Fact]
    public async Task ARecordArchivedForAReason_ThatIsLiveAgain_IsSupersededByTheReattach_SoItNeverComesBack()
    {
        // A snapshot restored (or an import) brings the record back into the live store while its archived copy stays; the
        // reattach doesn't re-resolve such a record, but marks its copy superseded, as the startup reconcile would.
        var record = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        _records.ArchiveOf(_owner).Append([Archived(record) with { Reason = "ClipMissing" }]);
        Assert.Equal(0, Owner(_records.Describe())["Archived"]);

        await _records.ReattachFromArchiveAsync(_owner, TestContext.Current.CancellationToken);
        _owner.Records.Clear();

        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
        Assert.Empty(_records.ExportArchive(_owner));
    }

    [Fact]
    public async Task AnOwnersOwnReason_LeavesAReadOnlyOwnerAlone_AndIsOneWord()
    {
        var record = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var asked = 0;
        Func<Func<IReadOnlyList<ArchivedRecord>, IReadOnlyList<ArchivedRecord>>, System.Threading.CancellationToken, Task<IReadOnlyList<ArchivedRecord>>> move = (append, _) =>
        {
            asked++;
            return Task.FromResult(append([Archived(record)]));
        };
        _owner.IsReadOnly = true;

        var result = await _records.ArchiveForReasonAsync(_owner, "ClipMissing", move, TestContext.Current.CancellationToken);

        Assert.Equal(("ArchiveClipMissing", "ReadOnly", 0), (result.Step, result.Skipped, asked));
        Assert.False(File.Exists(_records.ArchiveOf(_owner).FilePath));
        _owner.IsReadOnly = false;
        await Assert.ThrowsAsync<ArgumentException>(() => _records.ArchiveForReasonAsync(_owner, "Clip missing", move, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => _records.ArchiveForReasonAsync(_owner, string.Empty, move, TestContext.Current.CancellationToken));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AnArchiveThatCantBeRead_Is503ForTheExportTheImportAndAPurgeWithIt_BeforeAnythingIsDeleted()
    {
        // These used to read an unreadable archive as empty, so an export (the backup) left the archived
        // records out without a word, a dry run called every record new, and a purge deleted the live records and then
        // failed with a raw IOException.
        var detached = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-40));
        var archive = _records.ArchiveOf(_owner);
        archive.Append([Archived(new FakeRecord { Id = "archived", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday.AddDays(-100) })]);
        var incoming = new[] { "archived", "new" }
            .Select(id => Archived(new FakeRecord { Id = id, Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday }))
            .ToList();
        var ct = TestContext.Current.CancellationToken;

        using (new FileStream(archive.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(
                (PluginErrorKind.Unavailable, DetachedRecords.ArchiveUnreadableExport),
                Kind(Assert.Throws<PluginException>(() => _records.ExportArchive(_owner))));
            Assert.Equal(
                (PluginErrorKind.Unavailable, DetachedRecords.ArchiveUnreadableImport),
                Kind(Assert.Throws<PluginException>(() => _records.ImportArchived(_owner, incoming, dryRun: true))));
            Assert.Equal(
                (PluginErrorKind.Unavailable, DetachedRecords.ArchiveUnreadableImport),
                Kind(Assert.Throws<PluginException>(() => _records.ImportArchived(_owner, incoming, dryRun: false))));
            Assert.Equal(
                (PluginErrorKind.Unavailable, DetachedRecords.ArchiveUnreadablePurge),
                Kind(await Assert.ThrowsAsync<PluginException>(() => _records.PurgeAsync(_owner, null, includeArchive: true, ct))));
            Assert.NotNull(_owner.Find(detached.Id));
            Assert.Empty(_owner.Snapshots);

            // A purge without the archive doesn't read it.
            Assert.Equal(1, (await _records.PurgeAsync(_owner, null, includeArchive: false, ct)).Purged);
        }

        Assert.Equal(new[] { "archived" }, archive.Read().Records.Select(r => r.Id));
        Assert.Equal("archived", Assert.Single(_records.ExportArchive(_owner))["Id"]!.GetValue<string>());
        Assert.Equal(new[] { "new" }, _records.ImportArchived(_owner, incoming, dryRun: true).Select(r => r.Id));
    }

    [Fact]
    public async Task APurgeWhoseArchiveCantBeRewritten_Is503_AndSaysWhatWasPurged()
    {
        // The archive reads, but its purge fails (here its copy can't be made: the pre-op folder's name is a file).
        var detached = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-40));
        var archive = _records.ArchiveOf(_owner);
        archive.Append([Archived(new FakeRecord { Id = "archived", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday.AddDays(-100) })]);
        Directory.CreateDirectory(_directory.Combine("snapshots"));
        await File.WriteAllTextAsync(Path.Combine(_directory.Combine("snapshots"), "pre-op"), "not a folder", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PluginException>(() => _records.PurgeAsync(_owner, null, includeArchive: true, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        Assert.StartsWith("1 detached records were purged, but the archive couldn't be", error.Message, StringComparison.Ordinal);
        Assert.Null(_owner.Find(detached.Id));
        Assert.Equal(new[] { "archived" }, archive.Read().Records.Select(r => r.Id));
    }

    [Fact]
    public async Task Reconcile_MarksArchivedCopiesOfLiveRecordsSuperseded()
    {
        // A crash between the archive's write and the store's: the record is in both places, and the store's copy counts.
        var record = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-5));
        _records.ArchiveOf(_owner).Append([Archived(record)]);
        Assert.Equal(0, Owner(_records.Describe())["Archived"]);

        await _records.ReconcileAsync(TestContext.Current.CancellationToken);
        _owner.Records.Clear();

        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
        Assert.Single(_logger.At(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    [Fact]
    public async Task Reattach_ChecksAtMost500PerRun_LeastRecentlyCheckedFirst()
    {
        var archive = _records.ArchiveOf(_owner);
        archive.Append(Enumerable.Range(0, 600).Select(i => Archived(new FakeRecord
        {
            Id = "r" + i,
            Identity = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "m" + i },
            DetachedUtc = Monday.AddDays(-400)
        })).ToList());

        var first = await _records.ReattachFromArchiveAsync(_owner, TestContext.Current.CancellationToken);
        Assert.Equal(DetachedRecords.MaxReattachPerRun, first.Checked);
        Assert.Equal(new[] { "r500", "r501" }, archive.NextToCheck(2).Select(r => r.Id));

        var second = await _records.ReattachFromArchiveAsync(_owner, TestContext.Current.CancellationToken);
        Assert.Equal(DetachedRecords.MaxReattachPerRun, second.Checked);
        Assert.Equal(new[] { "r400", "r401" }, archive.NextToCheck(2).Select(r => r.Id));
    }

    [Fact]
    public async Task Reattach_ARecordThatIsLiveAgain_IsSupersededNotDuplicated()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var record = _owner.Add(ItemIdentity.Capture(movie, _library.Object));
        _records.ArchiveOf(_owner).Append([Archived(record)]);

        var result = await _records.ReattachFromArchiveAsync(_owner, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Relinked);
        Assert.Single(_owner.Records);
        _owner.Records.Clear();
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
    }

    [Fact]
    public async Task Purge_IsTheOnlyHardDelete_AfterASnapshot_AndHonoursTheAge()
    {
        var old = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-40));
        var recent = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-2));
        var attached = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        _records.ArchiveOf(_owner).Append([
            Archived(new FakeRecord { Id = "old-archived", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday.AddDays(-100) }),
            Archived(new FakeRecord { Id = "new-archived", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday.AddDays(-3) })
        ]);

        var withoutArchive = await _records.PurgeAsync(_owner, 30, includeArchive: false, TestContext.Current.CancellationToken);
        Assert.Equal(1, withoutArchive.Purged);
        Assert.Null(_owner.Find(old.Id));
        Assert.NotNull(_owner.Find(recent.Id));
        Assert.Equal(new[] { "bookmarks-purge-0.json" }, withoutArchive.Snapshots);
        Assert.Equal(2, _records.ArchiveOf(_owner).Read().Records.Count);

        var all = await _records.PurgeAsync(_owner, null, includeArchive: true, TestContext.Current.CancellationToken);
        Assert.Equal(3, all.Purged);
        Assert.Equal(2, all.Snapshots.Count);
        Assert.StartsWith("bookmarks-archived-purge-", all.Snapshots[1], StringComparison.Ordinal);
        Assert.Equal(new[] { attached.Id }, _owner.Records.Select(r => r.Id));
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);

        await Assert.ThrowsAsync<PluginException>(() => _records.PurgeAsync(_owner, -1, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ImportArchived_AddsOnlyRecordsThatAreNeitherLiveNorArchived_AndADryRunWritesNothing()
    {
        var live = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        _records.ArchiveOf(_owner).Append([Archived(new FakeRecord { Id = "archived", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday })]);
        var incoming = new[] { "archived", live.Id, "new", "new" }
            .Select(id => Archived(new FakeRecord { Id = id, Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday }))
            .ToList();

        var dry = _records.ImportArchived(_owner, incoming, dryRun: true);
        Assert.Equal(new[] { "new" }, dry.Select(r => r.Id));
        Assert.Single(_records.ArchiveOf(_owner).Read().Records);

        var added = _records.ImportArchived(_owner, incoming, dryRun: false);
        Assert.Equal(new[] { "new" }, added.Select(r => r.Id));
        Assert.Equal(new[] { "archived", "new" }, _records.ArchiveOf(_owner).Read().Records.Select(r => r.Id));
    }

    [Fact]
    public void ImportArchived_Replace_MakesTheArchiveTheRecordsNotLive_TheEarlierOnesGone_AndADryRunWritesNothing()
    {
        // An envelope Replace makes the archive the file's.
        var live = _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        _records.ArchiveOf(_owner).Append([Archived(new FakeRecord { Id = "only-here", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday })]);
        var incoming = new[] { live.Id, "from-file", "from-file" }
            .Select(id => Archived(new FakeRecord { Id = id, Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday }))
            .ToList();

        Assert.Equal(new[] { "from-file" }, _records.ImportArchived(_owner, incoming, dryRun: true, replace: true).Select(r => r.Id));
        Assert.Equal(new[] { "only-here" }, _records.ArchiveOf(_owner).Read().Records.Select(r => r.Id));

        Assert.Equal(new[] { "from-file" }, _records.ImportArchived(_owner, incoming, dryRun: false, replace: true).Select(r => r.Id));
        Assert.Equal(new[] { "from-file" }, _records.ArchiveOf(_owner).Read().Records.Select(r => r.Id));

        // A file without archived records empties the archive.
        Assert.Empty(_records.ImportArchived(_owner, [], dryRun: false, replace: true));
        Assert.Empty(_records.ArchiveOf(_owner).Read().Records);
    }

    [Fact]
    public void Describe_CountsTheDetached_TheOldest_TheArchived_AndThePendingRelinks()
    {
        File.WriteAllLines(_directory.Combine("moves.jsonl"), [Header(), Line(1, A, B, utc: Monday.AddDays(-1))]);
        _owner.Add(new IdentitySnapshot { ItemId = B, Kind = "Movie" });
        _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-12.5));
        _owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: Monday.AddDays(-1));
        _owner.DetachedRetentionDays = 90;
        _records.ArchiveOf(_owner).Append([Archived(new FakeRecord { Id = "x", Identity = new IdentitySnapshot { ItemId = Guid.NewGuid() }, DetachedUtc = Monday })]);

        var described = _records.Describe();
        var owner = Owner(described);

        Assert.Equal(2, owner["Count"]);
        Assert.Equal(12, owner["OldestDays"]);
        Assert.Equal(1, owner["Archived"]);
        Assert.Equal(1, owner["PendingRelinks"]);
        Assert.Equal(90, owner["RetentionDays"]);
        Assert.Equal("bookmarks: 2 detached, oldest 12 days; 1 archived", described["Summary"]);
    }

    private static IDictionary<string, object?> Owner(IDictionary<string, object?> described)
        => Assert.Single((IEnumerable<IDictionary<string, object?>>)described["Owners"]!);

    private static IdentitySnapshot WithoutRuntime() => new()
    {
        ItemId = Guid.NewGuid(),
        Kind = "Movie",
        Name = "Lone Movie",
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "900003" }
    };

    private static (PluginErrorKind Kind, string Message) Kind(PluginException exception) => (exception.Kind, exception.Message);

    private static ArchivedRecord Archived(FakeRecord record) => new()
    {
        Id = record.Id,
        UserId = record.UserId,
        Identity = record.Identity,
        AttachedUtc = record.AttachedUtc,
        DetachedUtc = record.DetachedUtc ?? Monday,
        Record = System.Text.Json.JsonSerializer.SerializeToNode(record, Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options)!.AsObject()
    };

    private DetachedRecords RecordsOf(IDetachedRecordOwner owner, string server = "")
    {
        var journal = new MoveJournalReader(_directory.Combine("moves.jsonl"), _logger);
        return new DetachedRecords(
            _directory.Combine(server, "archive"),
            _directory.Combine(server, "snapshots"),
            _library.Object,
            id => _users.GetValueOrDefault(id),
            new IdentityResolver(_library.Object, journal, _clock),
            journal,
            () => [owner],
            _logger,
            _clock);
    }

    private User NewUser(string name)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Id = Guid.NewGuid() };
        _users[user.Id] = user;
        return user;
    }
}
