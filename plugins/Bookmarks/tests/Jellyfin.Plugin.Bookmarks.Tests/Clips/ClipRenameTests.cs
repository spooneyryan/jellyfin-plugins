using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip's rename (Rename and Rename file, <c>ClipFileRenamer</c>) over real files and the real store: the name only, the name and the file in one write, the file alone; a taken name
/// numbered (a file, a clip being written, another user's record whose file is gone, a file waiting to be deleted), a name the
/// file has already, a change of case; names that aren't safe as file names; a clip being made, a file in use (faked and
/// held for real), a folder offline or not recognised, a file gone, another user's clip, no file, a stale IfUpdatedAt and
/// read-only data, each with nothing changed; a deleted bookmark's kept clip; the record's write failing after the move (the
/// file put back; and the file not put back, the rename held until it is finished); a stop between the move and the record's
/// write (the journal finishes it at the next start, from what is on disk, also with a folder offline at the start, a new
/// file at the old path, or the record gone); and the name gate shared with the clip worker. Every move of a clip file the
/// rename makes here finds its journal entry on disk first (<see cref="FakeClipFileSystem.RenameJournalFolder"/>); the
/// harder cases are in the class's other parts.
/// </summary>
public sealed partial class ClipRenameTests : IDisposable
{
    private readonly ClipListFixture _fixture = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private ClipFileOps? _files;

    public ClipRenameTests()
    {
        _library.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>())).Returns((Video?)null);
        _library.Setup(m => m.GetVirtualFolders()).Returns([]);

        // The crash guarantee: a clip file is moved only once its rename is in the journal on disk.
        _fileSystem.RenameJournalFolder = DataFolder;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClipFileOps Files => _files ??= ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem, states: _fixture.States);

    private string DataFolder => _fixture.Directory.Combine("data");

    [Fact]
    public async Task TheNameOnly_RenamesTheRecord_TheFileKeepsItsName_AndTheClipReadsNameChanged()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "  Vault  ", IfUpdatedAt = record.UpdatedAt }, Ct);

        Assert.Equal("Vault", renamed.Name);
        Assert.Equal(Path.GetFileName(file), renamed.Clip.FileName);
        Assert.True(renamed.Clip.IsNameOutdated);
        Assert.Equal(BookmarkClipState.Ready, renamed.Clip.State);
        var stored = _fixture.Store.Get(record.Id)!;
        Assert.Equal("Vault", stored.Name);
        Assert.Equal(file, stored.Clip!.FilePath);
        Assert.True(stored.UpdatedAt > record.UpdatedAt);
        Assert.Equal(stored.UpdatedAt, renamed.UpdatedAt);
        Assert.True(File.Exists(file));
        Assert.Empty(_fileSystem.Moves);
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task TheNameAndTheFile_AreRenamedInOneWrite_TheFileGettingTheNameBookmarksGivesItNow()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var old = record.Clip!.FilePath!;

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true, IfUpdatedAt = record.UpdatedAt }, Ct);

        var expected = Path.Combine(Path.GetDirectoryName(old)!, "Heat (1995) - Vault [00.01.00-00.01.30].mp4");
        Assert.Equal(Path.GetFileName(expected), renamed.Clip.FileName);
        Assert.False(renamed.Clip.IsNameOutdated);
        Assert.Equal(BookmarkClipState.Ready, renamed.Clip.State);
        Assert.True(renamed.Clip.FileExists);
        var stored = _fixture.Store.Get(record.Id)!;
        Assert.Equal(("Vault", expected), (stored.Name, stored.Clip!.FilePath));
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(old));
        Assert.Equal([(old, expected)], _fileSystem.Moves);
        Assert.Empty(JournalFiles());
        Assert.Null(Files.Journal!.Get(record.Id));
        Assert.Equal(ClipPathVerdict.Exists, Files.Check(expected).Verdict);
    }

    [Fact]
    public async Task RenameFile_AfterTheBookmarkWasRenamedElsewhere_GivesTheFileTheNewName_AndLeavesTheNameAndUpdatedAt()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", Mode = BookmarkClipMode.Fast }, Ct);
        await _fixture.Store.UpdateAsync(record.Id, b => b with { Name = "Vault" }, Ct);
        var before = _fixture.Store.Get(record.Id)!;
        Assert.True(Service().GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.Single().Clip.IsNameOutdated);

        var renamed = await Service().RenameFileAsync(_alice, record.Id, null, Ct);

        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mkv", renamed.Clip.FileName);
        Assert.False(renamed.Clip.IsNameOutdated);
        var stored = _fixture.Store.Get(record.Id)!;
        Assert.Equal(before.UpdatedAt, stored.UpdatedAt);
        Assert.Equal("Vault", stored.Name);
        Assert.True(File.Exists(stored.Clip!.FilePath));
        Assert.False(File.Exists(record.Clip!.FilePath));

        // Again: the file has that name already, so nothing is moved and nothing written.
        var bytes = await File.ReadAllBytesAsync(_fixture.Directory.Combine("data", "bookmarks.json"), Ct);
        var again = await Service().RenameFileAsync(_alice, record.Id, null, Ct);
        Assert.Equal(renamed.Clip.FileName, again.Clip.FileName);
        Assert.Single(_fileSystem.Moves);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_fixture.Directory.Combine("data", "bookmarks.json"), Ct));
    }

    [Fact]
    public async Task ATakenName_IsNumbered_ByAnyFile_AClipBeingWritten_AnyRecordsPath_AWaitingDelete_AndNeverOverwritten()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var folder = Path.GetDirectoryName(record.Clip!.FilePath)!;
        string Named(string suffix) => Path.Combine(folder, "Heat (1995) - Vault [00.01.00-00.01.30]" + suffix + ".mp4");

        // The plain name: another file there (any file, ours or not), in another case.
        await File.WriteAllTextAsync(Named(string.Empty).ToUpperInvariant(), "someone else's", Ct);

        // " (2)": a clip being written under it.
        await File.WriteAllTextAsync(Named(" (2)") + ClipFileGuard.PartialExtension, "being written", Ct);

        // " (3)": Bob's record names it, though its file is gone (two records never share a file).
        await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Bob's", FileName = Path.GetFileName(Named(" (3)")), Folder = folder, OnDisk = false }, Ct);

        // " (4)": a file waiting to be deleted (the retry would delete what is there).
        await _fixture.Store.AddPendingFileDeleteAsync(Named(" (4)"), Ct);

        // " (5)": a folder.
        Directory.CreateDirectory(Named(" (5)"));

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);

        Assert.Equal(Path.GetFileName(Named(" (6)")), renamed.Clip.FileName);
        Assert.Equal("someone else's", await File.ReadAllTextAsync(Named(string.Empty), Ct));
        Assert.True(File.Exists(Named(" (6)")));
        Assert.False(File.Exists(record.Clip.FilePath));
    }

    [Fact]
    public async Task ANumberedName_IsKeptWhileThePlainOneIsTaken_AndDroppedOnceItIsFree()
    {
        // A later Rename file can restore the canonical name once the old file is gone.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", FileName = "Heat (1995) - Bank job [00.01.00-00.01.30] (2).mp4" }, Ct);
        var plain = Path.Combine(Path.GetDirectoryName(record.Clip!.FilePath)!, "Heat (1995) - Bank job [00.01.00-00.01.30].mp4");
        await File.WriteAllTextAsync(plain, "the old remake's file, in use", Ct);

        var kept = await Service().RenameFileAsync(_alice, record.Id, null, Ct);
        Assert.Equal("Heat (1995) - Bank job [00.01.00-00.01.30] (2).mp4", kept.Clip.FileName);
        Assert.Empty(_fileSystem.Moves);

        File.Delete(plain);
        var restored = await Service().RenameFileAsync(_alice, record.Id, null, Ct);
        Assert.Equal("Heat (1995) - Bank job [00.01.00-00.01.30].mp4", restored.Clip.FileName);
        Assert.True(File.Exists(plain));
    }

    [Fact]
    public async Task AChangeOfCaseAlone_RenamesTheFile()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "bank job" }, Ct);
        var folder = Path.GetDirectoryName(record.Clip!.FilePath)!;

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Bank Job", RenameFile = true }, Ct);

        Assert.Equal("Heat (1995) - Bank Job [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.Equal(["Heat (1995) - Bank Job [00.01.00-00.01.30].mp4"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(Path.Combine(folder, "Heat (1995) - Bank Job [00.01.00-00.01.30].mp4"), _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Empty(JournalFiles());
    }

    [Theory]
    [InlineData("CON", "Heat (1995) - CON [00.01.00-00.01.30].mp4")]
    [InlineData("NUL.", "Heat (1995) - NUL [00.01.00-00.01.30].mp4")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "Heat (1995) - a_b_c -d_e_f_g_h_i_j [00.01.00-00.01.30].mp4")]
    [InlineData("Ends in dots...", "Heat (1995) - Ends in dots [00.01.00-00.01.30].mp4")]
    [InlineData("tab\there\u0001", "Heat (1995) - tab here [00.01.00-00.01.30].mp4")]
    [InlineData("..", "Heat (1995) [00.01.00-00.01.30].mp4")]
    public async Task NamesThatArentSafeInAFileName_GiveASafeFileName(string name, string fileName)
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = name, RenameFile = true }, Ct);

        Assert.Equal(fileName, renamed.Clip.FileName);
        var stored = _fixture.Store.Get(record.Id)!.Clip!.FilePath!;
        Assert.Equal(fileName, Directory.GetFiles(Path.GetDirectoryName(stored)!).Select(Path.GetFileName).Single());
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public async Task ALongName_GivesAFileNameWithinTheLimits()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", ItemName = new string('I', 200) }, Ct);
        var name = new string('n', BookmarkNames.MaxNameLength);

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = name, RenameFile = true }, Ct);

        // Each part at most 80 characters (ClipFileNaming), the whole name far under 255.
        Assert.Equal(new string('I', 80) + " - " + new string('n', 80) + " [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.True(File.Exists(_fixture.Store.Get(record.Id)!.Clip!.FilePath));
        var tooLong = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = name + "x", RenameFile = true }, Ct));
        Assert.Equal((PluginErrorKind.Invalid, BookmarkNames.TooLongMessage), (tooLong.Kind, tooLong.Message));
    }

    [Theory]
    [InlineData(BookmarkClipStatus.Running, true)]
    [InlineData(BookmarkClipStatus.Queued, true)]
    [InlineData(BookmarkClipStatus.Queued, false)]
    public async Task AClipBeingMade_Is409_WithNothingChanged(BookmarkClipStatus status, bool renameFile)
    {
        // Rename or delete while the clip is being re-made: 409 "Wait for the clip to finish or cancel it first."
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", Status = status }, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = renameFile }, Ct));

        Assert.Equal((PluginErrorKind.Conflict, ClipFileRenamer.InProgressMessage), (error.Kind, error.Message));
        Assert.Equal(record, _fixture.Store.Get(record.Id));
        Assert.True(File.Exists(record.Clip!.FilePath));
        Assert.Empty(_fileSystem.Moves);
        var file = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameFileAsync(_alice, record.Id, null, Ct));
        Assert.Equal(PluginErrorKind.Conflict, file.Kind);
    }

    [Fact]
    public async Task AFileInUse_AfterThreeAttempts_Is409_AndNothingIsRenamed_NotTheNameEither()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;
        _fileSystem.HoldOpen(file);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));

        Assert.Equal((PluginErrorKind.Conflict, ClipFileOps.InUseMessage), (error.Kind, error.Message));
        Assert.Equal(ClipFileOps.Attempts, _fileSystem.Moves.Count(m => m.Source == file));
        Assert.Equal(record, _fixture.Store.Get(record.Id));
        Assert.True(File.Exists(file));
        Assert.Empty(JournalFiles());
        Assert.Null(Files.Journal!.Get(record.Id));

        // Released, it is renamed.
        _fileSystem.Release();
        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);
        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
    }

    [Fact]
    public async Task AFileHeldOpenForReal_AsADownloadHoldsIt_Is409_AndIsRenamedOnceItIsClosed()
    {
        // A file held open with new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite): the rename is
        // 409; once the stream is closed, it succeeds.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to rename a file open without delete sharing.");
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;
        var service = Service(PhysicalClipFileSystem.Instance);
        var storeBefore = await File.ReadAllBytesAsync(_fixture.Directory.Combine("data", "bookmarks.json"), Ct);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(() => service.RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));
            Assert.Equal((PluginErrorKind.Conflict, ClipFileOps.InUseMessage), (error.Kind, error.Message));
            Assert.Equal(storeBefore, await File.ReadAllBytesAsync(_fixture.Directory.Combine("data", "bookmarks.json"), Ct));
            Assert.True(File.Exists(file));
        }

        var renamed = await service.RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);
        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task AFolderOffline_OrThereButNotRecognised_Is503_AndAFileGoneFromItsOwnFolderIs404_WithNothingChanged()
    {
        var usb = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(usb);
        await _fixture.Store.AddClipRootAsync(usb, Ct);
        await ClipFileSupport.MarkClipFolderAsync(_fixture.Store, usb);
        var offline = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "On the stick", Folder = Path.Combine(usb, "alice") }, Ct);
        var stray = _fixture.Directory.Combine("stray");
        Directory.CreateDirectory(stray);
        await _fixture.Store.AddClipRootAsync(stray, Ct);
        var unrecognised = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Stray", Folder = Path.Combine(stray, "alice"), OnDisk = false }, Ct);
        var gone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Gone", OnDisk = false }, Ct);
        ClipFileSupport.Unplug(usb);

        foreach (var (record, kind, message) in new[]
        {
            (offline, PluginErrorKind.Unavailable, ClipFileOps.FolderUnavailableMessage),
            (unrecognised, PluginErrorKind.Unavailable, ClipFileOps.FolderUnrecognisedMessage),
            (gone, PluginErrorKind.NotFound, ClipFileRenamer.FileMissingMessage)
        })
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "New", RenameFile = true }, Ct));
            Assert.Equal((kind, message), (error.Kind, error.Message));
            Assert.Equal(record, _fixture.Store.Get(record.Id));
            var fileOnly = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameFileAsync(_alice, record.Id, null, Ct));
            Assert.Equal(kind, fileOnly.Kind);
        }

        Assert.Empty(_fileSystem.Moves);
        Assert.True(File.Exists(Path.Combine(usb + "-unplugged", "alice", Path.GetFileName(offline.Clip!.FilePath)!)));

        // The name alone touches no file: it is renamed whatever its folder is.
        var named = await Service().RenameAsync(_alice, offline.Id, new BookmarkClipRenameRequest { Name = "New" }, Ct);
        Assert.Equal("New", named.Name);
        Assert.Equal(BookmarkClipState.FolderUnavailable, named.Clip.State);
    }

    [Fact]
    public async Task AnotherUsersClip_ABookmarkWithoutAClip_AndAClipWithoutAFile_Are404()
    {
        var bobs = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Bob's", UserFolder = "bob" }, Ct);
        var noClip = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "No clip", NoClip = true }, Ct);
        var failed = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Failed", Status = BookmarkClipStatus.Failed, HasFile = false }, Ct);

        foreach (var id in new[] { bobs.Id, noClip.Id, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, id, new BookmarkClipRenameRequest { Name = "Mine" }, Ct));
            Assert.Equal((PluginErrorKind.NotFound, ClipFileRenamer.NotFoundMessage), (error.Kind, error.Message));
        }

        Assert.Equal(bobs, _fixture.Store.Get(bobs.Id));
        var noFile = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameFileAsync(_alice, failed.Id, null, Ct));
        Assert.Equal((PluginErrorKind.NotFound, ClipFileRenamer.NoFileMessage), (noFile.Kind, noFile.Message));

        // A failed clip's name is renamed (its next clip takes it).
        Assert.Equal("Retry", (await Service().RenameAsync(_alice, failed.Id, new BookmarkClipRenameRequest { Name = "Retry" }, Ct)).Name);
    }

    [Fact]
    public async Task ARecordChangedSinceIfUpdatedAt_Is409_AndADeletedBookmarksKeptClip_IsRenamedWithItsFile()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var stale = record.UpdatedAt;
        await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "First", IfUpdatedAt = stale }, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Second", RenameFile = true, IfUpdatedAt = stale }, Ct));

        Assert.Equal((PluginErrorKind.Conflict, BookmarkManager.ChangedElsewhereMessage), (error.Kind, error.Message));
        Assert.Equal("First", _fixture.Store.Get(record.Id)!.Name);
        Assert.Empty(_fileSystem.Moves);

        var kept = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Kept", Tombstone = true, Start = 120, End = 150 }, Ct);
        var renamed = await Service().RenameAsync(_alice, kept.Id, new BookmarkClipRenameRequest { Name = "Still kept", RenameFile = true, IfUpdatedAt = kept.UpdatedAt }, Ct);
        Assert.True(renamed.BookmarkDeleted);
        Assert.Equal("Heat (1995) - Still kept [00.02.00-00.02.30].mp4", renamed.Clip.FileName);
        Assert.True(BookmarkRecordRules.IsTombstone(_fixture.Store.Get(kept.Id)!));
    }

    [Fact]
    public async Task ReadOnlyData_Is503_BeforeTheNameIsLookedAt()
    {
        var file = _fixture.Directory.Combine("newer", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, StoreFixtures.Read(StoreFixtures.Schema3), Ct);
        using var readOnly = BookmarkStore.Create(file, NullLogger.Instance);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service(store: readOnly).RenameAsync(_alice, Guid.NewGuid(), new BookmarkClipRenameRequest { Name = new string('n', 101), RenameFile = true }, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        var fileOnly = await Assert.ThrowsAnyAsync<PluginException>(() => Service(store: readOnly).RenameFileAsync(_alice, Guid.NewGuid(), null, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, fileOnly.Kind);
    }

    [Fact]
    public async Task ARecordWriteThatFailsAfterTheMove_PutsTheFileBack_AndNothingChanged()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;

        PluginException error;
        using (new FileStream(_fixture.Directory.Combine("data", "bookmarks.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));
        }

        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        Assert.Equal(record, _fixture.Store.Get(record.Id));
        Assert.True(File.Exists(file));
        var renamedPath = Path.Combine(Path.GetDirectoryName(file)!, "Heat (1995) - Vault [00.01.00-00.01.30].mp4");
        Assert.False(File.Exists(renamedPath));
        Assert.Equal([(file, renamedPath), (renamedPath, file)], _fileSystem.Moves);
        Assert.Empty(JournalFiles());
        Assert.Equal(ClipPathVerdict.Exists, Files.Check(file).Verdict);
    }

    [Fact]
    public async Task ARecordWriteThatFails_WithTheFileNotPutBack_HoldsBothPaths_UntilTheRenameIsFinished()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;
        var renamedPath = Path.Combine(Path.GetDirectoryName(file)!, "Heat (1995) - Vault [00.01.00-00.01.30].mp4");
        _fileSystem.MoveFailure = (source, _) => string.Equals(source, renamedPath, StringComparison.OrdinalIgnoreCase) ? FakeClipFileSystem.InUse(source) : null;

        using (new FileStream(_fixture.Directory.Combine("data", "bookmarks.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));

            Assert.Equal((PluginErrorKind.Unavailable, ClipFileOps.RenameUnfinishedMessage), (error.Kind, error.Message));
            Assert.True(File.Exists(renamedPath));
            Assert.False(File.Exists(file));
            Assert.Equal(file, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
            Assert.Single(JournalFiles());
            Assert.True(Files.HasUnfinishedRename(record.Id));

            // Both paths held while the data can't be written: nothing deletes, forgets or moves the clip meanwhile (each
            // answers 503 with the rename's sentence); its file is served from where it is.
            Assert.Equal((ClipPathVerdict.Unreadable, true), (Files.Check(file).Verdict, Files.Check(file).RenameUnfinished));
            Assert.True(Files.Check(renamedPath).RenameUnfinished);
            Assert.Equal(renamedPath, Files.FindServable(file, record.Id));
            Assert.Equal(ClipFileOutcome.Failed, Files.DeleteOnce(file, record.Id, 1).Outcome);
            var refused = ClipFileOps.ToRequestError(Files.Refusal(Files.Check(file), record.Id, file));
            Assert.Equal((PluginErrorKind.Unavailable, ClipFileOps.RenameUnfinishedMessage), (refused.Kind, refused.Message));
            var again = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameFileAsync(_alice, record.Id, null, Ct));
            Assert.Equal((PluginErrorKind.Unavailable, ClipFileOps.RenameUnfinishedMessage), (again.Kind, again.Message));
            Assert.Equal(new ClipRenameRecovery(0, 0, 0, 0, 0, 1), await Files.FinishRenamesAsync(null, Ct));
            Assert.True(File.Exists(renamedPath));
        }

        // Once the data can be written again, the next rename, delete, remake or start finishes it: the record names the
        // renamed file, with the name the rename gave it, and nothing is held.
        _fileSystem.Release();
        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 0, 0, 0), await Files.FinishRenamesAsync(null, Ct));
        var stored = _fixture.Store.Get(record.Id)!;
        Assert.Equal(("Vault", renamedPath), (stored.Name, stored.Clip!.FilePath));
        Assert.Empty(JournalFiles());
        Assert.False(Files.HasUnfinishedRename(record.Id));
        Assert.Equal(ClipPathVerdict.Exists, Files.Check(renamedPath).Verdict);
    }

    [Fact]
    public async Task AStopBetweenTheMoveAndTheRecordsWrite_IsFinishedAtTheNextStart_TheRecordNamingTheFileWithTheRenamesName()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Vault");

        // The next start: a new process's file operations read the journal; the janitor finishes the rename first.
        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem, states: _fixture.States);
        Assert.True(files.HasUnfinishedRename(record.Id));
        Assert.True(files.Check(from).RenameUnfinished, "held before the janitor runs (a request that comes first)");
        var run = await ClipFileSupport.Janitor(_fixture.Store, files).RunAtStartupAsync(Ct);

        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 0, 0, 0), run.Renames);
        var stored = _fixture.Store.Get(record.Id)!;
        Assert.Equal(("Vault", to), (stored.Name, stored.Clip!.FilePath));
        Assert.True(stored.UpdatedAt > record.UpdatedAt);
        Assert.Empty(JournalFiles());
        Assert.False(files.HasUnfinishedRename(record.Id));
        Assert.Equal(ClipPathVerdict.Missing, files.Check(from).Verdict);
        Assert.Equal(ClipPathVerdict.Exists, files.Check(to).Verdict);
        Assert.Equal(BookmarkClipState.Ready, Service().GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.Single().Clip.State);
        Assert.Equal(ClipRenameRecovery.None, await files.FinishRenamesAsync(BackgroundWriter.Maintenance, Ct));
    }

    [Fact]
    public async Task AStopBeforeTheMove_OrAfterTheRecordsWrite_IsFinishedWithoutAChange()
    {
        var before = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Before" }, Ct);
        var after = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "After", Start = 120, End = 150 }, Ct);
        var journal = new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance);
        var target = Path.Combine(Path.GetDirectoryName(before.Clip!.FilePath)!, "Heat (1995) - Not yet [00.01.00-00.01.30].mp4");
        journal.Add(Entry(before, target, "Not yet"));

        // After: moved and written, the entry still there.
        var written = Path.Combine(Path.GetDirectoryName(after.Clip!.FilePath)!, "Heat (1995) - Written [00.02.00-00.02.30].mp4");
        journal.Add(Entry(after, written, "Written"));
        File.Move(after.Clip.FilePath!, written);
        await _fixture.Store.UpdateAsync(after.Id, b => b with { Name = "Written", Clip = b.Clip! with { FilePath = written } }, Ct);
        var writtenRecord = _fixture.Store.Get(after.Id);

        var recovery = await ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem).FinishRenamesAsync(BackgroundWriter.PartialFileJanitor, Ct);

        Assert.Equal(new ClipRenameRecovery(1, 0, 1, 0, 0, 0), recovery);
        Assert.Equal(before, _fixture.Store.Get(before.Id));
        Assert.Equal(writtenRecord, _fixture.Store.Get(after.Id));
        Assert.True(File.Exists(before.Clip.FilePath));
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task AFolderOfflineAtTheStart_KeepsTheRenameHeld_AndItIsFinishedOnceTheFolderIsBack()
    {
        var usb = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(usb);
        await _fixture.Store.AddClipRootAsync(usb, Ct);
        await ClipFileSupport.MarkClipFolderAsync(_fixture.Store, usb);
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "On the stick", Folder = Path.Combine(usb, "alice") }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Moved");
        ClipFileSupport.Unplug(usb);

        var files = ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem);
        Assert.Equal(new ClipRenameRecovery(0, 0, 0, 0, 0, 1), await files.FinishRenamesAsync(BackgroundWriter.PartialFileJanitor, Ct));
        Assert.Single(JournalFiles());
        Assert.Equal(from, _fixture.Store.Get(record.Id)!.Clip!.FilePath);

        ClipFileSupport.PlugIn(usb);
        Assert.True(files.Check(from).RenameUnfinished, "still held once it is back, until finished");
        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 0, 0, 0), await files.FinishRenamesAsync(BackgroundWriter.Maintenance, Ct));
        Assert.Equal(to, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task ANewFileAtTheOldPath_IsToldApartByItsIdentity_AndARecordGoneMeanwhile_LeavesTheFileAsItIs()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Vault");

        // Another clip written under the freed name meanwhile (another size, another time).
        await File.WriteAllTextAsync(from, "another clip, longer", Ct);
        File.SetLastWriteTimeUtc(from, DateTime.UtcNow.AddMinutes(5));
        var gone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Gone later", Start = 120, End = 150 }, Ct);
        var (_, goneTo) = await CrashAfterTheMoveAsync(gone, "Renamed then deleted");
        await _fixture.Store.RemoveAsync(gone.Id, Ct);

        var logger = new Jellyfin.Plugin.Kit.TestSupport.Logging.ListLogger();
        var recovery = await ClipFileSupport.Ops(_fixture.Store, logger: logger, fileSystem: _fileSystem).FinishRenamesAsync(null, Ct);

        Assert.Equal(new ClipRenameRecovery(0, 1, 0, 1, 0, 0), recovery);
        Assert.Equal(to, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Equal("another clip, longer", await File.ReadAllTextAsync(from, Ct));
        Assert.True(File.Exists(goneTo), "a file no record names stays where it is");
        Assert.Contains(logger.Entries, l => l.Level == LogLevel.Warning && l.Message.Contains("is no clip's file any more", StringComparison.Ordinal) && l.Message.Contains(gone.Id.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, l => l.Level >= LogLevel.Information && (l.Message.Contains("Vault", StringComparison.Ordinal) || l.Message.Contains(_fixture.Root, StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task TheRenameChoosesItsName_UnderTheGateTheClipWorkerPlansUnder()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var service = Service();
        await Files.NameGate.WaitAsync(Ct);
        Task<BookmarkClipItemDto> rename;
        try
        {
            rename = service.RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);
            await Task.Delay(100, Ct);
            Assert.False(rename.IsCompleted, "waits while a clip job plans its name");
            Assert.Empty(_fileSystem.Moves);
        }
        finally
        {
            Files.NameGate.Release();
        }

        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", (await rename).Clip.FileName);

        // The name alone never waits for it.
        await Files.NameGate.WaitAsync(Ct);
        try
        {
            Assert.Equal("Only", (await service.RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Only" }, Ct)).Name);
        }
        finally
        {
            Files.NameGate.Release();
        }
    }

    public void Dispose() => _fixture.Dispose();

    private static ClipRenameEntry Entry(Bookmark record, string to, string? name)
        => new()
        {
            BookmarkId = record.Id,
            From = record.Clip!.FilePath!,
            To = to,
            File = PhysicalClipFileSystem.ReadFileIdentity(record.Clip.FilePath!),
            NameChanged = name is not null,
            Name = name,
            UpdatedAt = record.UpdatedAt,
            StartedUtc = DateTime.UtcNow
        };

    // What a stop between the move and the record's write leaves: the journal entry flushed, the file at its new path, the
    // record naming the old one.
    private async Task<(string From, string To)> CrashAfterTheMoveAsync(Bookmark record, string name)
    {
        var from = record.Clip!.FilePath!;
        var to = Path.Combine(Path.GetDirectoryName(from)!, ClipFileNaming.BuildRenamedFileName(record.ItemName, name, Path.GetFileName(from), record.Clip.FileStartTicks, record.Clip.FileEndTicks)!);
        new ClipRenameJournal(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(Entry(record, to, name));
        File.Move(from, to);
        await Task.Yield();
        return (from, to);
    }

    private string[] JournalFiles() => Directory.GetFiles(DataFolder, ClipRenameJournal.FilePrefix + "*");

    private ClipService Service(IClipFileSystem? fileSystem = null, IBookmarkStore? store = null)
    {
        store ??= _fixture.Store;
        var files = fileSystem is null && ReferenceEquals(store, _fixture.Store) ? Files : ClipFileSupport.Ops(store, fileSystem: fileSystem ?? _fileSystem, states: _fixture.States);
        return new ClipService(
            store,
            ReferenceEquals(store, _fixture.Store) ? _fixture.Query : new ClipQueryService(store, new ClipStateCache(store)),
            files,
            _clips.Object,
            new ClipLibraryLocations(_library.Object, NullLogger.Instance, _fixture.Clock),
            _library.Object,
            new LibraryAccess(_library.Object, _ => []),
            NullLogger<ClipService>.Instance);
    }
}
