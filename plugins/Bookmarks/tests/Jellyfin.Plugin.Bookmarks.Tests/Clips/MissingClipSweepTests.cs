using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The missing-clip sweep of the daily maintenance, on the real store,
/// real files, the kit's archive and the bookmarks' real record owner: a file gone from a clip folder that is there marked
/// missing, a file back cleared; a clip folder that isn't there (before or during the run) left out whole, never marked and
/// never archived, and checked again once it is back; the retention at its boundary, deleted bookmarks' clips into the
/// archive with the reason ClipMissing (exported, never reattached), live bookmarks' clip entries cleared, the bookmarks
/// staying; clips being made left alone; read-only data untouched; the run racing a clip being made, made again or deleted;
/// refused and unreadable paths left alone. No file is ever deleted, moved or renamed.
/// </summary>
public sealed class MissingClipSweepTests : IDisposable
{
    // The run's time: well after the fixture's records (ClipListFixture.T0).
    private static readonly DateTime Now = new(2026, 11, 15, 3, 10, 0, DateTimeKind.Utc);
    private static readonly Guid Alice = Guid.NewGuid();

    private readonly ClipListFixture _clips = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly PluginConfiguration _configuration = new();
    private readonly FakeClipFileSystem _files = new();
    private readonly FakeLibrary _library = new();
    private readonly ListLogger _logger = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly List<IDisposable> _disposables = [];
    private DetachedRecords _detached = null!;
    private BookmarkIdentityService _owner = null!;

    public MissingClipSweepTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ArchiveFile => _detached.ArchiveOf(_owner).FilePath;

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _clips.Dispose();
    }

    [Fact]
    public async Task AFileGoneFromAClipFolderThatIsThere_IsMarkedMissing_AndOneThatIsBack_IsCleared_NothingElseChanges()
    {
        var present = await _clips.AddAsync(Alice, new ClipSpec { Name = "present" }, Ct);
        var gone = await _clips.AddAsync(Alice, new ClipSpec { Name = "gone", Note = "its note", OnDisk = false }, Ct);
        var back = await _clips.AddAsync(Alice, new ClipSpec { Name = "back", MissingSince = Now.AddDays(-3) }, Ct);
        var stillGone = await _clips.AddAsync(Alice, new ClipSpec { Name = "still gone", OnDisk = false, MissingSince = Now.AddDays(-3) }, Ct);
        var keptGone = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept and gone", OnDisk = false, Tombstone = true }, Ct);
        var failed = await _clips.AddAsync(Alice, new ClipSpec { Name = "failed", HasFile = false, Status = BookmarkClipStatus.Failed }, Ct);

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((5, 2, 3, 2, 1, 0), (result.Checked, result.Present, result.Missing, result.Marked, result.Cleared, result.Changed));
        Assert.Equal((0, 0, 0, 0, 0, 0), (result.InProgress, result.FolderUnavailable, result.Unreadable, result.Refused, result.Archived, result.ClipsForgotten));
        Assert.Null(MissingSince(present));
        Assert.Equal(Now, MissingSince(gone));
        Assert.Null(MissingSince(back));
        Assert.Equal(Now.AddDays(-3), MissingSince(stillGone));
        Assert.Equal(Now, MissingSince(keptGone));
        Assert.Same(failed, _clips.Store.Get(failed.Id));

        // Only the mark changed: the name, the note, UpdatedAt (the IfUpdatedAt token), the clip's every other field.
        foreach (var before in new[] { gone, back, keptGone })
        {
            var after = _clips.Store.Get(before.Id)!;
            Assert.Equal(before, after with { Clip = after.Clip! with { MissingSinceUtc = before.Clip!.MissingSinceUtc } });
        }

        Assert.Same(present, _clips.Store.Get(present.Id));
        Assert.Same(stillGone, _clips.Store.Get(stillGone.Id));
        Assert.Empty(_files.Deletes);
        Assert.Empty(_files.Moves);
        Assert.True(File.Exists(present.Clip!.FilePath));

        // A second run finds nothing to change, and writes nothing.
        var bytes = await File.ReadAllBytesAsync(_clips.Directory.Combine("data", "bookmarks.json"), Ct);
        var second = await Sweep().RunAsync(Ct);
        Assert.Equal((0, 0), (second.Marked, second.Cleared));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_clips.Directory.Combine("data", "bookmarks.json"), Ct));
    }

    [Fact]
    public async Task AClipFolderThatIsntThere_IsLeftOutWhole_NothingInItMarkedOrArchived_AndDiagnosticsNamesIt()
    {
        // A clip drive unplugged: the files on it are probably fine; nothing is marked, archived or forgotten, also
        // a deleted bookmark's clip and a bookmark's own that were marked missing long before (the retention would take them
        // if their folder were there).
        var usb = await AddClipFolderAsync("usb");
        var onUsb = Path.Combine(usb, "alice");
        var there = await _clips.AddAsync(Alice, new ClipSpec { Name = "on usb", Folder = onUsb }, Ct);
        var gone = await _clips.AddAsync(Alice, new ClipSpec { Name = "gone on usb", Folder = onUsb, OnDisk = false }, Ct);
        var keptLongAgo = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept on usb", Folder = onUsb, OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-100) }, Ct);
        var liveLongAgo = await _clips.AddAsync(Alice, new ClipSpec { Name = "live on usb", Folder = onUsb, OnDisk = false, MissingSince = Now.AddDays(-100) }, Ct);
        var onC = await _clips.AddAsync(Alice, new ClipSpec { Name = "gone on C", OnDisk = false }, Ct);
        ClipFileSupport.Unplug(usb);

        var sweep = Sweep();
        var result = await sweep.RunAsync(Ct);

        Assert.Equal((4, 1, 1, 0, 0), (result.FolderUnavailable, result.Missing, result.Marked, result.Archived, result.ClipsForgotten));
        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [usb] = new(4, ClipFolderLeftOut.NotThere) }, result.UnavailableFolders);
        foreach (var record in new[] { there, gone, keptLongAgo, liveLongAgo })
        {
            Assert.Same(record, _clips.Store.Get(record.Id));
        }

        Assert.Equal(Now, MissingSince(onC));
        Assert.False(File.Exists(ArchiveFile));

        // Diagnostics (admins only) names the folder; the log names no folder and no clip at Information or above.
        var described = sweep.Describe();
        Assert.Equal(30, described["RetentionDays"]);
        Assert.Equal(3, described["MarkedMissing"]);
        var folders = (IEnumerable<IDictionary<string, object?>>)((IDictionary<string, object?>)described["LastRun"]!)["UnavailableFolders"]!;
        Assert.Equal((usb, 4, "NotThere"), folders.Select(f => ((string)f["Folder"]!, (int)f["Clips"]!, (string)f["Reason"]!)).Single());
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.StartsWith("1 clip folders were left out (1 not there, 0 there but not recognised by their marker file, 0 without their marker file and with some of their clips gone, 0 whose marker file couldn't be written, 0 with every clip gone at once)", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries.Where(e => e.Level >= LogLevel.Information), e => e.Message.Contains(usb, StringComparison.OrdinalIgnoreCase) || e.Message.Contains("on usb", StringComparison.Ordinal));
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Message.Contains(usb, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AClipFolderThatComesBack_IsCheckedAgain_ItsMissingFilesMarkedFromThen_AndTheRetentionTakesWhatWasMarkedBefore()
    {
        var usb = await AddClipFolderAsync("usb");
        var onUsb = Path.Combine(usb, "alice");
        var there = await _clips.AddAsync(Alice, new ClipSpec { Name = "on usb", Folder = onUsb }, Ct);
        var gone = await _clips.AddAsync(Alice, new ClipSpec { Name = "gone on usb", Folder = onUsb, OnDisk = false }, Ct);
        var keptLongAgo = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept on usb", Folder = onUsb, OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-100) }, Ct);
        ClipFileSupport.Unplug(usb);
        Assert.Equal(3, (await Sweep().RunAsync(Ct)).FolderUnavailable);

        // A week later the drive is back.
        ClipFileSupport.PlugIn(usb);
        _clock.Advance(TimeSpan.FromDays(7));
        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((0, 1, 2, 1, 1), (result.FolderUnavailable, result.Present, result.Missing, result.Marked, result.Archived));
        Assert.Empty(result.UnavailableFolders);
        Assert.Same(there, _clips.Store.Get(there.Id));
        Assert.Equal(Now.AddDays(7), MissingSince(gone));
        Assert.Null(_clips.Store.Get(keptLongAgo.Id));
        Assert.Equal(keptLongAgo.Id.ToString("D"), Assert.Single(_detached.ArchiveOf(_owner).Read().Records).Id);
    }

    [Fact]
    public async Task AClipFolderUnpluggedWhileTheRunLooks_IsTakenForOffline_NeverForMissingFiles()
    {
        // The guard looks at the clip folder, then at the file: a drive unplugged between the two reads as "nothing there" for
        // the file (an unplugged drive's DirectoryNotFoundException). Such a "missing" must never be written: the folder is
        // looked at again once the run has looked at its files, and a clip folder gone for any clip of the run is left out.
        var usb = await AddClipFolderAsync("usb");
        var first = await _clips.AddAsync(Alice, new ClipSpec { Name = "first", Folder = Path.Combine(usb, "alice") }, Ct);
        var unplug = new UnpluggedAfter(usb, folderLooks: 1);

        var alone = await Sweep(fileSystem: unplug).RunAsync(Ct);

        Assert.Equal((1, 0, 0), (alone.FolderUnavailable, alone.Missing, alone.Marked));
        Assert.Equal(2, unplug.FolderLooks);
        Assert.Same(first, _clips.Store.Get(first.Id));

        // With two clips in it, the second clip's look finds the folder gone: both left out.
        var second = await _clips.AddAsync(Alice, new ClipSpec { Name = "second", Folder = Path.Combine(usb, "alice") }, Ct);
        var both = await Sweep(fileSystem: new UnpluggedAfter(usb, folderLooks: 1)).RunAsync(Ct);

        Assert.Equal((2, 0, 0), (both.FolderUnavailable, both.Missing, both.Marked));
        Assert.Same(first, _clips.Store.Get(first.Id));
        Assert.Same(second, _clips.Store.Get(second.Id));
    }

    [Fact]
    public async Task AFileThatReappears_IsClearedAndNeverArchived_EvenPastTheRetention()
    {
        var back = await _clips.AddAsync(Alice, new ClipSpec { Name = "back", Tombstone = true, MissingSince = Now.AddDays(-40) }, Ct);
        var liveBack = await _clips.AddAsync(Alice, new ClipSpec { Name = "live back", MissingSince = Now.AddDays(-40) }, Ct);

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((2, 2, 0, 0), (result.Present, result.Cleared, result.Archived, result.ClipsForgotten));
        Assert.Null(MissingSince(back));
        Assert.Null(MissingSince(liveBack));
        Assert.False(File.Exists(ArchiveFile));
    }

    [Fact]
    public async Task TheRetention_MovesADeletedBookmarksClipIntoTheArchive_AndForgetsABookmarksOwnClip_AtItsBoundary_NothingDeleted()
    {
        // 30 days by default; a file missing exactly that long is due (the kit's retention rule: at least that many
        // days), one tick less isn't.
        var due = await _clips.AddAsync(Alice, new ClipSpec { Name = "Bank job", Note = "the kept clip's note", OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-30) }, Ct);
        var early = await _clips.AddAsync(Alice, new ClipSpec { Name = "early", OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-30).AddTicks(1) }, Ct);
        var liveDue = await _clips.AddAsync(Alice, new ClipSpec { Name = "Car chase", Note = "the bookmark's note", OnDisk = false, MissingSince = Now.AddDays(-45) }, Ct);
        var liveEarly = await _clips.AddAsync(Alice, new ClipSpec { Name = "live early", OnDisk = false, MissingSince = Now.AddDays(-29) }, Ct);
        var present = await _clips.AddAsync(Alice, new ClipSpec { Name = "present" }, Ct);
        Assert.Equal(30, _configuration.MissingClipRetentionDays);

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((4, 0, 1, 1, 0), (result.Missing, result.Marked, result.Archived, result.ClipsForgotten, result.Changed));
        Assert.Null(_clips.Store.Get(due.Id));
        Assert.Same(early, _clips.Store.Get(early.Id));
        Assert.Same(liveEarly, _clips.Store.Get(liveEarly.Id));
        Assert.Same(present, _clips.Store.Get(present.Id));

        // The live bookmark stays, its name, note, range and UpdatedAt as they were; only its clip entry went.
        var forgotten = _clips.Store.Get(liveDue.Id)!;
        Assert.Null(forgotten.Clip);
        Assert.Equal(liveDue with { Clip = null }, forgotten);

        // The deleted bookmark's clip is in the archive (the kit never hard-deletes on a timer), with its reason, the time
        // its file went missing, its name and note; the export takes it; the kit's Diagnostics counts it by reason.
        var archived = Assert.Single(_detached.ArchiveOf(_owner).Read().Records);
        Assert.Equal((due.Id.ToString("D"), MissingClipSweep.ArchiveReason, Now.AddDays(-30), Alice), (archived.Id, archived.Reason, archived.DetachedUtc, archived.UserId));
        Assert.Equal(("Bank job", "the kept clip's note"), (archived.Record["Name"]!.GetValue<string>(), archived.Record["Note"]!.GetValue<string>()));
        Assert.NotNull(archived.Record["DeletedUtc"]);
        Assert.Contains("\"Reason\":\"ClipMissing\"", await File.ReadAllTextAsync(ArchiveFile, Ct), StringComparison.Ordinal);
        Assert.Equal(due.Id.ToString("D"), Assert.Single(_detached.ExportArchive(_owner))["Id"]!.GetValue<string>());
        var owner = Assert.Single((IEnumerable<IDictionary<string, object?>>)_detached.Describe()["Owners"]!);
        Assert.Equal(1, Assert.IsType<Dictionary<string, int>>(owner["ArchivedFor"])[MissingClipSweep.ArchiveReason]);

        // No file was deleted, moved or renamed: there was none to delete, and the one there is untouched.
        Assert.Empty(_files.Deletes);
        Assert.Empty(_files.Moves);
        Assert.True(File.Exists(present.Clip!.FilePath));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("1 deleted bookmarks' clips missing for 30 days or more moved to the archive, 1 bookmarks' clip entries cleared", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries.Where(e => e.Level >= LogLevel.Information), e => e.Message.Contains("Bank job", StringComparison.Ordinal) || e.Message.Contains("note", StringComparison.Ordinal));

        // One tick later the early ones are due too.
        _clock.Advance(TimeSpan.FromTicks(1));
        var next = await Sweep().RunAsync(Ct);
        Assert.Equal((1, 0), (next.Archived, next.ClipsForgotten));
        Assert.Null(_clips.Store.Get(early.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ARetentionOf0OrLess_KeepsEveryMissingClip(int days)
    {
        _configuration.MissingClipRetentionDays = days;
        var kept = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept", OnDisk = false, Tombstone = true, MissingSince = Now.AddYears(-10) }, Ct);
        var live = await _clips.AddAsync(Alice, new ClipSpec { Name = "live", OnDisk = false, MissingSince = Now.AddYears(-10) }, Ct);

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((0, 2, 0, 0), (result.RetentionDays, result.Missing, result.Archived, result.ClipsForgotten));
        Assert.Same(kept, _clips.Store.Get(kept.Id));
        Assert.Same(live, _clips.Store.Get(live.Id));
    }

    [Fact]
    public async Task AClipBeingMade_IsLeftAlone_NeitherMarkedNorForgotten()
    {
        // A remake running over a file that went missing long ago, and one waiting: their jobs decide their files.
        var running = await _clips.AddAsync(Alice, new ClipSpec { Name = "running", OnDisk = false, Status = BookmarkClipStatus.Running, MissingSince = Now.AddDays(-100) }, Ct);
        var queued = await _clips.AddAsync(Alice, new ClipSpec { Name = "queued", OnDisk = false, Status = BookmarkClipStatus.Queued }, Ct);

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((2, 0, 0, 0), (result.InProgress, result.Checked, result.Marked, result.ClipsForgotten));
        Assert.Same(running, _clips.Store.Get(running.Id));
        Assert.Same(queued, _clips.Store.Get(queued.Id));
    }

    [Fact]
    public async Task AClipWhoseRenameWasntFinished_IsNeitherMarkedNorArchived_UntilTheRenameIsFinished()
    {
        // Clip rename: a stop between a rename's move and its record's write leaves the record naming the path the file left.
        // The sweep (a daily run before the next start, or one an admin starts) never takes that for a missing file: the
        // rename's journal holds both paths; once finished (as the next start's janitor, or the maintenance's ClipFiles step
        // before the sweep, finishes it), the record names the file, which is there.
        var renamed = await _clips.AddAsync(Alice, new ClipSpec { Name = "renamed" }, Ct);
        var from = renamed.Clip!.FilePath!;
        var to = Path.Combine(Path.GetDirectoryName(from)!, "Heat (1995) - moved [00.01.00-00.01.30].mp4");
        new ClipRenameJournal(_clips.Directory.Combine("data"), PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(new ClipRenameEntry
        {
            BookmarkId = renamed.Id,
            From = from,
            To = to,
            File = PhysicalClipFileSystem.ReadFileIdentity(from),
            UpdatedAt = renamed.UpdatedAt,
            StartedUtc = Now
        });
        File.Move(from, to);
        _clock.Advance(TimeSpan.FromDays(40));

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((1, 0, 0, 0, 0), (result.Unreadable, result.Missing, result.Marked, result.Archived, result.ClipsForgotten));
        Assert.Same(renamed, _clips.Store.Get(renamed.Id));

        var finished = await ClipFileSupport.Ops(_clips.Store, fileSystem: _files).FinishRenamesAsync(BackgroundWriter.Maintenance, Ct);
        Assert.Equal(1, finished.Completed);
        var again = await Sweep().RunAsync(Ct);
        Assert.Equal((1, 0, 0), (again.Present, again.Missing, again.Marked));
        Assert.Equal(to, _clips.Store.Get(renamed.Id)!.Clip!.FilePath);
    }

    [Fact]
    public async Task APathTheGuardRefusesOrCantInspect_IsLeftAsItIs()
    {
        var elsewhere = _clips.Directory.Combine("elsewhere");
        var outside = await _clips.AddAsync(Alice, new ClipSpec { Name = "outside", Folder = elsewhere, OnDisk = false }, Ct);
        var linked = await _clips.AddAsync(Alice, new ClipSpec { Name = "linked", Folder = Path.Combine(_clips.Root, "bob"), OnDisk = false }, Ct);
        _files.ReparsePoints.Add(Path.Combine(_clips.Root, "bob"));
        var unreadable = await _clips.AddAsync(Alice, new ClipSpec { Name = "unreadable", Folder = Path.Combine(_clips.Root, "carol") }, Ct);
        _files.AttributesFailure = path => path.StartsWith(Path.Combine(_clips.Root, "carol") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? new IOException("The device is not ready.")
            : null;

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((2, 1, 0, 0), (result.Refused, result.Unreadable, result.Missing, result.Marked));
        Assert.Same(outside, _clips.Store.Get(outside.Id));
        Assert.Same(linked, _clips.Store.Get(linked.Id));
        Assert.Same(unreadable, _clips.Store.Get(unreadable.Id));
    }

    [Fact]
    public async Task ReadOnlyData_RunsNothing()
    {
        Directory.CreateDirectory(_clips.Directory.Combine("readonly"));
        var file = _clips.Directory.Combine("readonly", "bookmarks.json");
        var original = StoreFixtures.Read(StoreFixtures.Schema3);
        await File.WriteAllBytesAsync(file, original, Ct);
        using var store = BookmarkStore.Create(file, NullLogger.Instance);
        _configuration.MissingClipRetentionDays = 1;

        var sweep = Sweep(store);
        var result = await sweep.RunAsync(Ct);

        Assert.Equal(("ReadOnly", 0), (result.Skipped, result.Checked));
        Assert.Equal(original, await File.ReadAllBytesAsync(file, Ct));
        Assert.False(File.Exists(ArchiveFile));
        Assert.Equal("ReadOnly", ((IDictionary<string, object?>)sweep.Describe()["LastRun"]!)["Skipped"]);
    }

    [Fact]
    public async Task AClipMadeAgainBetweenTheLookAndTheWrite_IsntMarked()
    {
        // A case found in testing: a remake of a missing clip takes its name again. The worker moves the new file into place and
        // records it while the run decides; the run's "missing" is from before, so it writes nothing to that clip.
        var store = new InterferingBookmarkStore(_clips.Store);
        var remade = await _clips.AddAsync(Alice, new ClipSpec { Name = "remade", OnDisk = false }, Ct);
        store.BeforeNextDocumentWrite = async () =>
        {
            await WriteFileAsync(remade.Clip!.FilePath!, [1, 2, 3]);
            await _clips.Store.UpdateAsync(remade.Id, b => b with { Clip = b.Clip! with { RequestedAt = Now, CompletedAt = Now, FileSize = 3 } }, Ct);
        };

        var result = await Sweep(store).RunAsync(Ct);

        Assert.Equal((1, 0, 1), (result.Missing, result.Marked, result.Changed));
        Assert.Null(MissingSince(remade));
        Assert.Equal(3, _clips.Store.Get(remade.Id)!.Clip!.FileSize);

        // The next run finds it there.
        var next = await Sweep().RunAsync(Ct);
        Assert.Equal((1, 0, 0), (next.Present, next.Marked, next.Cleared));
    }

    [Fact]
    public async Task AClipDeletedBetweenTheLookAndTheWrite_StaysDeleted()
    {
        var store = new InterferingBookmarkStore(_clips.Store);
        var cleared = await _clips.AddAsync(Alice, new ClipSpec { Name = "clip deleted", OnDisk = false }, Ct);
        var removed = await _clips.AddAsync(Alice, new ClipSpec { Name = "tombstone removed", OnDisk = false, Tombstone = true }, Ct);

        // A clip of the folder there, so it isn't a folder whose every clip is gone at once (the sweep's second guard).
        await _clips.AddAsync(Alice, new ClipSpec { Name = "there" }, Ct);
        store.BeforeNextDocumentWrite = async () =>
        {
            // The user's delete of the one clip (the bookmark stays) and of the other deleted bookmark's kept clip (204).
            await _clips.Store.UpdateAsync(cleared.Id, b => b with { Clip = null }, Ct);
            await _clips.Store.RemoveAsync(removed.Id, Ct);
        };

        var result = await Sweep(store).RunAsync(Ct);

        Assert.Equal((2, 0, 2), (result.Missing, result.Marked, result.Changed));
        Assert.Null(_clips.Store.Get(cleared.Id)!.Clip);
        Assert.Null(_clips.Store.Get(removed.Id));
    }

    [Fact]
    public async Task TheRetention_DecidesAgainUnderTheStoresLock_AFileBackOrAClipAskedForAgainStays()
    {
        // Both were missing long enough when the run looked; while it decides, the deleted bookmark's file is put back and the
        // other clip is asked for again (queued). Neither may leave: the retention looks at the record and the file again.
        var store = new InterferingBookmarkStore(_clips.Store);
        var putBack = await _clips.AddAsync(Alice, new ClipSpec { Name = "put back", OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-40) }, Ct);
        var askedAgain = await _clips.AddAsync(Alice, new ClipSpec { Name = "asked again", OnDisk = false, MissingSince = Now.AddDays(-40) }, Ct);
        store.BeforeNextDocumentWrite = async () =>
        {
            await WriteFileAsync(putBack.Clip!.FilePath!, [1]);
            await _clips.Store.UpdateAsync(askedAgain.Id, b => b with { Clip = b.Clip! with { Status = BookmarkClipStatus.Queued } }, Ct);
        };

        var result = await Sweep(store).RunAsync(Ct);

        Assert.Equal((2, 0, 0, 2), (result.Missing, result.Archived, result.ClipsForgotten, result.Changed));
        Assert.NotNull(_clips.Store.Get(putBack.Id));
        Assert.Equal(BookmarkClipStatus.Queued, _clips.Store.Get(askedAgain.Id)!.Clip!.Status);
        Assert.False(File.Exists(ArchiveFile) && _detached.ArchiveOf(_owner).Read().Records.Count > 0);
    }

    [Fact]
    public async Task ARemakeOfAMissingClip_IsLeftAloneWhileTheWorkerMakesIt_AndIsThereOnceMade()
    {
        // The real clip worker with a fake ffmpeg: the bookmark's clip file went missing 40 days ago, and its owner makes it
        // again. While the job runs the sweep leaves it (InProgress, nothing forgotten); once made, the new file is the clip's
        // and its "missing since" is gone with the old file (the worker's completion clears it).
        var planner = new FakeClipPlanner(_clips.Directory) { NamedFiles = true };
        var runner = new FakeFfmpegRunner();
        var files = ClipFileSupport.Ops(_clips.Store);
        using var worker = new ClipExtractionService(
            _clips.Store,
            new BackgroundWriteGate(_clips.Store, NullLogger<BackgroundWriteGate>.Instance),
            planner,
            runner,
            files,
            ClipFileSupport.Janitor(_clips.Store, files),
            ClipFileSupport.Guard(_clips.Store),
            TestData.ConfigurationProvider(_configuration),
            _host.Object,
            NullLogger<ClipExtractionService>.Instance);
        await worker.StartAsync(Ct);
        try
        {
            var bookmark = await _clips.AddAsync(Alice, new ClipSpec { Name = "remade", OnDisk = false, Mode = BookmarkClipMode.Fast, MissingSince = Now.AddDays(-40) }, Ct);
            runner.HoldUntilReleased();
            await worker.EnqueueAsync(bookmark.Id, Alice, BookmarkClipMode.Fast, null, Ct);
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            var during = await Sweep().RunAsync(Ct);
            Assert.Equal((1, 0, 0, 0), (during.InProgress, during.Checked, during.ClipsForgotten, during.Archived));
            Assert.Equal(BookmarkClipStatus.Running, _clips.Store.Get(bookmark.Id)!.Clip!.Status);

            // Even a mark written while the job ran (by a version that marks clips being made) goes with the old file.
            await _clips.Store.UpdateAsync(bookmark.Id, b => b with { Clip = b.Clip! with { MissingSinceUtc = Now.AddDays(-1) } }, Ct);
            runner.Release();
            var done = await WaitForAsync(() => _clips.Store.Get(bookmark.Id) is { Clip: { Status: BookmarkClipStatus.Completed } clip } record && clip.RequestedAt != bookmark.Clip!.RequestedAt ? record : null);
            Assert.True(File.Exists(done.Clip!.FilePath));
            Assert.Null(done.Clip.MissingSinceUtc);

            var after = await Sweep().RunAsync(Ct);
            Assert.Equal((1, 1, 0, 0), (after.Checked, after.Present, after.Marked, after.ClipsForgotten));
        }
        finally
        {
            runner.Release();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnArchivedMissingClip_NeverComesBackFromTheDailyReattach_EvenWithItsVideoInTheLibrary()
    {
        // Archived tombstones are not re-resolved (there is no bookmark to restore). Its video is in the library,
        // so the kit's resolver would find it at once (an Exact match).
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var kept = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept", OnDisk = false, Tombstone = true, ItemId = movie.Id, MissingSince = Now.AddDays(-31) }, Ct);
        Assert.Equal(1, (await Sweep().RunAsync(Ct)).Archived);

        var reattach = await _detached.ReattachFromArchiveAsync(_owner, Ct);

        Assert.Equal((0, 0), (reattach.Checked, reattach.Relinked));
        Assert.Null(_clips.Store.Get(kept.Id));
        Assert.Single(_detached.ArchiveOf(_owner).Read().Records);
    }

    [Fact]
    public async Task ADetachedBookmarksMissingClip_IsTheSweeps_TheKitsRetentionNeverArchivesARecordWithAClipFile()
    {
        // A deleted bookmark whose video left the library and whose file is gone too: the kit's detached retention never takes
        // a record with a clip file, so the sweep's retention is the one that archives it, with its reason.
        _configuration.DetachedRetentionDays = 1;
        var both = await _clips.AddAsync(Alice, new ClipSpec { Name = "both gone", OnDisk = false, Tombstone = true, Detached = Now.AddDays(-60), MissingSince = Now.AddDays(-31) }, Ct);
        var sweep = Sweep();

        Assert.Equal(0, (await _detached.ArchiveDueAsync(_owner, Ct)).Archived);
        Assert.NotNull(_clips.Store.Get(both.Id));
        Assert.Equal(1, (await sweep.RunAsync(Ct)).Archived);

        var archived = Assert.Single(_detached.ArchiveOf(_owner).Read().Records);
        Assert.Equal((MissingClipSweep.ArchiveReason, Now.AddDays(-31)), (archived.Reason, archived.DetachedUtc));
    }

    [Fact]
    public async Task AClipFolderAway_IsNeverMadeAgainByAJob_AndNothingInItIsMarkedOrForgotten_EvenWithAFolderInItsPlace()
    {
        // Found in testing: two bookmarks' clips and a deleted bookmark's kept clip in the clip folder, which goes away (an
        // unplugged drive). An early 1.2.0 build's clip worker made the folder again for a clip asked for meanwhile, the next run found
        // the folder there and every file in it "missing", and 31 days on the bookmarks lost their clip entries and the kept
        // clip went to the archive, every file intact on the drive.
        var golf = await _clips.AddAsync(Alice, new ClipSpec { Name = "Golf" }, Ct);
        var hotel = await _clips.AddAsync(Alice, new ClipSpec { Name = "Hotel" }, Ct);
        var india = await _clips.AddAsync(Alice, new ClipSpec { Name = "India", Tombstone = true }, Ct);
        var root = _clips.Root;
        ClipFileSupport.Unplug(root);
        var away = await Sweep().RunAsync(Ct);
        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [root] = new(3, ClipFolderLeftOut.NotThere) }, away.UnavailableFolders);

        // A clip asked for meanwhile: its job fails before anything is written, and the folder isn't made again.
        var runner = new FakeFfmpegRunner();
        var worker = await StartWorkerAsync(runner);
        var juliet = await _clips.AddAsync(Alice, new ClipSpec { Name = "Juliet", NoClip = true, HasFile = false }, Ct);
        Assert.Equal(ClipExtractionService.ClipFolderAwayMessage, (await MakeClipAsync(worker, juliet, BookmarkClipStatus.Failed)).Clip!.Error);
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, runner.Runs);
        var still = await Sweep().RunAsync(Ct);
        Assert.Equal((3, 0, 0), (still.FolderUnavailable, still.Missing, still.Marked));

        // Worse: a folder at its path anyway (as that build's job left it; an empty mount point is the same). It is there,
        // but without the marker file and without any of its clips: not recognised, nothing marked, and a job is refused
        // again, writing nothing there.
        Directory.CreateDirectory(Path.Combine(root, "alice"));
        var stray = await Sweep().RunAsync(Ct);
        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [root] = new(3, ClipFolderLeftOut.NoMarker) }, stray.UnavailableFolders);
        Assert.Equal((0, 0, 0), (stray.Missing, stray.Marked, stray.MarkersWritten));
        Assert.Equal(ClipExtractionService.ClipFolderUnrecognisedMessage, (await MakeClipAsync(worker, juliet, BookmarkClipStatus.Failed)).Clip!.Error);
        Assert.Equal([Path.Combine(root, "alice")], Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories));

        // 31 days on: nothing forgotten, nothing archived.
        _clock.Advance(TimeSpan.FromDays(31));
        var later = await Sweep().RunAsync(Ct);
        Assert.Equal((3, 0, 0, 0), (later.FolderUnavailable, later.Marked, later.ClipsForgotten, later.Archived));

        // The drive back (the stray folder gone first): every clip there and none marked; the bookmarks keep their clips and
        // the deleted bookmark its record; nothing was archived. The clip asked for is made now.
        Directory.Delete(root, recursive: true);
        ClipFileSupport.PlugIn(root);
        var back = await Sweep().RunAsync(Ct);
        Assert.Equal((3, 3, 0, 0), (back.Checked, back.Present, back.Marked, back.FolderUnavailable));
        foreach (var record in new[] { golf, hotel, india })
        {
            Assert.Same(record, _clips.Store.Get(record.Id));
        }

        Assert.False(File.Exists(ArchiveFile));
        Assert.True(File.Exists((await MakeClipAsync(worker, juliet, BookmarkClipStatus.Completed)).Clip!.FilePath));
        Assert.Empty(_files.Deletes);
    }

    [Fact]
    public async Task AnEmptyFolderAtAClipFoldersPath_AMountPointWithoutItsDisk_IsNotRecognised_NothingInItMarkedOrArchived()
    {
        // A folder that is there but isn't the clips' (an empty mount point, a share root whose disk
        // isn't mounted, another drive under the same letter). Even clips long due stay as they are.
        var usb = await AddClipFolderAsync("usb");
        var onUsb = Path.Combine(usb, "alice");
        var there = await _clips.AddAsync(Alice, new ClipSpec { Name = "on usb", Folder = onUsb }, Ct);
        var keptLongAgo = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept on usb", Folder = onUsb, OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-100) }, Ct);
        var liveLongAgo = await _clips.AddAsync(Alice, new ClipSpec { Name = "live on usb", Folder = onUsb, OnDisk = false, MissingSince = Now.AddDays(-100) }, Ct);
        ClipFileSupport.Unplug(usb);
        Directory.CreateDirectory(usb);

        var sweep = Sweep();
        var result = await sweep.RunAsync(Ct);

        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [usb] = new(3, ClipFolderLeftOut.NoMarker) }, result.UnavailableFolders);
        Assert.Equal((0, 0, 0, 0, 0), (result.Missing, result.Marked, result.Cleared, result.Archived, result.ClipsForgotten));
        foreach (var record in new[] { there, keptLongAgo, liveLongAgo })
        {
            Assert.Same(record, _clips.Store.Get(record.Id));
        }

        Assert.Empty(_files.MarkerWrites);
        Assert.Empty(Directory.EnumerateFileSystemEntries(usb));
        Assert.False(File.Exists(ArchiveFile));
        var folders = (IEnumerable<IDictionary<string, object?>>)((IDictionary<string, object?>)sweep.Describe()["LastRun"]!)["UnavailableFolders"]!;
        Assert.Equal((usb, 3, "NoMarker"), folders.Select(f => ((string)f["Folder"]!, (int)f["Clips"]!, (string)f["Reason"]!)).Single());
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.StartsWith("1 clip folders were left out (0 not there, 1 there but not recognised by their marker file", StringComparison.Ordinal));

        // The disk mounted again: the folder is recognised by its marker, and judged as any other.
        Directory.Delete(usb);
        ClipFileSupport.PlugIn(usb);
        var back = await Sweep().RunAsync(Ct);
        Assert.Empty(back.UnavailableFolders);
        Assert.Equal((1, 2, 1, 1), (back.Present, back.Missing, back.Archived, back.ClipsForgotten));
    }

    [Fact]
    public async Task AFolderWithItsMarker_WhoseFilesAreReallyGone_IsJudgedAsBefore()
    {
        // The marker is the folder's proof, so files deleted from it by hand are marked, and the retention takes them.
        var present = await _clips.AddAsync(Alice, new ClipSpec { Name = "present" }, Ct);
        var gone = await _clips.AddAsync(Alice, new ClipSpec { Name = "gone" }, Ct);
        var keptGone = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept and gone", Tombstone = true }, Ct);
        File.Delete(gone.Clip!.FilePath!);
        File.Delete(keptGone.Clip!.FilePath!);
        Assert.True(ClipFileSupport.IsMarked(_clips.Root));

        var marked = await Sweep().RunAsync(Ct);
        _clock.Advance(TimeSpan.FromDays(30));
        var due = await Sweep().RunAsync(Ct);

        Assert.Equal((1, 2, 2, 0), (marked.Present, marked.Missing, marked.Marked, marked.MarkersWritten));
        Assert.Empty(marked.UnavailableFolders);
        Assert.Equal((1, 1), (due.ClipsForgotten, due.Archived));
        Assert.Null(_clips.Store.Get(gone.Id)!.Clip);
        Assert.Null(_clips.Store.Get(keptGone.Id));
        Assert.Same(present, _clips.Store.Get(present.Id));
    }

    [Fact]
    public async Task AFolderWithoutItsMarker_GetsItWhenEveryOneOfItsClipsIsThere_AndIsJudgedFromThen_ButNotBefore()
    {
        // Bookmarks 1.1 wrote no marker: its clips there, none gone, prove the folder, and the run writes the marker (the
        // backfill) with a token it records, and judges the folder. Since 1.2.0 a folder whose clips aren't
        // all there isn't proven by those that are (Unproven); one without any of them there stays unrecognised (NoMarker).
        var old = await AddClipFolderAsync("old", marked: false);
        var there = await _clips.AddAsync(Alice, new ClipSpec { Name = "there", Folder = Path.Combine(old, "alice") }, Ct);
        var also = await _clips.AddAsync(Alice, new ClipSpec { Name = "also there", Folder = Path.Combine(old, "alice") }, Ct);
        var partly = await AddClipFolderAsync("partly", marked: false);
        var kept = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept", Folder = Path.Combine(partly, "alice") }, Ct);
        var lost = await _clips.AddAsync(Alice, new ClipSpec { Name = "lost", Folder = Path.Combine(partly, "alice"), OnDisk = false }, Ct);
        var empty = await AddClipFolderAsync("empty", marked: false);
        var unseen = await _clips.AddAsync(Alice, new ClipSpec { Name = "unseen", Folder = Path.Combine(empty, "alice"), OnDisk = false }, Ct);

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal((1, 0, 0), (result.MarkersWritten, result.Missing, result.Marked));
        Assert.Equal(
            new Dictionary<string, ClipFolderLeftOut> { [partly] = new(2, ClipFolderLeftOut.Unproven), [empty] = new(1, ClipFolderLeftOut.NoMarker) },
            result.UnavailableFolders);
        Assert.True(ClipFileSupport.IsMarked(old));
        Assert.Equal(_clips.Store.GetClipRootToken(old), ClipFileSupport.TokenOf(old));
        Assert.False(ClipFileSupport.IsMarked(partly));
        Assert.False(ClipFileSupport.IsMarked(empty));
        Assert.Equal([old], _files.MarkerWrites);
        foreach (var record in new[] { there, also, kept, lost, unseen })
        {
            Assert.Same(record, _clips.Store.Get(record.Id));
        }

        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.EndsWith("1 clip folders given their marker file", StringComparison.Ordinal));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.StartsWith("2 clip folders were left out (0 not there, 1 there but not recognised by their marker file, 1 without their marker file and with some of their clips gone,", StringComparison.Ordinal));

        // From then on the marker proves the folder, also once its clips are deleted one by one.
        File.Delete(there.Clip!.FilePath!);
        var next = await Sweep().RunAsync(Ct);
        Assert.Equal((0, 1), (next.MarkersWritten, next.Marked));
        Assert.False(next.UnavailableFolders.ContainsKey(old));
        Assert.Equal(Now, MissingSince(there));

        // The unproven folder's lost clip back: every clip there, proven, given its marker.
        await WriteFileAsync(lost.Clip!.FilePath!, [1]);
        var proven = await Sweep().RunAsync(Ct);
        Assert.False(proven.UnavailableFolders.ContainsKey(partly));
        Assert.True(ClipFileSupport.IsMarked(partly));
    }

    [Fact]
    public async Task AFolderWhoseMarkerCantBeWritten_IsLeftOut_UntilItCanBe()
    {
        var old = await AddClipFolderAsync("old", marked: false);
        await _clips.AddAsync(Alice, new ClipSpec { Name = "there", Folder = Path.Combine(old, "alice") }, Ct);
        var later = await _clips.AddAsync(Alice, new ClipSpec { Name = "deleted later", Folder = Path.Combine(old, "alice") }, Ct);
        _files.MarkerWriteFailure = _ => new UnauthorizedAccessException("Access to the path is denied.");

        var refused = await Sweep().RunAsync(Ct);

        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [old] = new(2, ClipFolderLeftOut.MarkerNotWritten) }, refused.UnavailableFolders);
        Assert.Equal((0, 0, 0), (refused.Missing, refused.Marked, refused.MarkersWritten));
        Assert.Null(_clips.Store.GetClipRootToken(old));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.StartsWith("Could not write the marker file (.jfbm-clip-root) of a clip folder", StringComparison.Ordinal) && !e.Message.Contains(old, StringComparison.OrdinalIgnoreCase));

        _files.MarkerWriteFailure = null;
        var next = await Sweep().RunAsync(Ct);
        Assert.Equal((1, 0), (next.MarkersWritten, next.Marked));
        Assert.Empty(next.UnavailableFolders);
        File.Delete(later.Clip!.FilePath!);
        Assert.Equal(1, (await Sweep().RunAsync(Ct)).Marked);
    }

    [Fact]
    public async Task AStrayFolderHoldingAClipOlderVersionsWroteThere_IsUnproven_NothingMarkedForgottenOrArchived()
    {
        // A repro found in testing: Golf, Hotel, India (a deleted bookmark's kept clip) and Juliet made in the clip folder;
        // the real folder renamed aside; a folder in its place holding only Juliet's file, as Bookmarks 1.1.x's clip worker
        // left one while the real folder was away (ReserveOutput made the folder again). The backfill took Juliet's file for
        // proof: the first run wrote the marker and marked the other three (MarkersWritten 1, Marked 3), and 31 days on two
        // clip entries were forgotten and India archived, all three files intact in the renamed folder.
        var root = await AddClipFolderAsync("mnt-clips", marked: false);
        var onIt = Path.Combine(root, "alice");
        var golf = await _clips.AddAsync(Alice, new ClipSpec { Name = "Golf", Folder = onIt }, Ct);
        var hotel = await _clips.AddAsync(Alice, new ClipSpec { Name = "Hotel", Folder = onIt }, Ct);
        var india = await _clips.AddAsync(Alice, new ClipSpec { Name = "India", Folder = onIt, Tombstone = true }, Ct);
        var juliet = await _clips.AddAsync(Alice, new ClipSpec { Name = "Juliet", Folder = onIt }, Ct);
        ClipFileSupport.Unplug(root);
        Directory.CreateDirectory(onIt);
        File.Copy(Path.Combine(root + "-unplugged", "alice", Path.GetFileName(juliet.Clip!.FilePath!)), juliet.Clip!.FilePath!);

        var first = await Sweep().RunAsync(Ct);

        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [root] = new(4, ClipFolderLeftOut.Unproven) }, first.UnavailableFolders);
        Assert.Equal((0, 0, 0), (first.MarkersWritten, first.Marked, first.Missing));
        Assert.Empty(_files.MarkerWrites);
        Assert.False(File.Exists(ClipRootMarker.PathIn(root)));

        // 31 days on: nothing forgotten, nothing archived; every record as it was, every file where it was.
        _clock.Advance(TimeSpan.FromDays(31));
        var later = await Sweep().RunAsync(Ct);
        Assert.Equal((0, 0, 0, 4), (later.ClipsForgotten, later.Archived, later.Marked, later.FolderUnavailable));
        foreach (var record in new[] { golf, hotel, india, juliet })
        {
            Assert.Same(record, _clips.Store.Get(record.Id));
            Assert.True(File.Exists(Path.Combine(root + "-unplugged", "alice", Path.GetFileName(record.Clip!.FilePath!))));
        }

        Assert.False(File.Exists(ArchiveFile));
        Assert.Empty(_files.Deletes);

        // The stray folder gone and the real one back: its clips are all there, so it is proven and given its marker.
        Directory.Delete(root, recursive: true);
        ClipFileSupport.PlugIn(root);
        var back = await Sweep().RunAsync(Ct);
        Assert.Empty(back.UnavailableFolders);
        Assert.Equal((4, 1, 0), (back.Present, back.MarkersWritten, back.Marked));
    }

    [Fact]
    public async Task AnAdminsConfirmation_OfAFolderEmptiedByHand_MarksItsClipsMissing_AndTheRetentionTakesThemAsAny()
    {
        // Found in testing: a folder of Bookmarks 1.1 whose clips were all deleted by hand has neither its marker nor any
        // of its clips (NoMarker): left out at every run, and every new clip into it refused, with no remedy for an admin. The
        // admin's "This is the clip folder; its clips are gone" gives it its marker and marks them; then the retention runs.
        var old = await AddClipFolderAsync("old", marked: false);
        var bravo = await _clips.AddAsync(Alice, new ClipSpec { Name = "Bravo", Folder = Path.Combine(old, "alice"), OnDisk = false }, Ct);
        var charlie = await _clips.AddAsync(Alice, new ClipSpec { Name = "Charlie", Folder = Path.Combine(old, "alice"), OnDisk = false, Tombstone = true }, Ct);
        var sweep = Sweep();
        var left = await sweep.RunAsync(Ct);
        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [old] = new(2, ClipFolderLeftOut.NoMarker) }, left.UnavailableFolders);
        var rootId = ClipRootPaths.RootId(old);

        // A dry run counts, and changes nothing.
        var dry = await sweep.ConfirmFolderAsync(rootId, dryRun: true, Alice, Ct);
        Assert.Equal((true, true, false, 2, 0, 2, 0, 2), ((bool)dry["DryRun"]!, (bool)dry["Existed"]!, (bool)dry["Created"]!, (int)dry["Clips"]!, (int)dry["Present"]!, (int)dry["Gone"]!, (int)dry["MarkedBefore"]!, (int)dry["Marked"]!));
        Assert.Null(dry["Marker"]);
        Assert.Empty(_files.MarkerWrites);
        Assert.Null(MissingSince(bravo));

        var confirmed = await sweep.ConfirmFolderAsync(rootId.ToUpperInvariant(), dryRun: false, Alice, Ct);

        Assert.Equal((false, "Written", 2, 0), ((bool)confirmed["DryRun"]!, (string)confirmed["Marker"]!, (int)confirmed["Marked"]!, (int)confirmed["MarkedBefore"]!));
        Assert.Equal((old, rootId), ((string)confirmed["Folder"]!, (string)confirmed["RootId"]!));
        Assert.Equal(Now, MissingSince(bravo));
        Assert.Equal(Now, MissingSince(charlie));
        Assert.Equal(_clips.Store.GetClipRootToken(old), ClipFileSupport.TokenOf(old));
        Assert.Empty(sweep.LastRun!.UnavailableFolders);
        Assert.Equal(0, sweep.LastRun.FolderUnavailable);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.StartsWith("An admin (" + Alice.ToString("D"), StringComparison.Ordinal) && e.Message.Contains(rootId, StringComparison.Ordinal) && !e.Message.Contains(old, StringComparison.OrdinalIgnoreCase));

        // The next run judges the folder (its clips marked already: no AllMissing), and 30 days on the retention acts.
        var judged = await Sweep().RunAsync(Ct);
        Assert.Empty(judged.UnavailableFolders);
        Assert.Equal((2, 0), (judged.Missing, judged.Marked));
        _clock.Advance(TimeSpan.FromDays(30));
        var due = await Sweep().RunAsync(Ct);
        Assert.Equal((1, 1), (due.ClipsForgotten, due.Archived));
        Assert.Null(_clips.Store.Get(bravo.Id)!.Clip);
        Assert.Null(_clips.Store.Get(charlie.Id));
        Assert.Empty(_files.Deletes);

        // An id no recorded folder has is 404, and nothing else is touched.
        var unknown = await Assert.ThrowsAsync<Jellyfin.Plugin.Kit.Api.PluginException>(() => sweep.ConfirmFolderAsync("0123456789ab", dryRun: false, Alice, Ct));
        Assert.Equal(Jellyfin.Plugin.Kit.Api.PluginErrorKind.NotFound, unknown.Kind);
    }

    [Fact]
    public async Task AnAdminsConfirmation_OfAFolderDeletedAsAWhole_MakesItAgain_AndOfAnEmptiedMarkedOne_MarksItsClips()
    {
        // Found in testing: a clip folder (not the default one) deleted with its clips is NotThere forever; a marked one
        // emptied at once is AllMissing. The admin confirms each.
        var gone = await AddClipFolderAsync("gone");
        var a = await _clips.AddAsync(Alice, new ClipSpec { Name = "a", Folder = Path.Combine(gone, "alice") }, Ct);
        var emptied = await AddClipFolderAsync("emptied");
        var b = await _clips.AddAsync(Alice, new ClipSpec { Name = "b", Folder = Path.Combine(emptied, "alice") }, Ct);
        var c = await _clips.AddAsync(Alice, new ClipSpec { Name = "c", Folder = Path.Combine(emptied, "alice") }, Ct);
        Directory.Delete(gone, recursive: true);
        Directory.Delete(Path.Combine(emptied, "alice"), recursive: true);
        var sweep = Sweep();
        var left = await sweep.RunAsync(Ct);
        Assert.Equal(
            new Dictionary<string, ClipFolderLeftOut> { [gone] = new(1, ClipFolderLeftOut.NotThere), [emptied] = new(2, ClipFolderLeftOut.AllMissing) },
            left.UnavailableFolders);

        var made = await sweep.ConfirmFolderAsync(ClipRootPaths.RootId(gone), dryRun: false, Alice, Ct);
        Assert.Equal((false, true, 1), ((bool)made["Existed"]!, (bool)made["Created"]!, (int)made["Marked"]!));
        Assert.True(Directory.Exists(gone));
        Assert.True(ClipFileSupport.IsMarked(gone));
        Assert.Equal(Now, MissingSince(a));

        var marked = await sweep.ConfirmFolderAsync(ClipRootPaths.RootId(emptied), dryRun: false, Alice, Ct);
        Assert.Equal(("AlreadyMarked", 2), ((string)marked["Marker"]!, (int)marked["Marked"]!));
        Assert.Equal(Now, MissingSince(b));
        Assert.Equal(Now, MissingSince(c));
        Assert.Empty(sweep.LastRun!.UnavailableFolders);
        Assert.Empty((await Sweep().RunAsync(Ct)).UnavailableFolders);

        // A folder that can't be made (its drive isn't there): 503, nothing marked.
        var nowhere = Path.Combine(_clips.Directory.Combine("a-file"), "clips");
        await File.WriteAllTextAsync(_clips.Directory.Combine("a-file"), "a file where the folder's parent should be", Ct);
        await _clips.Store.AddClipRootAsync(nowhere, Ct);
        var lost = await _clips.AddAsync(Alice, new ClipSpec { Name = "lost", HasFile = true, OnDisk = false, FileName = "lost.mkv", Folder = nowhere }, Ct);
        var refused = await Assert.ThrowsAsync<Jellyfin.Plugin.Kit.Api.PluginException>(() => sweep.ConfirmFolderAsync(ClipRootPaths.RootId(nowhere), dryRun: false, Alice, Ct));
        Assert.Equal((Jellyfin.Plugin.Kit.Api.PluginErrorKind.Unavailable, ClipFolderConfirmation.NotMadeMessage), (refused.Kind, refused.Message));
        Assert.Null(MissingSince(lost));
    }

    [Fact]
    public async Task TheDefaultClipFolderDeleted_IsMadeAgainByTheRun_ItsClipsMarkedMissing_AndTheRetentionTakesThem()
    {
        // Found in testing: data/bookmarks/clips deleted as a whole was NotThere at every run, as an unplugged drive is.
        // Inside the plugin's data folder, which is there, it is deleted: the run makes it again with its marker and marks its
        // clips, and the retention takes them 30 days on. Another folder away stays NotThere.
        var usb = await AddClipFolderAsync("usb");
        var onUsb = await _clips.AddAsync(Alice, new ClipSpec { Name = "on usb", Folder = Path.Combine(usb, "alice") }, Ct);
        var live = await _clips.AddAsync(Alice, new ClipSpec { Name = "live" }, Ct);
        var kept = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept", Tombstone = true }, Ct);
        Directory.Delete(_clips.Root, recursive: true);
        ClipFileSupport.Unplug(usb);
        var paths = BookmarkPaths.ForRoot(Path.GetDirectoryName(_clips.Root)!);

        var result = await Sweep(paths: paths).RunAsync(Ct);

        Assert.Equal((1, 2, 2, 1), (result.FoldersMadeAgain, result.Marked, result.Missing, result.MarkersWritten));
        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [usb] = new(1, ClipFolderLeftOut.NotThere) }, result.UnavailableFolders);
        Assert.True(ClipFileSupport.IsMarked(_clips.Root));
        Assert.Equal(_clips.Store.GetClipRootToken(_clips.Root), ClipFileSupport.TokenOf(_clips.Root));
        Assert.Equal(Now, MissingSince(live));
        Assert.Equal(Now, MissingSince(kept));
        Assert.Same(onUsb, _clips.Store.Get(onUsb.Id));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.StartsWith("The default clip folder was gone (deleted with its 2 clips): made again with its marker file, 2 clips marked missing", StringComparison.Ordinal));

        _clock.Advance(TimeSpan.FromDays(30));
        var due = await Sweep(paths: paths).RunAsync(Ct);
        Assert.Equal((0, 1, 1), (due.FoldersMadeAgain, due.ClipsForgotten, due.Archived));

        // Without the plugin's data folder there (a data folder on a drive away), the default folder isn't taken for deleted.
        Assert.Null(ClipFileSupport.Ops(_clips.Store, paths: BookmarkPaths.ForRoot(_clips.Directory.Combine("no-data"))).DeletedDefaultFolder());
    }

    [Fact]
    public async Task AnOlderCopyOfTheFolder_BackUnderItsPath_IsntTheFolder_ItsNewerClipsNeverMarked()
    {
        // The marker's RootId was only the path's hash: an older copy of the folder (or an older clip drive under the same
        // letter) back in its place counted as the folder, and its newer clips, missing there, as gone. The marker carries a
        // token now, recorded in the store; a copy whose marker names an older token, or none (taken before the token), is
        // another folder: with some of its clips there and some not, Unproven. (An exact copy, taken after the marker's last
        // writing, can't be told from the folder: scripts/README.md and the plugin's README say so.)
        var drive = await AddClipFolderAsync("drive", marked: false);
        ClipFileSupport.MarkClipFolderWithoutToken(drive);
        var onIt = Path.Combine(drive, "alice");
        var older = await _clips.AddAsync(Alice, new ClipSpec { Name = "older", Folder = onIt }, Ct);
        var copy = _clips.Directory.Combine("drive-copy");
        CopyFolder(drive, copy);

        // The marker without a token adopted once: given a token, recorded; then a newer clip.
        var adopted = await Sweep().RunAsync(Ct);
        Assert.Equal((1, 0), (adopted.MarkersWritten, adopted.UnavailableFolders.Count));
        Assert.NotNull(_clips.Store.GetClipRootToken(drive));
        var newer = await _clips.AddAsync(Alice, new ClipSpec { Name = "newer", Folder = onIt }, Ct);

        // The older copy back in the folder's place: its marker has no token, while one is recorded.
        ClipFileSupport.Unplug(drive);
        Directory.Move(copy, drive);
        var result = await Sweep().RunAsync(Ct);

        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [drive] = new(2, ClipFolderLeftOut.Unproven) }, result.UnavailableFolders);
        Assert.Equal((0, 0), (result.Marked, result.MarkersWritten));
        Assert.Null(MissingSince(newer));
        Assert.Same(older, _clips.Store.Get(older.Id));

        // One with another token (an older drive marked on its own) is the same: never Marked.
        PhysicalClipFileSystem.Instance.WriteRootMarker(drive, ClipRootMarker.Text(drive, ClipRootMarker.NewToken()));
        Assert.Equal(ClipFolderLeftOut.Unproven, (await Sweep().RunAsync(Ct)).UnavailableFolders[drive].Reason);

        // The folder back: its marker names the recorded token, and it is judged.
        Directory.Delete(drive, recursive: true);
        ClipFileSupport.PlugIn(drive);
        var back = await Sweep().RunAsync(Ct);
        Assert.Empty(back.UnavailableFolders);
        Assert.Equal((2, 0), (back.Present, back.MarkersWritten));

        static void CopyFolder(string from, string to)
        {
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }

    [Fact]
    public async Task AFolderWhoseEveryClipIsGoneAtOnce_IsLeftOut_FromTwoClipsThatWereThere()
    {
        // The sweep's second guard: in a folder with its marker, every clip missing at once, at least two of them not marked
        // before (there at the last look, or never looked at), is a folder emptied or put back from an old copy, not files
        // deleted one by one: nothing in it is marked. One clip alone is still marked, and so is a folder where only one of
        // the missing clips was there before.
        var emptied = await AddClipFolderAsync("emptied");
        var a = await _clips.AddAsync(Alice, new ClipSpec { Name = "a", Folder = Path.Combine(emptied, "alice") }, Ct);
        var b = await _clips.AddAsync(Alice, new ClipSpec { Name = "b", Folder = Path.Combine(emptied, "alice"), Tombstone = true }, Ct);
        var single = await AddClipFolderAsync("single");
        var alone = await _clips.AddAsync(Alice, new ClipSpec { Name = "alone", Folder = Path.Combine(single, "alice"), OnDisk = false }, Ct);
        var earlier = await AddClipFolderAsync("earlier");
        await _clips.AddAsync(Alice, new ClipSpec { Name = "marked before", Folder = Path.Combine(earlier, "alice"), OnDisk = false, MissingSince = Now.AddDays(-3) }, Ct);
        var newly = await _clips.AddAsync(Alice, new ClipSpec { Name = "newly", Folder = Path.Combine(earlier, "alice"), OnDisk = false }, Ct);
        Assert.Equal(2, MissingClipSweep.AllMissingThreshold);
        Directory.Delete(Path.Combine(emptied, "alice"), recursive: true);

        var result = await Sweep().RunAsync(Ct);

        Assert.Equal(new Dictionary<string, ClipFolderLeftOut> { [emptied] = new(2, ClipFolderLeftOut.AllMissing) }, result.UnavailableFolders);
        Assert.Same(a, _clips.Store.Get(a.Id));
        Assert.Same(b, _clips.Store.Get(b.Id));
        Assert.Equal(Now, MissingSince(alone));
        Assert.Equal(Now, MissingSince(newly));
        Assert.Equal((3, 2), (result.Missing, result.Marked));

        // Once one of its clips is there again, the folder is judged: the other one is marked.
        await WriteFileAsync(a.Clip!.FilePath!, [1]);
        var back = await Sweep().RunAsync(Ct);
        Assert.Empty(back.UnavailableFolders);
        Assert.Null(MissingSince(a));
        Assert.Equal(Now, MissingSince(b));
    }

    [Fact]
    public async Task TheRetention_ActsOnlyWhileTheClipsFolderStillHasItsMarker_LookedAtUnderTheStoresLock()
    {
        // Both were due when the run looked; the folder loses its marker before the retention decides (a folder swapped for
        // another between the look and the write): neither leaves.
        var store = new InterferingBookmarkStore(_clips.Store);
        await _clips.AddAsync(Alice, new ClipSpec { Name = "there" }, Ct);
        var kept = await _clips.AddAsync(Alice, new ClipSpec { Name = "kept", OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-40) }, Ct);
        var live = await _clips.AddAsync(Alice, new ClipSpec { Name = "live", OnDisk = false, MissingSince = Now.AddDays(-40) }, Ct);
        store.BeforeNextDocumentWrite = () =>
        {
            ClipFileSupport.UnmarkClipFolder(_clips.Root);
            return Task.CompletedTask;
        };

        var result = await Sweep(store).RunAsync(Ct);

        Assert.Equal((2, 0, 0, 2), (result.Missing, result.Archived, result.ClipsForgotten, result.Changed));
        Assert.Same(kept, _clips.Store.Get(kept.Id));
        Assert.Same(live, _clips.Store.Get(live.Id));
        Assert.False(File.Exists(ArchiveFile));
    }

    [Fact]
    public async Task AStoreWriteThatFailsAfterTheArchiveLines_KeepsTheDeletedBookmark_ItsArchivedCopySuperseded_AndTheNextRunArchivesItOnce()
    {
        // Found in testing: the kit appends and flushes the archive lines inside the store's write, which then fails (here the
        // real store can't replace bookmarks.json, held open by another program). The record stays in the store, its archived
        // copy is superseded at once (so it never counts twice), the run reports the error, and the next run archives it once.
        var kept = await _clips.AddAsync(Alice, new ClipSpec { Name = "Bank job", Note = "the kept clip's note", OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-40) }, Ct);
        await _clips.AddAsync(Alice, new ClipSpec { Name = "there" }, Ct);
        var sweep = Sweep();
        Jellyfin.Plugin.Kit.Api.PluginException error;
        using (new FileStream(_clips.Directory.Combine("data", "bookmarks.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            error = await Assert.ThrowsAsync<Jellyfin.Plugin.Kit.Api.PluginException>(() => sweep.RunAsync(Ct));
        }

        Assert.Equal(Jellyfin.Plugin.Kit.Api.PluginErrorKind.Unavailable, error.Kind);
        Assert.Same(kept, _clips.Store.Get(kept.Id));
        Assert.Empty(_detached.ArchiveOf(_owner).Read().Records);
        Assert.Equal(["Archived", "Superseded"], ArchiveOps());
        Assert.Equal(error.Message, sweep.LastRun!.Error);

        var next = await Sweep().RunAsync(Ct);

        Assert.Equal(1, next.Archived);
        Assert.Null(_clips.Store.Get(kept.Id));
        var archived = Assert.Single(_detached.ArchiveOf(_owner).Read().Records);
        Assert.Equal((kept.Id.ToString("D"), MissingClipSweep.ArchiveReason, "the kept clip's note"), (archived.Id, archived.Reason, archived.Record["Note"]!.GetValue<string>()));
        Assert.Equal(["Archived", "Superseded", "Archived"], ArchiveOps());
        Assert.Equal(kept.Id.ToString("D"), Assert.Single(_detached.ExportArchive(_owner))["Id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(int.MaxValue)]
    public async Task ARetentionAboveItsMaximum_CountsAsTheMaximum_AndNeverThrows(int days)
    {
        // Found in testing: above about 739,000 days the cutoff fell before the year 1, and the retention threw at every run.
        _configuration.MissingClipRetentionDays = days;
        var ancient = await _clips.AddAsync(Alice, new ClipSpec { Name = "ancient", OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(-MissingClipSweep.MaxRetentionDays) }, Ct);
        var old = await _clips.AddAsync(Alice, new ClipSpec { Name = "old", OnDisk = false, Tombstone = true, MissingSince = Now.AddDays(1 - MissingClipSweep.MaxRetentionDays) }, Ct);
        var sweep = Sweep();

        var result = await sweep.RunAsync(Ct);

        Assert.Equal((36_500, 2, 1), (result.RetentionDays, result.Missing, result.Archived));
        Assert.Null(result.Error);
        Assert.Null(_clips.Store.Get(ancient.Id));
        Assert.Same(old, _clips.Store.Get(old.Id));
        Assert.Equal(36_500, sweep.Describe()["RetentionDays"]);
    }

    [Fact]
    public void TheResult_DescribesItselfWithPlainValues()
    {
        var result = new MissingClipSweepResult
        {
            StartedUtc = Now,
            Checked = 3,
            UnavailableFolders = new Dictionary<string, ClipFolderLeftOut>
            {
                [@"E:\Clips"] = new(2, ClipFolderLeftOut.NoMarker),
                [@"D:\Clips"] = new(1, ClipFolderLeftOut.NotThere),
                [@"F:\Clips"] = new(1, ClipFolderLeftOut.MarkerNotWritten),
                [@"G:\Clips"] = new(4, ClipFolderLeftOut.Unproven)
            },
            MarkersWritten = 1,
            FoldersMadeAgain = 1
        };

        var described = result.ToDictionary();

        Assert.Equal(
            ["StartedUtc", "Milliseconds", "Skipped", "RetentionDays", "Checked", "Present", "Missing", "Marked", "Cleared", "InProgress", "FolderUnavailable", "UnavailableFolders", "MarkersWritten", "FoldersMadeAgain", "Unreadable", "Refused", "Changed", "Archived", "ClipsForgotten", "Error"],
            described.Keys);

        // Each folder with its RootId, and whether an admin may confirm it (not while its marker
        // can't be written).
        Assert.Equal(
            [
                (@"D:\Clips", 1, "NotThere", ClipRootPaths.RootId(@"D:\Clips"), true), (@"E:\Clips", 2, "NoMarker", ClipRootPaths.RootId(@"E:\Clips"), true),
                (@"F:\Clips", 1, "MarkerNotWritten", ClipRootPaths.RootId(@"F:\Clips"), false), (@"G:\Clips", 4, "Unproven", ClipRootPaths.RootId(@"G:\Clips"), true)
            ],
            ((IEnumerable<IDictionary<string, object?>>)described["UnavailableFolders"]!).Select(f => ((string)f["Folder"]!, (int)f["Clips"]!, (string)f["Reason"]!, (string)f["RootId"]!, (bool)f["Confirmable"]!)));
        Assert.Equal((1, 1), ((int)described["MarkersWritten"]!, (int)described["FoldersMadeAgain"]!));
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> found)
        where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (found() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The condition didn't hold within 10 s.");
            }

            await Task.Delay(20, Ct);
        }
    }

    private static async Task WriteFileAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, Ct);
    }

    private DateTime? MissingSince(Bookmark record) => _clips.Store.Get(record.Id)?.Clip?.MissingSinceUtc;

    // The real clip worker over the fixture's store, its clip folder the fixture's (FakeClipPlanner's clips), with a fake
    // ffmpeg; started, and stopped when the test ends.
    private async Task<ClipExtractionService> StartWorkerAsync(FakeFfmpegRunner runner)
    {
        var files = ClipFileSupport.Ops(_clips.Store, _logger, _files);
        var worker = new ClipExtractionService(
            _clips.Store,
            new BackgroundWriteGate(_clips.Store, NullLogger<BackgroundWriteGate>.Instance),
            new FakeClipPlanner(_clips.Directory) { NamedFiles = true },
            runner,
            files,
            ClipFileSupport.Janitor(_clips.Store, files),
            ClipFileSupport.Guard(_clips.Store),
            TestData.ConfigurationProvider(_configuration),
            _host.Object,
            NullLogger<ClipExtractionService>.Instance);
        _disposables.Add(new StopsWorker(worker));
        await worker.StartAsync(Ct);
        return worker;
    }

    // A clip asked for through the worker, once its job has ended as given.
    private async Task<Bookmark> MakeClipAsync(ClipExtractionService worker, Bookmark bookmark, BookmarkClipStatus ending)
    {
        var before = _clips.Store.Get(bookmark.Id)?.Clip?.RequestedAt;
        await worker.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        return await WaitForAsync(() => _clips.Store.Get(bookmark.Id) is { Clip: { } clip } record && clip.RequestedAt != before && clip.Status == ending ? record : null);
    }

    // The operations of the archive's lines, in order.
    private string[] ArchiveOps()
    {
        return File.ReadAllLines(ArchiveFile).Where(line => line.Length > 0).Select(Op).ToArray();

        static string Op(string line)
        {
            using var json = System.Text.Json.JsonDocument.Parse(line);
            return json.RootElement.GetProperty("Op").GetString()!;
        }
    }

    // A second recorded clip folder as the clip worker leaves one: with its marker file (or without, as Bookmarks 1.1 and
    // an early 1.2.0 build left theirs).
    private async Task<string> AddClipFolderAsync(string name, bool marked = true)
    {
        var folder = _clips.Directory.Combine(name);
        Directory.CreateDirectory(folder);
        await _clips.Store.AddClipRootAsync(folder, Ct);
        return marked ? ClipFileSupport.MarkClipFolder(folder) : ClipRootPaths.TryNormalize(folder, out var normalized) ? normalized : folder;
    }

    // The sweep as the registrator builds it, over the fixture's store (or another), the fake file system that records every
    // delete and move, the kit's detached records and the bookmarks' real record owner (not started: only its archive is
    // used), on the run's clock.
    private MissingClipSweep Sweep(IBookmarkStore? store = null, IClipFileSystem? fileSystem = null, BookmarkPaths? paths = null)
    {
        store ??= _clips.Store;
        var journal = new MoveJournalReader(_clips.Directory.Combine("moves.jsonl"), _logger);
        BookmarkIdentityService? owner = null;
        _detached = new DetachedRecords(
            _clips.Directory.Combine("data", "archive"),
            _clips.Directory.Combine("data", "snapshots"),
            _library.Object,
            id => _users.Object.GetUserById(id),
            new IdentityResolver(_library.Object, journal, _clock),
            journal,
            () => [owner!],
            _logger,
            _clock);
        var detached = _detached;
        owner = new BookmarkIdentityService(
            _host.Object,
            store,
            new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
            _library.Object,
            journal,
            () => throw new InvalidOperationException("No verify sweep in these tests."),
            () => detached,
            TestData.ConfigurationProvider(_configuration),
            _clips.Directory.Combine("data", "snapshots"),
            _logger,
            _clock);
        _owner = owner;
        _disposables.Add(owner);
        return new MissingClipSweep(
            store,
            new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
            ClipFileSupport.Ops(store, _logger, fileSystem ?? _files, paths: paths, clock: _clock),
            TestData.ConfigurationProvider(_configuration),
            () => detached,
            () => owner,
            _logger,
            _clock);
    }

    /// <summary>Stops a worker when the test ends.</summary>
    private sealed class StopsWorker(ClipExtractionService worker) : IDisposable
    {
        public void Dispose()
        {
            worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            worker.Dispose();
        }
    }

    /// <summary>
    /// The real file system, but a clip folder unplugged after the first looks: it is there for that many looks, then gone,
    /// and every path under it reads as nothing there (an unplugged drive's DirectoryNotFoundException).
    /// </summary>
    private sealed class UnpluggedAfter(string folder, int folderLooks) : IClipFileSystem
    {
        private int _looks;

        public int FolderLooks => Volatile.Read(ref _looks);

        public bool FolderExists(string path)
            => !ClipRootPaths.Comparer.Equals(path, folder)
                ? PhysicalClipFileSystem.Instance.FolderExists(path)
                : Interlocked.Increment(ref _looks) <= folderLooks && PhysicalClipFileSystem.Instance.FolderExists(path);

        public FileAttributes? GetAttributes(string path)
            => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? null : PhysicalClipFileSystem.Instance.GetAttributes(path);

        public void DeleteFile(string path) => throw new InvalidOperationException("The sweep never deletes a file.");

        public void MoveFile(string source, string destination) => throw new InvalidOperationException("The sweep never moves a file.");
        public void ReplaceFile(string source, string destination) => throw new InvalidOperationException("The sweep never moves a file.");

        public IEnumerable<ClipFolderFile> ListClipFolder(string path) => PhysicalClipFileSystem.Instance.ListClipFolder(path);

        public DateTime? GetLastWriteTimeUtc(string path) => PhysicalClipFileSystem.Instance.GetLastWriteTimeUtc(path);

        // The folder's marker goes with it: nothing is there once it is unplugged.
        public string? ReadRootMarker(string path)
            => ClipRootPaths.Comparer.Equals(path, folder) && FolderLooks > folderLooks ? null : PhysicalClipFileSystem.Instance.ReadRootMarker(path);

        public void WriteRootMarker(string path, string text) => throw new InvalidOperationException("No marker is written in these runs.");
    }
}
