using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip's rename, its waiting renames and history: the renames that wait are judged without a clip folder's slow answer under the
/// store's lock, and tried again less often while nothing changes; the history keeps the identity the file has at its new
/// name, is moved aside when it can't be read, read again when it changed on disk, and loses the renames of a clip file
/// deleted or of clips gone for good; an older rename's finish never clears a newer one's mark; the recovery's fallback never
/// takes another record's file; and a clip folder that misses one look-up of a new name gets a second one.
/// </summary>
public sealed partial class ClipRenameTests
{
    [Fact]
    public async Task APass_AsksAClipFolderThatDoesntAnswer_OutsideTheStoresLock_SoOtherWritesGoOn_AndItsRenameWaits()
    {
        // Found in testing: FinishRenamesAsync judged each entry under the store's write lock, so a share that
        // doesn't answer held up every user's writes for its I/O timeout, every minute.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (from, _) = await CrashAfterTheMoveAsync(record, "Vault");
        using var asked = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        _fileSystem.FolderFailure = path =>
        {
            if (!ClipRootPaths.Comparer.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(_fixture.Root)))
            {
                return null;
            }

            asked.Set();
            answer.Wait(TimeSpan.FromSeconds(120), Ct);
            return new IOException("The semaphore timeout period has expired.", unchecked((int)0x80070079));
        };

        var pass = Task.Run(() => Files.FinishRenamesAsync(null, Ct), Ct);
        Assert.True(asked.Wait(TimeSpan.FromSeconds(60), Ct));

        // While the folder is asked (it doesn't answer for 120 s), another write goes through.
        var other = _fixture.Make(_bob.Id, new ClipSpec { Name = "Elsewhere", NoClip = true });
        await _fixture.Store.AddAsync(other, Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.NotNull(_fixture.Store.Get(other.Id));

        answer.Set();
        var recovery = await pass;
        Assert.Equal(new ClipRenameRecovery(0, 0, 0, 0, 0, 1), recovery);
        Assert.Equal(from, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.NotNull(Files.Journal!.Get(record.Id));

        // Answering again, it is finished.
        _fileSystem.FolderFailure = null;
        Assert.Equal(1, (await Files.FinishRenamesAsync(null, Ct)).Completed);
    }

    [Fact]
    public async Task AClipFolderThatAnswersTooSlowly_KeepsItsRenamesWaiting_ForALaterPass()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var (from, to) = await CrashAfterTheMoveAsync(record, "Vault");
        Files.RootProbeSlowThreshold = TimeSpan.FromMilliseconds(20);
        _fileSystem.FolderFailure = _ =>
        {
            Thread.Sleep(150);
            return null;
        };

        var recovery = await Files.FinishRenamesAsync(null, Ct);

        Assert.Equal(1, recovery.Waiting);
        Assert.Equal(from, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        _fileSystem.FolderFailure = null;
        Assert.Equal(1, (await Files.FinishRenamesAsync(null, Ct)).Completed);
        Assert.Equal(to, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
    }

    [Fact]
    public void ThePauseBetweenTries_DoublesUpToItsLongest()
    {
        var pause = ClipFileOps.RenameRetryInterval;
        var pauses = Enumerable.Range(0, 6).Select(_ => pause = ClipFileOps.NextRetryPause(pause, ClipFileOps.RenameRetryMaxInterval)).ToList();

        Assert.Equal([2, 4, 8, 16, 16, 16], pauses.Select(p => (int)p.TotalMinutes));
    }

    [Fact]
    public async Task TheRetryOfTheRenamesThatWait_BacksOffWhileNothingChanges_AndStartsOverWhenANewOneWaits()
    {
        var first = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        await CrashAfterTheMoveAsync(first, "Vault");
        _fileSystem.FolderFailure = path => new IOException("The device is not ready.", unchecked((int)0x80070015));
        var pauses = new ConcurrentQueue<TimeSpan>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var step = TimeSpan.FromMilliseconds(15);

        // The loop is held after its 5th pass (in its own callback, on its own thread) while the second rename is added,
        // so no pass can run half before and half after it: on a busy runner one did, and its pause was counted as the
        // first after the new rename.
        using var held = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        void AfterPass(TimeSpan pause)
        {
            pauses.Enqueue(pause);
            if (pauses.Count == 5)
            {
                held.Set();
                resume.Wait(stop.Token);
            }
        }

        var retry = Files.RetryWaitingRenamesCoreAsync(step, step * 8, AfterPass, stop.Token);
        try
        {
            await WaitUntilAsync(() => held.IsSet);
            Assert.Equal([1, 2, 4, 8, 8], pauses.Select(p => (int)(p / step)));

            // A new rename waiting: the next pass comes within a step, and the pause starts over.
            var second = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Second", Start = 120, End = 150 }, Ct);
            var moved = Path.Combine(Path.GetDirectoryName(second.Clip!.FilePath!)!, "Heat (1995) - Safe [00.02.00-00.02.30].mp4");
            Files.Journal!.Add(Entry(second, moved, "Safe"));
            File.Move(second.Clip.FilePath!, moved);
            resume.Set();
            await WaitUntilAsync(() => pauses.Count >= 7);
            Assert.Equal([1, 2], pauses.Skip(5).Take(2).Select(p => (int)(p / step)));
        }
        finally
        {
            await stop.CancelAsync();
            await retry;
        }
    }

    [Fact]
    public async Task TheHistory_KeepsTheIdentityTheFileHasAtItsNewName_AsFatCanNumberARenamedFileAnew()
    {
        // Found in testing: the history kept the identity read before the move.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        _fileSystem.IdentityFilter = Renumbered;

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);

        var noted = Assert.Single(Files.History!.Entries);
        Assert.Equal(renamed.Clip.FileName, Path.GetFileName(noted.To));
        Assert.Equal(AtTheNewName, noted.File!.FileId);

        // The recovery's note too: a rename the server stopped during, finished at the next pass.
        var other = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Other", Start = 120, End = 150 }, Ct);
        var to = Path.Combine(Path.GetDirectoryName(other.Clip!.FilePath!)!, "Heat (1995) - Safe [00.02.00-00.02.30].mp4");
        Files.Journal!.Add(Entry(other, to, "Safe") with { File = _fileSystem.GetFileIdentity(other.Clip.FilePath!) });
        File.Move(other.Clip.FilePath!, to);
        Assert.Equal(1, (await Files.FinishRenamesAsync(null, Ct)).Completed);
        Assert.Equal(AtTheNewName, Files.History!.Entries.Single(e => e.BookmarkId == other.Id).File!.FileId);
        Assert.Equal(to, _fixture.Store.Get(other.Id)!.Clip!.FilePath);
    }

    [Fact]
    public async Task AnOlderRenamesFinish_NeverClearsTheMarkOfANewerRenameOfTheSameClip_WhoseMoveWasntFlushed()
    {
        // Found in testing: the mark of a move not flushed was kept by the record's id alone, so the finish (or
        // the recovery, or the undo) of an older rename of the clip cleared the newer one's, and its entry was dropped although
        // its move might not be on the disk.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var folder = Path.GetDirectoryName(record.Clip!.FilePath!)!;
        var middle = Path.Combine(folder, "Heat (1995) - Vault [00.01.00-00.01.30].mp4");
        var last = Path.Combine(folder, "Heat (1995) - Safe [00.01.00-00.01.30].mp4");
        _fileSystem.FlushFailure = _ => true;

        Assert.Equal(ClipFileOutcome.Done, Files.RenameOnce(Entry(record, middle, null), 1, out var older).Outcome);
        await _fixture.Store.UpdateAsync(record.Id, b => b with { Clip = b.Clip! with { FilePath = middle } }, Ct);
        var newer = new ClipRenameEntry { BookmarkId = record.Id, From = middle, To = last, UpdatedAt = record.UpdatedAt, StartedUtc = DateTime.UtcNow.AddSeconds(1) };
        Assert.Equal(ClipFileOutcome.Done, Files.RenameOnce(newer, 1, out var recorded).Outcome);
        await _fixture.Store.UpdateAsync(record.Id, b => b with { Clip = b.Clip! with { FilePath = last } }, Ct);

        Files.FinishRename(older!);
        Files.FinishRename(recorded!);

        // The newer rename's entry stays for the next pass, which drops it once it sees the file at its new name.
        Assert.Equal(last, Files.Journal!.Get(record.Id)?.To);
        Assert.Single(JournalFiles());
        _fileSystem.FlushFailure = null;
        Assert.Equal(1, (await Files.FinishRenamesAsync(null, Ct)).Written);
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public async Task TheRecoverysFallback_NeverTakesAFileAnotherRecordNames()
    {
        // Found by mutation testing: neither path holds the file as the journal recorded it, and a file is at the new path, which
        // another record (Bob's clip) names: the rename's record keeps its old path ("Lost"), never Bob's file.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var bobs = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Bobs", FileName = "Heat (1995) - Vault [00.01.00-00.01.30].mp4", Start = 300, End = 330 }, Ct);
        var from = record.Clip!.FilePath!;
        Files.Journal!.Add(Entry(record, bobs.Clip!.FilePath!, "Vault"));
        File.Delete(from);

        var recovery = await Files.FinishRenamesAsync(null, Ct);

        Assert.Equal(1, recovery.Lost);
        Assert.Equal(0, recovery.Completed);
        Assert.Equal(from, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Equal(bobs.Clip.FilePath, _fixture.Store.Get(bobs.Id)!.Clip!.FilePath);
        Assert.True(File.Exists(bobs.Clip.FilePath));
    }

    [Fact]
    public async Task AnUnreadableHistory_IsMovedAside_BeforeANewRenameIsWrittenOverIt()
    {
        Directory.CreateDirectory(DataFolder);
        var historyFile = Path.Combine(DataFolder, ClipRenameHistory.FileName);
        await File.WriteAllTextAsync(historyFile, "{ not the history", Ct);
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);

        await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);

        var aside = Assert.Single(Directory.GetFiles(DataFolder, ClipRenameHistory.UnreadablePrefix + "*.json"));
        Assert.Equal("{ not the history", await File.ReadAllTextAsync(aside, Ct));
        Assert.Single(new ClipRenameHistory(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance).Entries);

        // The next start's clean-up of half-written files leaves it alone.
        _ = new ClipRenameHistory(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance).Entries;
        Assert.True(File.Exists(aside));
    }

    [Fact]
    public async Task TheHistory_IsReadAgain_WhenItsFileChangedOnDisk()
    {
        var mine = new ClipRenameHistory(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance);
        Assert.Empty(mine.Entries);

        var another = new ClipRenameHistory(DataFolder, PhysicalClipFileSystem.Instance, NullLogger.Instance);
        another.Add(new ClipRenameEntry { BookmarkId = Guid.NewGuid(), From = @"C:\clips\a.mkv", To = @"C:\clips\b.mkv" }, DateTime.UtcNow);
        Assert.Single(mine.Entries);

        another.Add(new ClipRenameEntry { BookmarkId = Guid.NewGuid(), From = @"C:\clips\c.mkv", To = @"C:\clips\d.mkv" }, DateTime.UtcNow);
        Assert.Equal(2, mine.Entries.Count);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ADeletedClipFile_TakesTheRenamesThatLedToItOutOfTheHistory_AFileWaitingToBeDeletedToo()
    {
        // Privacy: the history's paths can start with a note's words, and outlived the clip.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var held = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Held", Start = 120, End = 150 }, Ct);
        var kept = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Kept", Start = 180, End = 210 }, Ct);
        await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);
        var twice = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Safe", RenameFile = true }, Ct);
        await Service().RenameAsync(_alice, held.Id, new BookmarkClipRenameRequest { Name = "Held renamed", RenameFile = true }, Ct);
        await Service().RenameAsync(_alice, kept.Id, new BookmarkClipRenameRequest { Name = "Kept renamed", RenameFile = true }, Ct);
        Assert.Equal(4, Files.History!.Entries.Count);
        var path = _fixture.Store.Get(record.Id)!.Clip!.FilePath!;
        Assert.Equal(twice.Clip.FileName, Path.GetFileName(path));

        Assert.Equal(ClipFileOutcome.Done, Files.DeleteOnce(path, record.Id, 1).Outcome);
        Assert.DoesNotContain(Files.History!.Entries, e => e.BookmarkId == record.Id);

        // An automatic delete comes after the record let go of the file (here, its clip dropped).
        var heldPath = _fixture.Store.Get(held.Id)!.Clip!.FilePath!;
        await _fixture.Store.UpdateAsync(held.Id, b => b with { Clip = null }, Ct);
        _fileSystem.HoldOpen(heldPath);
        var queued = await Files.DeleteOrQueueAsync(heldPath, held.Id, BackgroundWriter.ClipWorker, Ct);
        Assert.True(queued.Queued);
        Assert.Equal([kept.Id], Files.History!.Entries.Select(e => e.BookmarkId));
    }

    [Fact]
    public async Task TheDailyCare_DropsTheRenamesOfClipsGoneForGood_ButKeepsThoseWhoseFileIsStillThere()
    {
        var gone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Gone" }, Ct);
        var orphan = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Orphan", Start = 120, End = 150 }, Ct);
        var live = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Live", Start = 180, End = 210 }, Ct);
        foreach (var record in new[] { gone, orphan, live })
        {
            await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = record.Name + " renamed", RenameFile = true }, Ct);
        }

        // Gone's record and file gone (deleted by hand); Orphan's record gone, its renamed file still there (a restore of its
        // record could still name it again); Live as it is.
        File.Delete(_fixture.Store.Get(gone.Id)!.Clip!.FilePath!);
        await _fixture.Store.RemoveAsync(gone.Id, Ct);
        await _fixture.Store.RemoveAsync(orphan.Id, Ct);

        Assert.Equal(1, Files.PruneRenameHistory());
        Assert.Equal(new[] { orphan.Id, live.Id }.Order(), Files.History!.Entries.Select(e => e.BookmarkId).Order());
    }

    [Fact]
    public async Task AClipFolderThatMissesOneLookUpOfTheNewName_GetsASecondOne_OutsideTheStoresLock_AndTheRenameIsMade()
    {
        // Found in testing: one failed look-up ended the rename (503). Now the attempt is made again after a pause
        // outside the store's lock, so the look-ups are two.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;
        var lookups = 0;
        _fileSystem.AttributesFailure = path =>
        {
            if (!Path.GetFileName(path).StartsWith("Heat (1995) - Vault", StringComparison.Ordinal))
            {
                return null;
            }

            return ++lookups == 1 ? new IOException("The device is not ready.", unchecked((int)0x80070015)) : null;
        };

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);

        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.True(lookups >= 2);
        Assert.Single(_fileSystem.Moves, m => m.Source == file);
        Assert.Equal(1, Files.NameGate.CurrentCount);
    }

    [Fact]
    public async Task AClipFolderThatCantAnswerTwice_Is503_WithNothingChanged()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        _fileSystem.AttributesFailure = path => Path.GetFileName(path).StartsWith("Heat (1995) - Vault", StringComparison.Ordinal)
            ? new UnauthorizedAccessException("Access to the path is denied.")
            : null;
        var storeFile = _fixture.Directory.Combine("data", "bookmarks.json");
        var before = await File.ReadAllBytesAsync(storeFile, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));

        Assert.Equal((PluginErrorKind.Unavailable, ClipFileOps.FolderUnreachableMessage), (error.Kind, error.Message));
        Assert.Empty(_fileSystem.Moves);
        Assert.Equal(before, await File.ReadAllBytesAsync(storeFile, Ct));
        Assert.Empty(JournalFiles());
    }

    private const string AtTheNewName = "00000000000000000000000000000042";

    // FAT's way with a rename: the file numbered anew at its new name.
    private static ClipFileIdentity? Renumbered(string path, ClipFileIdentity? identity)
        => identity is null || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? identity
            : identity with
            {
                Volume = 7,
                FileId = Path.GetFileName(path).Contains(" - Vault", StringComparison.Ordinal) || Path.GetFileName(path).Contains(" - Safe", StringComparison.Ordinal)
                    ? AtTheNewName
                    : "00000000000000000000000000000041"
            };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting.");
            await Task.Delay(10, Ct);
        }
    }
}
