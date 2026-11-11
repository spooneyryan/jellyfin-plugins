using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The My Clips list through <see cref="ClipQueryService"/> on real files: the
/// caller's clips only, tombstones included; each state (<c>FolderUnavailable</c> among them), mode, type, the deleted toggle,
/// each searched field; every sort; the clips being made first; paging and its 400s; the summary; the files' and the clip
/// folders' answers kept 30 s; and what a request asks of the file system: the page's clips and
/// those only their file dates, for a state filter only the clips it could match, nothing for a clip being made, and every
/// clip with a file for the summary.
/// </summary>
public sealed class ClipQueryServiceTests : IDisposable
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly DateTime T0 = ClipListFixture.T0;

    private readonly ClipListFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rows_AreTheUsersClips_LiveAndKept_NeverAnotherUsers_NorABookmarkWithoutAClip()
    {
        var live = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Live" }, Ct);
        var kept = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Kept", Tombstone = true }, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Name = "No clip", NoClip = true }, Ct);
        await _fixture.AddAsync(Bob, new ClipSpec { Name = "Bob's", UserFolder = "bob" }, Ct);

        var page = Query(Alice);

        Assert.Equal([kept.Id, live.Id], ClipListFixture.Ids(page));
        Assert.Equal(2, page.Total);
        Assert.True(page.Rows[0].IsTombstone);
        Assert.Empty(ClipListFixture.Ids(Query(Alice, new() { SearchTerm = "Bob" })));
    }

    [Fact]
    public async Task StateFilter_ListsEachState_OneOrSeveral_AndTheOfflineFolderIsItsOwn()
    {
        var offline = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(offline);
        await _fixture.Store.AddClipRootAsync(offline, Ct);
        var ready = await _fixture.AddAsync(Alice, new ClipSpec(), Ct);
        var making = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false }, Ct);
        var failed = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, HasFile = false }, Ct);
        var missing = await _fixture.AddAsync(Alice, new ClipSpec { OnDisk = false }, Ct);
        var unplugged = await _fixture.AddAsync(Alice, new ClipSpec { Folder = Path.Combine(offline, "alice") }, Ct);
        ClipFileSupport.Unplug(offline);

        Assert.Equal([ready.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.Ready] })));
        Assert.Equal([making.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.InProgress] })));
        Assert.Equal([failed.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.Failed] })));
        Assert.Equal([missing.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.Missing] })));
        Assert.Equal([unplugged.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.FolderUnavailable] })));
        Assert.Equal(
            new[] { missing.Id, unplugged.Id }.Order(),
            ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.Missing, BookmarkClipState.FolderUnavailable] })).Order());
        Assert.Equal(5, Query(Alice, new() { State = [] }).Total);
    }

    [Fact]
    public async Task ModeFilter_IsTheFilesMode_ButTheJobsForAClipBeingMade()
    {
        var accurate = await _fixture.AddAsync(Alice, new ClipSpec { Mode = BookmarkClipMode.Accurate }, Ct);
        var fast = await _fixture.AddAsync(Alice, new ClipSpec { Mode = BookmarkClipMode.Fast }, Ct);

        // An MP4 next to a Fast remake that failed is an MP4; one with a Fast remake running is made Fast.
        var failedFastRemake = await _fixture.AddAsync(Alice, new ClipSpec { Mode = BookmarkClipMode.Accurate, JobMode = BookmarkClipMode.Fast, Status = BookmarkClipStatus.Failed }, Ct);
        var runningFastRemake = await _fixture.AddAsync(Alice, new ClipSpec { Mode = BookmarkClipMode.Accurate, JobMode = BookmarkClipMode.Fast, Status = BookmarkClipStatus.Running }, Ct);

        Assert.Equal(new[] { accurate.Id, failedFastRemake.Id }.Order(), ClipListFixture.Ids(Query(Alice, new() { Mode = BookmarkClipMode.Accurate })).Order());
        Assert.Equal(new[] { fast.Id, runningFastRemake.Id }.Order(), ClipListFixture.Ids(Query(Alice, new() { Mode = BookmarkClipMode.Fast })).Order());
    }

    [Fact]
    public async Task TypeDeletedAndVideoFilters_EachNarrowTheList()
    {
        var item = Guid.NewGuid();
        var movie = await _fixture.AddAsync(Alice, new ClipSpec { Kind = "Movie", ItemId = item }, Ct);
        var episode = await _fixture.AddAsync(Alice, new ClipSpec { Kind = "Episode", ItemName = "The Wire S01E02 - The Detail" }, Ct);
        var home = await _fixture.AddAsync(Alice, new ClipSpec { Kind = "Video", ItemName = "birthday cake" }, Ct);
        var keptMovie = await _fixture.AddAsync(Alice, new ClipSpec { Kind = "Movie", ItemId = item, Tombstone = true, Start = 100, End = 110 }, Ct);

        Assert.Equal(new[] { movie.Id, keptMovie.Id }.Order(), ClipListFixture.Ids(Query(Alice, new() { ItemTypes = [BookmarkItemType.Movie] })).Order());
        Assert.Equal([episode.Id], ClipListFixture.Ids(Query(Alice, new() { ItemTypes = [BookmarkItemType.Episode] })));
        Assert.Equal([home.Id], ClipListFixture.Ids(Query(Alice, new() { ItemTypes = [BookmarkItemType.Other] })));
        Assert.Equal(4, Query(Alice, new() { ItemTypes = [] }).Total);

        // The clips of deleted bookmarks are listed unless asked not to.
        Assert.Equal(4, Query(Alice, new() { IncludeDeleted = true }).Total);
        Assert.DoesNotContain(keptMovie.Id, ClipListFixture.Ids(Query(Alice, new() { IncludeDeleted = false })));
        Assert.Equal(3, Query(Alice, new() { IncludeDeleted = false }).Total);

        // One video: only narrows the caller's own clips by the id they stored.
        Assert.Equal(new[] { movie.Id, keptMovie.Id }.Order(), ClipListFixture.Ids(Query(Alice, new() { ItemId = item })).Order());
        Assert.Equal([movie.Id], ClipListFixture.Ids(Query(Alice, new() { ItemId = item, IncludeDeleted = false })));
        Assert.Empty(ClipListFixture.Ids(Query(Alice, new() { ItemId = Guid.NewGuid() })));
    }

    [Theory]
    [InlineData("bank", "name")]
    [InlineData("BANK JOB", "name")]
    [InlineData("reflection", "note")]
    [InlineData("wire", "item")]
    [InlineData("  heist  ", "file")]
    [InlineData("amelie", "accents")]
    [InlineData("CAFÉ", "accents")]
    public async Task Search_LooksInTheNameNoteVideoAndFileName_IgnoringCaseAndAccents(string term, string expected)
    {
        var name = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Bank job", ItemName = "Other (2001)" }, Ct);
        var note = await _fixture.AddAsync(Alice, new ClipSpec { Note = "Watch the reflection in the window", ItemName = "Other (2002)" }, Ct);
        var item = await _fixture.AddAsync(Alice, new ClipSpec { ItemName = "The Wire S01E02 - The Detail" }, Ct);
        var file = await _fixture.AddAsync(Alice, new ClipSpec { ItemName = "Other (2003)", FileName = "Old heist [00.01.00-00.01.30].mp4" }, Ct);
        var accents = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Amélie, café scène", ItemName = "Other (2004)" }, Ct);
        await _fixture.AddAsync(Bob, new ClipSpec { Name = "Bank job heist wire amelie reflection", UserFolder = "bob" }, Ct);
        var ids = new System.Collections.Generic.Dictionary<string, Guid> { ["name"] = name.Id, ["note"] = note.Id, ["item"] = item.Id, ["file"] = file.Id, ["accents"] = accents.Id };

        var page = Query(Alice, new() { SearchTerm = term });

        Assert.Equal([ids[expected]], ClipListFixture.Ids(page));
    }

    [Fact]
    public async Task Sorts_EachInItsOwnDirection_TiesOnTheId()
    {
        // Made at different times (a kept clip without its job's time is dated by its file), named, on videos, sized and
        // long as the clips of a real list; b and d on one video, stored under two spellings of its name.
        var alien = Guid.NewGuid();
        var a = await _fixture.AddAsync(Alice, new ClipSpec { Name = "b", ItemName = "Heat (1995)", Completed = T0.AddHours(3), Size = 300, Start = 60, End = 90 }, Ct);
        var b = await _fixture.AddAsync(Alice, new ClipSpec { Name = "A", ItemName = "alien (1979)", ItemId = alien, Completed = T0.AddHours(1), Size = 900, Start = 10, End = 70 }, Ct);
        var c = await _fixture.AddAsync(Alice, new ClipSpec { Note = "c note\nsecond line", ItemName = "Brazil (1985)", Status = BookmarkClipStatus.Queued, Written = T0.AddHours(2), Tombstone = true, Size = 100, Start = 0, End = 5 }, Ct);
        var d = await _fixture.AddAsync(Alice, new ClipSpec { ItemName = "Alien (1979)", ItemId = alien, FileName = "d-file [00.00.00-00.00.10].mkv", Completed = T0.AddHours(4), Size = null, Start = 5, End = 15 }, Ct);
        var e = await _fixture.AddAsync(Alice, new ClipSpec { ItemName = "Zulu (1964)", Status = BookmarkClipStatus.Failed, HasFile = false, Requested = T0.AddHours(5), Start = 0, End = 25 }, Ct);

        // Newest: when the file was made (the kept clip by its file's time), else when the clip was asked for (e).
        Assert.Equal([e.Id, d.Id, a.Id, c.Id, b.Id], Sorted(BookmarkClipSortBy.Newest));
        Assert.Equal([e.Id, d.Id, a.Id, c.Id, b.Id], Sorted(null));
        Assert.Equal([b.Id, c.Id, a.Id, d.Id, e.Id], Sorted(BookmarkClipSortBy.Oldest));

        // The title: the name, else the note's first line, else the file name; none (e) last.
        Assert.Equal([b.Id, a.Id, c.Id, d.Id, e.Id], Sorted(BookmarkClipSortBy.Name));

        // The video's name ignoring case, then where the clip starts.
        Assert.Equal([d.Id, b.Id, c.Id, a.Id, e.Id], Sorted(BookmarkClipSortBy.ItemName));

        // Largest first, the clips without a file or a size last (by the newest among them).
        Assert.Equal([b.Id, a.Id, c.Id, e.Id, d.Id], Sorted(BookmarkClipSortBy.Size));

        // Longest first: the file's range, else the job's.
        Assert.Equal([b.Id, a.Id, e.Id, d.Id, c.Id], Sorted(BookmarkClipSortBy.Duration));
    }

    [Fact]
    public async Task TheClipsBeingMade_ComeFirst_WhateverTheSort_TheRunningOneThenTheQueueInOrder()
    {
        var done = await _fixture.AddAsync(Alice, new ClipSpec { Name = "A done", Completed = T0.AddDays(1), Size = 9_000 }, Ct);
        var queuedLater = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Z later", Status = BookmarkClipStatus.Queued, HasFile = false, Requested = T0.AddHours(2) }, Ct);
        var running = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Y running", Status = BookmarkClipStatus.Running, Requested = T0.AddHours(3) }, Ct);
        var queuedFirst = await _fixture.AddAsync(Alice, new ClipSpec { Name = "X first", Status = BookmarkClipStatus.Queued, Requested = T0.AddHours(1) }, Ct);

        foreach (var sort in Enum.GetValues<BookmarkClipSortBy>())
        {
            Assert.Equal([running.Id, queuedFirst.Id, queuedLater.Id, done.Id], Sorted(sort));
        }

        // Pinned to the top of the whole list, so to the first page; a state filter without InProgress pins nothing.
        Assert.Equal([running.Id, queuedFirst.Id], ClipListFixture.Ids(Query(Alice, new() { Limit = 2 })));
        Assert.Equal([queuedLater.Id, done.Id], ClipListFixture.Ids(Query(Alice, new() { Limit = 2, StartIndex = 2 })));
        Assert.Equal([done.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.Ready] })));
    }

    [Fact]
    public async Task Paging_AtItsEdges_AndOutOfRangeValuesAre400()
    {
        await _fixture.AddAllAsync(Enumerable.Range(0, 205).Select(i => _fixture.Make(Alice, new ClipSpec { Completed = T0.AddMinutes(-i), OnDisk = false })), Ct);

        Assert.Equal(50, Query(Alice).Rows.Count);
        Assert.Equal(200, Query(Alice, new() { Limit = 200 }).Rows.Count);
        Assert.Single(Query(Alice, new() { Limit = 1 }).Rows);
        var last = Query(Alice, new() { StartIndex = 200, Limit = 200 });
        Assert.Equal(5, last.Rows.Count);
        Assert.Equal(205, last.Total);
        Assert.Equal(200, last.StartIndex);
        var beyond = Query(Alice, new() { StartIndex = 205 });
        Assert.Empty(beyond.Rows);
        Assert.Equal(205, beyond.Total);

        // Pages follow each other without a gap or a repeat.
        var all = ClipListFixture.Ids(Query(Alice, new() { Limit = 200 })).Concat(ClipListFixture.Ids(last)).ToList();
        Assert.Equal(205, all.Distinct().Count());

        foreach (var (filter, message) in new (BookmarkClipQueryFilter, string)[]
        {
            (new() { Limit = 0 }, "The limit must be between 1 and 200."),
            (new() { Limit = 201 }, "The limit must be between 1 and 200."),
            (new() { StartIndex = -1 }, "The start index can't be negative."),
            (new() { SearchTerm = new string('x', 101) }, "Search terms can be at most 100 characters long.")
        })
        {
            var error = Assert.Throws<BookmarkException>(() => Query(Alice, filter));
            Assert.Equal(PluginErrorKind.Invalid, error.Kind);
            Assert.Equal(message, error.Message);
        }

        Assert.Equal(205, Query(Alice, new() { SearchTerm = "   " }).Total);
        Assert.Equal(0, Query(Alice, new() { SearchTerm = new string('x', 100) }).Total);
    }

    [Fact]
    public async Task Summary_CountsEveryClipByState_AndTheBytesOfEveryFileNotKnownToBeGone()
    {
        var offline = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(offline);
        await _fixture.Store.AddClipRootAsync(offline, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Size = 1_000 }, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Size = 2_000, Tombstone = true }, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Size = 40_000, OnDisk = false }, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Size = 300, Folder = Path.Combine(offline, "alice") }, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Size = 50, Status = BookmarkClipStatus.Running }, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false }, Ct);
        await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, HasFile = false }, Ct);
        await _fixture.AddAsync(Bob, new ClipSpec { Size = 7_000_000, UserFolder = "bob" }, Ct);
        ClipFileSupport.Unplug(offline);

        var summary = _fixture.Query.Summarize(_fixture.Query.Rows(Alice));

        Assert.Equal(7, summary.Count);
        Assert.Equal(2, summary.InProgress);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Missing);
        Assert.Equal(1, summary.FolderUnavailable);

        // Ready 1,000 and 2,000, offline 300 (probably still on the drive), the running remake's earlier file 50; the missing
        // file's 40,000 not; Bob's never.
        Assert.Equal(3_350, summary.TotalBytes);
    }

    [Fact]
    public async Task TheFilesAndFoldersAnswers_AreKept30Seconds()
    {
        var record = await _fixture.AddAsync(Alice, new ClipSpec(), Ct);
        Assert.Equal(BookmarkClipState.Ready, Query(Alice).Rows[0].Facts.State);

        File.Delete(record.Clip!.FilePath!);

        // Within the 30 s the list still has the answer it got (display only: no delete or download uses it).
        _fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(BookmarkClipState.Ready, Query(Alice).Rows[0].Facts.State);

        _fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BookmarkClipState.Missing, Query(Alice).Rows[0].Facts.State);
    }

    [Fact]
    public async Task ThePage_AsksTheFileSystemOnlyAboutItsOwnClips_AndThoseOnlyTheirFileDates()
    {
        // At most 200 File.Exists calls per request (the page size). Found in testing: the default order (Newest) dated
        // every clip by its file's facts, so a page of 50 asked about all of the user's clips. A finished job's completion
        // is its file's date; only a kept clip whose job's time was lost, and an earlier file next to a
        // remake that failed, are dated by the file itself.
        var files = new CountingFileSystem();
        using var fixture = new ClipListFixture(files);
        await fixture.AddAllAsync(Enumerable.Range(0, 300).Select(i => fixture.Make(Alice, new ClipSpec { Name = $"c{i}", Completed = T0.AddMinutes(-i) })), Ct);
        var keptWhileQueued = fixture.Make(Alice, new ClipSpec { Name = "kept while queued", Status = BookmarkClipStatus.Queued, Tombstone = true, Written = T0.AddDays(-2) });
        var failedRemake = fixture.Make(Alice, new ClipSpec { Name = "failed remake", Status = BookmarkClipStatus.Failed, Written = T0.AddDays(-3) });
        await fixture.AddAllAsync([keptWhileQueued, failedRemake], Ct);
        files.Reset();

        var page = fixture.Query.Query(fixture.Query.Rows(Alice), new BookmarkClipQueryFilter(), null);

        // The order asked about the two its files date, nothing else.
        Assert.Equal(2, files.FileCalls);
        Assert.Equal(2, files.TimeCalls);
        Assert.Equal(50, page.Rows.Count);
        Assert.Equal(302, page.Total);

        // What the page shows asks about its 50 clips.
        Assert.All(page.Rows, row => Assert.Equal(BookmarkClipState.Ready, row.Facts.State));
        Assert.Equal(52, files.FileCalls);

        // Oldest puts the two first, dated by their files (the time kept with their verdicts).
        files.Reset();
        var oldest = fixture.Query.Query(fixture.Query.Rows(Alice), new BookmarkClipQueryFilter { Limit = 2 }, BookmarkClipSortBy.Oldest);
        Assert.Equal([failedRemake.Id, keptWhileQueued.Id], ClipListFixture.Ids(oldest));
        Assert.Equal(0, files.FileCalls + files.TimeCalls);

        // No other order asks anything of the file system before the page is shown.
        foreach (var sort in new[] { BookmarkClipSortBy.Name, BookmarkClipSortBy.ItemName, BookmarkClipSortBy.Size, BookmarkClipSortBy.Duration })
        {
            fixture.Clock.Advance(ClipStateCache.Lifetime);
            files.Reset();
            fixture.Query.Query(fixture.Query.Rows(Alice), new BookmarkClipQueryFilter(), sort);
            Assert.True(files.FileCalls <= 2, $"{sort}: {files.FileCalls} file calls");
        }
    }

    [Fact]
    public async Task AStateFilter_AsksTheFileSystemOnlyAboutTheClipsItCouldMatch()
    {
        var files = new CountingFileSystem();
        using var fixture = new ClipListFixture(files);
        await fixture.AddAllAsync(Enumerable.Range(0, 100).Select(i => fixture.Make(Alice, new ClipSpec { Name = $"c{i}", Completed = T0.AddMinutes(-i), OnDisk = i % 10 != 0 })), Ct);
        await fixture.AddAllAsync(Enumerable.Range(0, 5).Select(_ => fixture.Make(Alice, new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false })), Ct);
        await fixture.AddAllAsync(Enumerable.Range(0, 5).Select(_ => fixture.Make(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, HasFile = false })), Ct);
        files.Reset();

        // Being made and failed: the records say so.
        Assert.Equal(5, fixture.Query.Query(fixture.Query.Rows(Alice), new() { State = [BookmarkClipState.InProgress] }, null).Total);
        Assert.Equal(5, fixture.Query.Query(fixture.Query.Rows(Alice), new() { State = [BookmarkClipState.Failed] }, null).Total);
        Assert.Equal(10, fixture.Query.Query(fixture.Query.Rows(Alice), new() { State = [BookmarkClipState.Failed, BookmarkClipState.InProgress] }, null).Total);
        Assert.Equal(0, files.FileCalls);

        // A file's state: every clip with a file could match, so each is judged once (the recorded cost).
        Assert.Equal(10, fixture.Query.Query(fixture.Query.Rows(Alice), new() { State = [BookmarkClipState.Missing] }, null).Total);
        Assert.Equal(100, files.FileCalls);
        Assert.Equal(95, fixture.Query.Query(fixture.Query.Rows(Alice), new() { State = [BookmarkClipState.Ready, BookmarkClipState.InProgress] }, null).Total);
        Assert.Equal(100, files.FileCalls);
    }

    [Fact]
    public void TheStateOfAClipBeingMade_AsksTheFileSystemNothing()
    {
        // What the page's poll of the clips being made reads (Clips/Status): the state only, which the record says.
        var files = new CountingFileSystem();
        using var fixture = new ClipListFixture(files);
        var remake = fixture.Make(Alice, new ClipSpec { Status = BookmarkClipStatus.Running });
        var queued = fixture.Make(Alice, new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false });
        var failed = fixture.Make(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, HasFile = false });
        var ready = fixture.Make(Alice, new ClipSpec());
        files.Reset();

        var rows = fixture.Query.Rows([remake, queued, failed]);

        Assert.Equal([BookmarkClipState.InProgress, BookmarkClipState.InProgress, BookmarkClipState.Failed], rows.Select(r => r.State));
        Assert.Equal(0, files.FileCalls + files.FolderCalls);
        Assert.Equal(BookmarkClipState.Ready, Assert.Single(fixture.Query.Rows([ready])).State);
        Assert.Equal(1, files.FileCalls);
    }

    [Fact]
    public async Task TheSummary_JudgesEveryClipWithAFile_OncePer30Seconds()
    {
        // The summary does call the file system: its Missing and FolderUnavailable count the same
        // verdicts as the rows, so it judges every clip with a file, each once per 30 s (a file in memory here).
        var files = new CountingFileSystem();
        using var fixture = new ClipListFixture(files);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "alice"));
        await fixture.AddAllAsync(Enumerable.Range(0, 2_000).Select(i => fixture.Make(Alice, new ClipSpec { Name = $"c{i}", Completed = T0.AddMinutes(-i), OnDisk = false })), Ct);
        await fixture.AddAllAsync(Enumerable.Range(0, 10).Select(_ => fixture.Make(Alice, new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false })), Ct);
        files.Reset();

        var cold = fixture.Query.Summarize(fixture.Query.Rows(Alice));
        Assert.Equal((2_010, 2_000, 10), (cold.Count, cold.Missing, cold.InProgress));
        Assert.Equal(2_000, files.FileCalls);
        Assert.Equal(1, files.FolderCalls);

        var warm = fixture.Query.Summarize(fixture.Query.Rows(Alice));
        Assert.Equal(cold.Missing, warm.Missing);
        Assert.Equal(2_000, files.FileCalls);
    }

    [Fact]
    public async Task AFileGone_FromAClipFolderThatIsntRecognised_ReadsAsItsFolderOffline_NeverMissing_UntilTheFolderIsMarked()
    {
        // Found in testing (the list didn't look at the marker, so an unrecognised folder's clips read Missing and the
        // page offered Forget): a clip folder that is there without a marker of its own (an empty mount
        // point, a folder made in place of the real one) holds no missing file, as the sweep and the worker judge it; a file
        // that is there in it is Ready.
        var stray = _fixture.Directory.Combine("stray");
        Directory.CreateDirectory(stray);
        await _fixture.Store.AddClipRootAsync(stray, Ct);
        var gone = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Gone", Folder = Path.Combine(stray, "alice"), OnDisk = false, Size = 500 }, Ct);
        var there = await _fixture.AddAsync(Alice, new ClipSpec { Name = "There", Folder = Path.Combine(stray, "alice"), Size = 70 }, Ct);
        var ownMissing = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Own", OnDisk = false, Size = 9 }, Ct);

        var facts = _fixture.Query.Rows(Alice).ToDictionary(r => r.Record.Id, r => r.Facts);
        Assert.Equal((BookmarkClipState.FolderUnavailable, false, true), (facts[gone.Id].State, facts[gone.Id].FileExists, facts[gone.Id].RootAvailable));
        Assert.Equal(BookmarkClipState.Ready, facts[there.Id].State);
        Assert.Equal(BookmarkClipState.Missing, facts[ownMissing.Id].State);
        Assert.Equal([gone.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.FolderUnavailable] })));
        Assert.Equal([ownMissing.Id], ClipListFixture.Ids(Query(Alice, new() { State = [BookmarkClipState.Missing] })));

        // The summary counts it as offline, and its bytes as probably still on the real folder's drive.
        var summary = _fixture.Query.Summarize(_fixture.Query.Rows(Alice));
        Assert.Equal((1, 1, 570L), (summary.Missing, summary.FolderUnavailable, summary.TotalBytes));

        // The folder given its marker by Bookmarks (an admin's confirmation, the worker, the sweep's backfill): the list
        // judges its clips again at once, not after the 30 s.
        var ops = ClipFileSupport.Ops(_fixture.Store, states: _fixture.States);
        Assert.True(ClipRootPaths.TryNormalize(stray, out var root));
        Assert.Equal(ClipRootMarking.Written, await ops.WriteRootMarkerAsync(root, Ct));
        Assert.Equal(BookmarkClipState.Missing, _fixture.Query.Rows(Alice).Single(r => r.Record.Id == gone.Id).Facts.State);
    }

    [Fact]
    public async Task AFolderWhoseMarkerCantBeRead_IsntRecognised_AndAnAdoptableMarkerIs()
    {
        // A marker that names the folder while the store records no token yet (an early 1.2.0 build's) is the folder's, as the
        // sweep adopts it; one that can't be read is none.
        var files = new FailingMarkerFileSystem();
        using var fixture = new ClipListFixture(files);
        var adoptable = fixture.Directory.Combine("adoptable");
        Directory.CreateDirectory(adoptable);
        ClipFileSupport.MarkClipFolderWithoutToken(adoptable);
        await fixture.Store.AddClipRootAsync(adoptable, Ct);
        var gone = await fixture.AddAsync(Alice, new ClipSpec { Folder = Path.Combine(adoptable, "alice"), OnDisk = false }, Ct);
        Assert.Equal(BookmarkClipState.Missing, fixture.Query.Rows(Alice).Single(r => r.Record.Id == gone.Id).Facts.State);

        fixture.Clock.Advance(ClipStateCache.Lifetime);
        files.Fail = true;
        Assert.Equal(BookmarkClipState.FolderUnavailable, fixture.Query.Rows(Alice).Single(r => r.Record.Id == gone.Id).Facts.State);
    }

    public void Dispose() => _fixture.Dispose();

    private ClipPage Query(Guid user, BookmarkClipQueryFilter? filter = null, BookmarkClipSortBy? sortBy = null)
        => _fixture.Query.Query(_fixture.Query.Rows(user), filter ?? new BookmarkClipQueryFilter(), sortBy);

    private Guid[] Sorted(BookmarkClipSortBy? sortBy) => ClipListFixture.Ids(Query(Alice, sortBy: sortBy));

    /// <summary>The real file system, counting what the list asks: a clip file's attributes, a clip folder, a write time.</summary>
    private sealed class CountingFileSystem : IClipFileSystem
    {
        private int _files;
        private int _folders;
        private int _times;

        public int FileCalls => Volatile.Read(ref _files);

        public int FolderCalls => Volatile.Read(ref _folders);

        public int TimeCalls => Volatile.Read(ref _times);

        public void Reset()
        {
            Interlocked.Exchange(ref _files, 0);
            Interlocked.Exchange(ref _folders, 0);
            Interlocked.Exchange(ref _times, 0);
        }

        public FileAttributes? GetAttributes(string path)
        {
            if (ClipFileGuard.IsClipFileName(Path.GetFileName(path), allowPartial: false))
            {
                Interlocked.Increment(ref _files);
            }

            return PhysicalClipFileSystem.Instance.GetAttributes(path);
        }

        public bool FolderExists(string path)
        {
            Interlocked.Increment(ref _folders);
            return PhysicalClipFileSystem.Instance.FolderExists(path);
        }

        public DateTime? GetLastWriteTimeUtc(string path)
        {
            Interlocked.Increment(ref _times);
            return PhysicalClipFileSystem.Instance.GetLastWriteTimeUtc(path);
        }

        public void DeleteFile(string path) => throw new NotSupportedException();

        public void MoveFile(string source, string destination) => throw new NotSupportedException();
        public void ReplaceFile(string source, string destination) => throw new NotSupportedException();

        public System.Collections.Generic.IEnumerable<ClipFolderFile> ListClipFolder(string folder) => throw new NotSupportedException();
    }

    /// <summary>The real file system, whose marker files can't be read while <see cref="Fail"/> is set.</summary>
    private sealed class FailingMarkerFileSystem : IClipFileSystem
    {
        public bool Fail { get; set; }

        public FileAttributes? GetAttributes(string path) => PhysicalClipFileSystem.Instance.GetAttributes(path);

        public bool FolderExists(string path) => PhysicalClipFileSystem.Instance.FolderExists(path);

        public DateTime? GetLastWriteTimeUtc(string path) => PhysicalClipFileSystem.Instance.GetLastWriteTimeUtc(path);

        public string? ReadRootMarker(string folder) => Fail ? throw new IOException("The device is not ready.") : PhysicalClipFileSystem.Instance.ReadRootMarker(folder);

        public void DeleteFile(string path) => throw new NotSupportedException();

        public void MoveFile(string source, string destination) => throw new NotSupportedException();
        public void ReplaceFile(string source, string destination) => throw new NotSupportedException();

        public System.Collections.Generic.IEnumerable<ClipFolderFile> ListClipFolder(string folder) => throw new NotSupportedException();
    }
}
