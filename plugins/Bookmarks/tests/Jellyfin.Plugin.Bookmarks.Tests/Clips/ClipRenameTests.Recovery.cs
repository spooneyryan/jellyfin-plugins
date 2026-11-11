using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip's rename, its harder cases: an entry that can't be read holds its clip; the record's name is applied only
/// while the record is unchanged; a clip queued just before the store's lock is 409; a folder there but not recognised keeps
/// the rename waiting; the file told apart by its id (NTFS tunnelling), and a file at the new name taken for the clip when its
/// identity changed; the move flushed before the record names it, and a power cut that lost it; an entry removed only when it
/// is the one finished; a change of case judged by the case on disk; an unexpected error leaving the rename to the recovery; a
/// written rename holding nothing; the download's fallback for the renamed record only; leftover writes; Rename file's
/// IfUpdatedAt; "Rename not finished" in the list, the bulk delete, the retry and Diagnostics; an emoji at the 80th character.
/// </summary>
public sealed partial class ClipRenameTests
{
    [Fact]
    public async Task AnEntryThatCantBeRead_HoldsItsClipByTheIdInItsName_IsCounted_AndStays()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var entry = Path.Combine(DataFolder, ClipRenameJournal.FilePrefix + record.Id.ToString("N") + ".json");
        await File.WriteAllTextAsync(entry, "{ not json", Ct);
        var tooLarge = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Too large", Start = 120, End = 150 }, Ct);
        var large = Path.Combine(DataFolder, ClipRenameJournal.FilePrefix + tooLarge.Id.ToString("N") + ".json");
        await File.WriteAllTextAsync(large, new string(' ', ClipRenameJournal.MaxBytes + 1), Ct);
        await File.WriteAllTextAsync(Path.Combine(DataFolder, ClipRenameJournal.FilePrefix + "not-an-id.json"), "{}", Ct);
        var logger = new ListLogger();

        var files = ClipFileSupport.Ops(_fixture.Store, logger: logger, fileSystem: _fileSystem, states: _fixture.States);

        foreach (var held in new[] { record, tooLarge })
        {
            Assert.True(files.HasUnfinishedRename(held.Id));
            Assert.True(files.Check(held.Clip!.FilePath).RenameUnfinished);
            Assert.Equal(ClipFileOutcome.Failed, files.DeleteOnce(held.Clip.FilePath, held.Id, 1).Outcome);
        }

        var recovery = await files.FinishRenamesAsync(null, Ct);
        Assert.Equal(ClipRenameRecovery.None with { Unreadable = 3 }, recovery);
        Assert.True(File.Exists(entry) && File.Exists(large) && File.Exists(record.Clip!.FilePath));
        Assert.Contains(logger.Entries, l => l.Level == LogLevel.Warning && l.Message.Contains("can't be read", StringComparison.Ordinal) && l.Message.Contains(record.Id.ToString(), StringComparison.Ordinal));
        Assert.Contains(logger.Entries, l => l.Level == LogLevel.Warning && l.Message.Contains("too large", StringComparison.Ordinal) && l.Message.Contains(tooLarge.Id.ToString(), StringComparison.Ordinal));

        // The list, the delete, the remake and the rename leave it; Diagnostics counts all three files.
        var rows = ServiceOver(files).GetClips(_alice, new BookmarkClipQueryFilter(), null).Items;
        Assert.All(rows, r => Assert.Equal(BookmarkClipState.RenameUnfinished, r.Clip.State));
        var rename = await Assert.ThrowsAnyAsync<PluginException>(() => ServiceOver(files).RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "New", RenameFile = true }, Ct));
        Assert.Equal((PluginErrorKind.Unavailable, ClipFileOps.RenameUnfinishedMessage), (rename.Kind, rename.Message));
        var described = files.DescribeRenames();
        Assert.Equal((3, 0, 3), ((int)described["Unfinished"]!, (int)described["Waiting"]!, (int)described["Unreadable"]!));
        Assert.Equal(new[] { record.Id, tooLarge.Id }.Order(), ((System.Collections.Generic.IReadOnlyList<Guid>)described["Bookmarks"]!).Order());

        // Removed by an admin: the clip is free again at the next start.
        File.Delete(entry);
        File.Delete(large);
        var next = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem, states: _fixture.States);
        Assert.False(next.HasUnfinishedRename(record.Id));
        Assert.Equal(ClipPathVerdict.Exists, next.Check(record.Clip.FilePath).Verdict);
    }

    [Fact]
    public async Task TheFakeFileSystem_RefusesAClipFilesMove_WithoutItsJournalEntry()
    {
        // The check the whole class runs under (without it, a journal written after the move survived every test).
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var to = Path.Combine(Path.GetDirectoryName(record.Clip!.FilePath)!, "elsewhere.mp4");

        Assert.Throws<InvalidOperationException>(() => _fileSystem.MoveFile(record.Clip.FilePath!, to));
        new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(Entry(record, to, null));
        _fileSystem.MoveFile(record.Clip.FilePath!, to);
        Assert.True(File.Exists(to));
    }

    [Fact]
    public async Task ARecordChangedAfterTheStop_GetsTheRenamedPath_ButKeepsItsNewName()
    {
        // The rename's name is applied only to a record unchanged since the rename was decided.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (_, to) = await CrashAfterTheMoveAsync(record, "Vault");
        await _fixture.Store.UpdateAsync(record.Id, b => BookmarkRecordRules.WithClipName(b, "Edited meanwhile", DateTime.UtcNow), Ct);

        var recovery = await ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem).FinishRenamesAsync(null, Ct);

        Assert.Equal(1, recovery.Completed);
        var stored = _fixture.Store.Get(record.Id)!;
        Assert.Equal(("Edited meanwhile", to), (stored.Name, stored.Clip!.FilePath));
    }

    [Fact]
    public async Task AClipQueuedJustBeforeTheStoresLock_Is409_AndNoFileMoves()
    {
        // The clip job check under the store's lock, not only the one before it.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var interfering = new InterferingBookmarkStore(_fixture.Store);
        interfering.BeforeNextWrite(b => b with { Clip = b.Clip! with { Status = BookmarkClipStatus.Queued } });
        var service = ServiceOver(ClipFileSupport.Ops(interfering, fileSystem: _fileSystem, states: _fixture.States), interfering);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => service.RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));

        Assert.Equal((PluginErrorKind.Conflict, ClipFileRenamer.InProgressMessage), (error.Kind, error.Message));
        Assert.Empty(_fileSystem.Moves);
        Assert.True(File.Exists(record.Clip!.FilePath));
        Assert.Equal("Bank job", _fixture.Store.Get(record.Id)!.Name);
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task AClipFolderThereButNotRecognised_KeepsARenameWaiting_WhileTheFileIsAtNeitherPath()
    {
        // An empty mount point at the folder's path (the real disk not mounted) isn't "the file gone".
        var usb = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(usb);
        await _fixture.Store.AddClipRootAsync(usb, Ct);
        await ClipFileSupport.MarkClipFolderAsync(_fixture.Store, usb);
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "On the stick", Folder = Path.Combine(usb, "alice") }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Moved");
        ClipFileSupport.Unplug(usb);
        Directory.CreateDirectory(usb);
        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem);

        Assert.Equal(new ClipRenameRecovery(0, 0, 0, 0, 0, 1), await files.FinishRenamesAsync(BackgroundWriter.PartialFileJanitor, Ct));
        Assert.Single(JournalFiles());
        Assert.True(files.Check(from).RenameUnfinished);

        // The real disk back in its place: finished from what is on it.
        Directory.Delete(usb);
        ClipFileSupport.PlugIn(usb);
        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 0, 0, 0), await files.FinishRenamesAsync(BackgroundWriter.Maintenance, Ct));
        Assert.Equal(to, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
    }

    [Fact]
    public async Task AFileRenamedIntoANameFreedSecondsAgo_IsStillToldApartByItsId_DespiteNtfsTunnelling()
    {
        // NTFS gives a file renamed into a name freed less than 15 s earlier the freed file's creation time, which an early
        // 1.2.0 build's identity compared. The file id isn't tunnelled.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS tunnelling and file ids are Windows'.");
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var from = record.Clip!.FilePath!;
        var to = Path.Combine(Path.GetDirectoryName(from)!, ClipFileNaming.BuildRenamedFileName(record.ItemName, "Vault", Path.GetFileName(from), record.Clip.FileStartTicks, record.Clip.FileEndTicks)!);
        var identity = PhysicalClipFileSystem.ReadFileIdentity(from)!;
        Assert.True(identity.HasFileId);

        // Another file had the new name until just now (made at a time of its own).
        await File.WriteAllTextAsync(to, "the name's previous file", Ct);
        var tunnelled = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(to, tunnelled);
        File.Delete(to);

        new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(Entry(record, to, "Vault"));
        File.Move(from, to);
        Assert.Equal(tunnelled, File.GetCreationTimeUtc(to));

        var recovery = await ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem).FinishRenamesAsync(null, Ct);

        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 0, 0, 0), recovery);
        Assert.Equal(to, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.True(ClipFileIdentity.IsSameFile(identity, PhysicalClipFileSystem.ReadFileIdentity(to)));
    }

    [Fact]
    public async Task AFileWhoseIdChangedWithTheRename_AtTheNewNameNoRecordNames_IsTakenForTheClip_WithAWarning()
    {
        // FAT and exFAT number a file by its folder entry; a rename may move it. Prefer the clip over "Lost".
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (_, to) = await CrashAfterTheMoveAsync(record, "Vault");
        _fileSystem.IdentityFilter = (path, identity) => identity is null ? null : identity with { Volume = 7, FileId = "00000000000000000000000000000abc" };
        var logger = new ListLogger();

        var recovery = await ClipFileSupport.Ops(_fixture.Store, logger: logger, fileSystem: _fileSystem).FinishRenamesAsync(null, Ct);

        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 0, 0, 0), recovery);
        Assert.Equal(to, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Contains(logger.Entries, l => l.Level == LogLevel.Warning && l.Message.Contains("couldn't be told apart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMoveReportedFailedThatMovedTheFile_CountsAsMade_AlsoWithItsIdChanged()
    {
        // A share's lost answer (RenameOnce's catch): the old path empty, a file at the new one.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        _fileSystem.FailAfterMove = source => string.Equals(source, record.Clip!.FilePath, StringComparison.OrdinalIgnoreCase) ? new IOException("The network name is no longer available.") : null;
        _fileSystem.IdentityFilter = (path, identity) => identity is not null && path.Contains("Vault", StringComparison.Ordinal) ? identity with { FileId = "0000000000000000000000000000dead", Volume = 9 } : identity;

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);

        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.True(File.Exists(_fixture.Store.Get(record.Id)!.Clip!.FilePath));
        Assert.Empty(JournalFiles());
        Assert.Single(_fileSystem.Moves);
    }

    [Fact]
    public async Task TheMove_IsFlushedToTheDisk_BeforeTheRecordNamesIt()
    {
        // With the data on C: and the clips on another volume, the record's write must never reach the
        // disk before the rename does.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        string? recordAtFlush = null;
        _fileSystem.FlushFailure = path =>
        {
            recordAtFlush = _fixture.Store.Get(record.Id)!.Clip!.FilePath;
            return false;
        };

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);

        var to = _fixture.Store.Get(record.Id)!.Clip!.FilePath!;
        Assert.Equal([to], _fileSystem.Flushes);
        Assert.Equal(record.Clip!.FilePath, recordAtFlush);
        Assert.EndsWith("Vault [00.01.00-00.01.30].mp4", renamed.Clip.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMoveThatCouldntBeFlushed_LeavesItsEntry_HoldingNothing_UntilTheNextPassSeesTheFileThere()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        _fileSystem.FlushFailure = _ => true;

        await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);

        var to = _fixture.Store.Get(record.Id)!.Clip!.FilePath!;
        Assert.Single(JournalFiles());
        Assert.False(Files.HasUnfinishedRename(record.Id));
        Assert.Equal(ClipPathVerdict.Exists, Files.Check(to).Verdict);
        Assert.Equal(BookmarkClipState.Ready, Service().GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.Single().Clip.State);
        Assert.Equal(1, Files.WaitingRenames);

        Assert.Equal(new ClipRenameRecovery(1, 0, 0, 0, 0, 0), await Files.FinishRenamesAsync(BackgroundWriter.PartialFileJanitor, Ct, quiet: true));
        Assert.Empty(JournalFiles());
        Assert.Equal(0, Files.WaitingRenames);
    }

    [Fact]
    public async Task APowerCutThatKeptTheRecordsWrite_AndLostTheRename_NamesTheOldPathAgain()
    {
        // The record names the new path, the file is at the old one, the entry is there: the clip volume lost the rename.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var from = record.Clip!.FilePath!;
        var to = Path.Combine(Path.GetDirectoryName(from)!, "Heat (1995) - Vault [00.01.00-00.01.30].mp4");
        new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(Entry(record, to, "Vault"));
        await _fixture.Store.UpdateAsync(record.Id, b => b with { Name = "Vault", Clip = b.Clip! with { FilePath = to } }, Ct);
        var logger = new ListLogger();

        var recovery = await ClipFileSupport.Ops(_fixture.Store, logger: logger, fileSystem: _fileSystem).FinishRenamesAsync(null, Ct);

        Assert.Equal(new ClipRenameRecovery(0, 0, 1, 0, 0, 0), recovery);
        Assert.Equal(from, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.True(File.Exists(from));
        Assert.Contains(logger.Entries, l => l.Level == LogLevel.Warning && l.Message.Contains("names the old name again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingAnEntry_RemovesOnlyTheOneFinished_NeverANewerOneOfTheSameClip()
    {
        // Two entries of one clip: removing the first, which is no longer the clip's, leaves the newer one.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var folder = Path.GetDirectoryName(record.Clip!.FilePath)!;
        var journal = new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance);
        var first = Entry(record, Path.Combine(folder, "first.mp4"), "First");
        var second = Entry(record, Path.Combine(folder, "second.mp4"), "Second");
        journal.Add(first);
        journal.Add(second);

        Assert.False(journal.Remove(first));
        Assert.Equal(second, journal.Get(record.Id));
        Assert.Single(JournalFiles());
        Assert.True(journal.Remove(second));
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task AChangeOfCaseStoppedBeforeTheMove_IsNotMoved_AndAfterIt_TheRecordTakesTheCaseOnDisk()
    {
        // An early 1.2.0 build's recovery said Completed for a change of case whatever the disk held.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A change of case alone is a rename of one file on Windows.");
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "bank job" }, Ct);
        var from = record.Clip!.FilePath!;
        var to = Path.Combine(Path.GetDirectoryName(from)!, "Heat (1995) - Bank Job [00.01.00-00.01.30].mp4");
        var journal = new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance);
        journal.Add(Entry(record, to, "Bank Job"));

        Assert.Equal(new ClipRenameRecovery(0, 0, 1, 0, 0, 0), await ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem).FinishRenamesAsync(null, Ct));
        Assert.Equal(record, _fixture.Store.Get(record.Id));

        // Stopped after the move this time.
        new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(Entry(record, to, "Bank Job"));
        File.Move(from, to);
        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 0, 0, 0), await ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem).FinishRenamesAsync(null, Ct));
        var stored = _fixture.Store.Get(record.Id)!;
        Assert.Equal(("Bank Job", to), (stored.Name, stored.Clip!.FilePath));
        Assert.Equal(Path.GetFileName(to), Path.GetFileName(Directory.GetFiles(Path.GetDirectoryName(to)!).Single()));
    }

    [Fact]
    public async Task AnUnexpectedErrorDuringTheMove_LeavesTheRenameToTheRecovery_NeverStuckAsUnderWay()
    {
        // _renaming ends in a finally, so such an entry is finished from what is on disk.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        _fileSystem.MoveFailure = (_, _) => new InvalidOperationException("Nobody expected this.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));

        Assert.True(Files.HasUnfinishedRename(record.Id));
        Assert.True(File.Exists(record.Clip!.FilePath));
        _fileSystem.Release();
        Assert.Equal(new ClipRenameRecovery(0, 0, 1, 0, 0, 0), await Files.FinishRenamesAsync(null, Ct));
        Assert.Empty(JournalFiles());
        Assert.Equal("Vault", (await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct)).Name);
    }

    [Fact]
    public async Task ARenameWhoseRecordNamesItsNewPath_HoldsNothing_SoADeleteRightAfterItIsNo503()
    {
        // Between the record's write and the entry's end a delete was 503.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Vault");
        await _fixture.Store.UpdateAsync(record.Id, b => b with { Clip = b.Clip! with { FilePath = to } }, Ct);
        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem);

        Assert.False(files.HasUnfinishedRename(record.Id));
        Assert.Equal(ClipPathVerdict.Exists, files.Check(to).Verdict);
        Assert.Equal(ClipPathVerdict.Missing, files.Check(from).Verdict);
        Assert.Equal(ClipFileOutcome.Done, files.DeleteOnce(to, record.Id, 1).Outcome);
    }

    [Fact]
    public async Task TheDownloadsFallback_ServesTheRenamedFile_OnlyForTheRecordRenamed()
    {
        // Never another record's file through a path that record once had.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Vault");
        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem);

        Assert.Equal(to, files.FindServable(from, record.Id));
        Assert.Null(files.FindServable(from, Guid.NewGuid()));
    }

    [Fact]
    public async Task WritesThatStoppedHalfWay_AreDeletedAtTheFirstRead()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        Directory.CreateDirectory(DataFolder);
        var journalLeft = Path.Combine(DataFolder, ClipRenameJournal.FilePrefix + record.Id.ToString("N") + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
        var historyLeft = Path.Combine(DataFolder, ClipRenameHistory.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(journalLeft, "{", Ct);
        await File.WriteAllTextAsync(historyLeft, "{", Ct);
        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem);

        Assert.False(files.HasUnfinishedRename(record.Id));
        Assert.Empty(files.History!.Entries);
        Assert.False(File.Exists(journalLeft));
        Assert.False(File.Exists(historyLeft));
    }

    [Fact]
    public async Task RenameFile_WithTheClipRenamedElsewhereSince_Is409_AndNothingMoves()
    {
        // The confirmation named a file name; another tab's rename would give another.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var shown = record.UpdatedAt;
        await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Elsewhere" }, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameFileAsync(_alice, record.Id, shown, Ct));

        Assert.Equal((PluginErrorKind.Conflict, BookmarkManager.ChangedElsewhereMessage), (error.Kind, error.Message));
        Assert.Empty(_fileSystem.Moves);
        var now = _fixture.Store.Get(record.Id)!;
        Assert.Equal(record.Clip!.FilePath, now.Clip!.FilePath);
        Assert.Equal("Heat (1995) - Elsewhere [00.01.00-00.01.30].mp4", (await Service().RenameFileAsync(_alice, record.Id, now.UpdatedAt, Ct)).Clip.FileName);
    }

    [Fact]
    public async Task ARenameNotFinished_IsItsOwnState_NeverMissing_AndTheBulkDeleteFinishesItFirst()
    {
        // "Rename not finished" is a state of its own, and the bulk delete finishes a rename before it judges the clip.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var other = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Other", Start = 120, End = 150 }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Vault");
        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem, states: _fixture.States);
        var service = ServiceOver(files);

        var row = service.GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.Single(i => i.Id == record.Id);
        Assert.Equal(BookmarkClipState.RenameUnfinished, row.Clip.State);
        var summary = service.GetSummary(_alice);
        Assert.Equal((2, 0, 0), (summary.Count, summary.Missing, summary.FolderUnavailable));
        Assert.Equal(record.Clip!.FileSize + other.Clip!.FileSize, summary.TotalBytes);
        Assert.Equal(BookmarkClipState.RenameUnfinished, service.GetStatus(_alice, [record.Id]).Single().State);
        Assert.Equal([record.Id], service.GetClips(_alice, new BookmarkClipQueryFilter { State = [BookmarkClipState.RenameUnfinished] }, null).Items.Select(i => i.Id));

        var deleted = await service.BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [record.Id] }, Ct);

        Assert.Equal([record.Id], deleted.FilesDeleted);
        Assert.False(File.Exists(to));
        Assert.False(File.Exists(from));
        Assert.Null(_fixture.Store.Get(record.Id)!.Clip);
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task TheBulkDelete_LeavesAClipItCantFinishTheRenameOf_AsItIs()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        await File.WriteAllTextAsync(Path.Combine(DataFolder, ClipRenameJournal.FilePrefix + record.Id.ToString("N") + ".json"), "not an entry", Ct);
        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem, states: _fixture.States);

        var deleted = await ServiceOver(files).BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [record.Id] }, Ct);

        Assert.Equal([record.Id], deleted.Failed);
        Assert.Empty(deleted.FilesDeleted);
        Assert.True(File.Exists(record.Clip!.FilePath));
        Assert.Equal(record, _fixture.Store.Get(record.Id));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task ARenameThatWaits_IsTriedAgainEveryInterval_AndFinishedOnceItsFolderIsBack_WithADailyWarningMeanwhile()
    {
        var usb = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(usb);
        await _fixture.Store.AddClipRootAsync(usb, Ct);
        await ClipFileSupport.MarkClipFolderAsync(_fixture.Store, usb);
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "On the stick", Folder = Path.Combine(usb, "alice") }, Ct);
        var (_, to) = await CrashAfterTheMoveAsync(record, "Moved");
        ClipFileSupport.Unplug(usb);
        var logger = new ListLogger();
        var files = ClipFileSupport.Ops(_fixture.Store, logger: logger, fileSystem: _fileSystem, states: _fixture.States);

        // The daily maintenance's pass: a Warning, and Diagnostics' count.
        var daily = await files.FinishRenamesAsync(BackgroundWriter.Maintenance, Ct);
        files.WarnIfRenamesWait(daily);
        Assert.Contains(logger.Entries, l => l.Level == LogLevel.Warning && l.Message.StartsWith("1 clip file renames still aren't finished", StringComparison.Ordinal));
        Assert.Equal(1, (int)files.DescribeRenames()["Unfinished"]!);

        // Tried every interval, quietly while nothing changes (Debug, not a line a minute at Information).
        static bool Pass(ListLogger log) => log.Entries.Count(l => l.Level >= LogLevel.Information && l.Message.StartsWith("Clip file renames that weren't finished", StringComparison.Ordinal)) is var n && n > 0;
        var loud = logger.Entries.Count(l => l.Level >= LogLevel.Information && l.Message.StartsWith("Clip file renames that weren't finished", StringComparison.Ordinal));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var retry = files.RetryWaitingRenamesAsync(TimeSpan.FromMilliseconds(20), stop.Token);
        await Task.Delay(150, Ct);
        Assert.Single(JournalFiles());
        Assert.True(Pass(logger));
        Assert.Equal(loud, logger.Entries.Count(l => l.Level >= LogLevel.Information && l.Message.StartsWith("Clip file renames that weren't finished", StringComparison.Ordinal)));
        Assert.Contains(logger.Entries, l => l.Level == LogLevel.Debug && l.Message.StartsWith("Clip file renames that weren't finished", StringComparison.Ordinal));

        ClipFileSupport.PlugIn(usb);
        for (var i = 0; i < 200 && JournalFiles().Length > 0; i++)
        {
            await Task.Delay(25, Ct);
        }

        await stop.CancelAsync();
        await retry;
        Assert.Empty(JournalFiles());
        Assert.Equal(to, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Equal(0, (int)files.DescribeRenames()["Unfinished"]!);
        Assert.Equal(1, (int)files.DescribeRenames()["History"]!);
    }

    [Fact]
    public async Task ANameWithAnEmojiAtItsEightiethCharacter_RenamesTheFile_ToANameThatSurvivesARestart()
    {
        // Found in testing: the cut split the emoji, the file's name kept half of it, the JSON store wrote U+FFFD, and after
        // a restart the record named a file that didn't exist.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var name = new string('g', 79) + "\U0001F3C6" + "goal";

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = name, RenameFile = true }, Ct);

        Assert.Equal("Heat (1995) - " + new string('g', 79) + " [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        using var reopened = BookmarkStore.Create(_fixture.Directory.Combine("data", "bookmarks.json"), NullLogger.Instance);
        var stored = reopened.Get(record.Id)!;
        Assert.Equal(name, stored.Name);
        Assert.True(File.Exists(stored.Clip!.FilePath), stored.Clip.FilePath);
        Assert.Equal(_fixture.Store.Get(record.Id)!.Clip!.FilePath, stored.Clip.FilePath);
    }

    private ClipService ServiceOver(ClipFileOps files, IBookmarkStore? store = null)
        => new(
            store ?? _fixture.Store,
            _fixture.Query,
            files,
            _clips.Object,
            new ClipLibraryLocations(_library.Object, NullLogger.Instance, _fixture.Clock),
            _library.Object,
            new LibraryAccess(_library.Object, _ => []),
            NullLogger<ClipService>.Instance);
}
