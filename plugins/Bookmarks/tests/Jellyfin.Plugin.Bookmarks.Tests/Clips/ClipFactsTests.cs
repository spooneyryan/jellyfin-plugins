using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// What a clip is right now (the derived fields), through <see cref="ClipQueryService.Describe"/> on
/// real files: the <c>State</c> truth table (a clip folder that isn't there is <c>FolderUnavailable</c>, never
/// <c>Missing</c>), <c>IsNameOutdated</c> with and without the <c> (2)</c> suffix, the <c>DurationTicks</c> fallbacks,
/// <c>IsOutdated</c> (never for a tombstone), <c>LastJobFailed</c>, when the clip was made, and the container.
/// </summary>
public sealed class ClipFactsTests : IDisposable
{
    private static readonly Guid Alice = Guid.NewGuid();
    private readonly ClipListFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, BookmarkClipState, bool, bool?> States => new()
    {
        // Case, State, FileExists, RootAvailable.
        { "a completed clip whose file is there", BookmarkClipState.Ready, true, true },
        { "a completed clip whose file is gone", BookmarkClipState.Missing, false, true },
        { "a completed clip whose user folder is gone, in a clip folder that is there", BookmarkClipState.Missing, false, true },
        { "a queued clip, no file yet", BookmarkClipState.InProgress, false, null },
        { "a running remake, its earlier file there", BookmarkClipState.InProgress, true, true },
        { "a failed clip without a file", BookmarkClipState.Failed, false, null },
        { "a cancelled clip without a file", BookmarkClipState.Failed, false, null },
        { "a failed remake, its earlier file there", BookmarkClipState.Ready, true, true },
        { "a clip in a recorded clip folder that isn't there", BookmarkClipState.FolderUnavailable, false, false },
        { "a clip outside every recorded clip folder", BookmarkClipState.FolderUnavailable, false, false },
        { "a tombstone whose file is there", BookmarkClipState.Ready, true, true },
        { "a tombstone whose file is gone", BookmarkClipState.Missing, false, true },
        { "a tombstone in a clip folder that isn't there", BookmarkClipState.FolderUnavailable, false, false },
    };

    [Theory]
    [MemberData(nameof(States))]
    public async Task State_FollowsTheJob_ThenTheFile_AndAnOfflineFolderIsNeverMissing(string name, BookmarkClipState state, bool fileExists, bool? rootAvailable)
    {
        var offline = _fixture.Directory.Combine("offline-root");
        Directory.CreateDirectory(offline);
        await _fixture.Store.AddClipRootAsync(offline, Ct);
        var spec = name switch
        {
            "a completed clip whose file is there" => new ClipSpec(),
            "a completed clip whose file is gone" => new ClipSpec { OnDisk = false },
            "a completed clip whose user folder is gone, in a clip folder that is there" => new ClipSpec { OnDisk = false, UserFolder = "nobody-yet" },
            "a queued clip, no file yet" => new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false },
            "a running remake, its earlier file there" => new ClipSpec { Status = BookmarkClipStatus.Running },
            "a failed clip without a file" => new ClipSpec { Status = BookmarkClipStatus.Failed, HasFile = false },
            "a cancelled clip without a file" => new ClipSpec { Status = BookmarkClipStatus.Cancelled, HasFile = false },
            "a failed remake, its earlier file there" => new ClipSpec { Status = BookmarkClipStatus.Failed },
            "a clip in a recorded clip folder that isn't there" => new ClipSpec { Folder = Path.Combine(offline, "alice") },
            "a clip outside every recorded clip folder" => new ClipSpec { Folder = _fixture.Directory.Combine("elsewhere") },
            "a tombstone whose file is there" => new ClipSpec { Tombstone = true },
            "a tombstone whose file is gone" => new ClipSpec { Tombstone = true, OnDisk = false },
            "a tombstone in a clip folder that isn't there" => new ClipSpec { Tombstone = true, Folder = Path.Combine(offline, "alice") },
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };
        var record = await _fixture.AddAsync(Alice, spec, Ct);
        if (name.Contains("isn't there", StringComparison.Ordinal))
        {
            // The drive unplugged: its folder and the file in it go away together (the file is fine on the drive).
            ClipFileSupport.Unplug(offline);
        }

        var facts = _fixture.Query.Describe(record);

        Assert.Equal(state, facts.State);
        Assert.Equal(fileExists, facts.FileExists);
        Assert.Equal(rootAvailable, facts.RootAvailable);
    }

    [Theory]
    [InlineData("Bank job", "Bank job", null, false)]
    [InlineData("Bank job", "Vault job", null, true)]
    [InlineData("Bank job", "Bank job", " (2)", false)]
    [InlineData("Bank job", "Vault job", " (2)", true)]
    [InlineData("Bank job", "Bank job", " (0123456789abcdef0123456789abcdef)", false)]
    [InlineData("Bank job", "bank job", null, true)]
    [InlineData(null, null, null, false)]
    [InlineData(null, "Named since", null, true)]
    public async Task IsNameOutdated_ComparesTheFileNamesStem_WithWhatTheNameGivesNow(string? madeWith, string? nameNow, string? suffix, bool outdated)
    {
        // The file was made with madeWith (named by the planner, " (2)" when the name was taken); the bookmark is named
        // nameNow since. The stem before the range is compared, as written.
        var made = ClipFileNaming.BuildFileName("Heat (1995)", madeWith, 60 * TestData.OneSecond, 90 * TestData.OneSecond, BookmarkClipMode.Accurate);
        var fileName = suffix is null ? made : Path.GetFileNameWithoutExtension(made) + suffix + ".mp4";
        var record = await _fixture.AddAsync(Alice, new ClipSpec { Name = nameNow, FileName = fileName }, Ct);

        Assert.Equal(outdated, _fixture.Query.Describe(record).IsNameOutdated);
    }

    [Fact]
    public async Task IsNameOutdated_ComparesWithTheNameAlone_SoANoteInAnOlderFileNameIsOutdated_AndIgnoresANameBookmarksDoesntGive()
    {
        // Since 1.2.0 a clip file takes its bookmark's name, never its note. A file a bookmark without a
        // name got from its note before 1.2.0 is "Name changed" (Rename file gives it the video's name alone); one named after
        // the video alone isn't, note or not.
        var byNote = await _fixture.AddAsync(Alice, new ClipSpec { Note = "Watch the window", FileName = "Heat (1995) - Watch the window [00.01.00-00.01.30].mp4" }, Ct);
        var byVideo = await _fixture.AddAsync(Alice, new ClipSpec { Note = "Watch the door", FileName = "Heat (1995) [00.01.00-00.01.30].mp4" }, Ct);
        var byName = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Vault", Note = "Watch the door", FileName = "Heat (1995) - Vault [00.01.00-00.01.30].mp4" }, Ct);
        var foreign = await _fixture.AddAsync(Alice, new ClipSpec { Name = "Bank job", FileName = "my own clip.mp4" }, Ct);

        Assert.True(_fixture.Query.Describe(byNote).IsNameOutdated);
        Assert.False(_fixture.Query.Describe(byVideo).IsNameOutdated);
        Assert.False(_fixture.Query.Describe(byName).IsNameOutdated);
        Assert.False(_fixture.Query.Describe(foreign).IsNameOutdated);
    }

    [Fact]
    public async Task DurationTicks_IsTheFilesRange_ElseTheJobs()
    {
        var known = await _fixture.AddAsync(Alice, new ClipSpec { Start = 60, End = 90, FileStart = 10, FileEnd = 70, Status = BookmarkClipStatus.Running }, Ct);
        var unknown = await _fixture.AddAsync(Alice, new ClipSpec { Start = 60, End = 75, NoFileRange = true }, Ct);
        var noFile = await _fixture.AddAsync(Alice, new ClipSpec { Start = 60, End = 80, Status = BookmarkClipStatus.Failed, HasFile = false }, Ct);

        Assert.Equal(60 * TestData.OneSecond, _fixture.Query.Describe(known).DurationTicks);
        Assert.Equal(15 * TestData.OneSecond, _fixture.Query.Describe(unknown).DurationTicks);
        Assert.Equal(20 * TestData.OneSecond, _fixture.Query.Describe(noFile).DurationTicks);
    }

    [Fact]
    public async Task IsOutdated_WhenTheBookmarksRangeMoved_ButNeverForATombstone()
    {
        var same = await _fixture.AddAsync(Alice, new ClipSpec(), Ct);
        var moved = await _fixture.AddAsync(Alice, new ClipSpec { BookmarkStart = 61 }, Ct);
        var movedEnd = await _fixture.AddAsync(Alice, new ClipSpec { BookmarkEnd = 95 }, Ct);
        var tombstone = await _fixture.AddAsync(Alice, new ClipSpec { BookmarkStart = 61, Tombstone = true }, Ct);

        Assert.False(_fixture.Query.Describe(same).IsOutdated);
        Assert.True(_fixture.Query.Describe(moved).IsOutdated);
        Assert.True(_fixture.Query.Describe(movedEnd).IsOutdated);
        Assert.False(_fixture.Query.Describe(tombstone).IsOutdated);
    }

    [Fact]
    public async Task LastJobFailed_OnlyWhenAFailedOrCancelledJobLeftAnEarlierFile()
    {
        var failed = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed }, Ct);
        var cancelled = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Cancelled }, Ct);
        var failedNoFile = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, HasFile = false }, Ct);
        var failedFileGone = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, OnDisk = false }, Ct);
        var completed = await _fixture.AddAsync(Alice, new ClipSpec(), Ct);

        Assert.True(_fixture.Query.Describe(failed).LastJobFailed);
        Assert.True(_fixture.Query.Describe(cancelled).LastJobFailed);
        Assert.False(_fixture.Query.Describe(failedNoFile).LastJobFailed);
        Assert.False(_fixture.Query.Describe(failedFileGone).LastJobFailed);
        Assert.Equal(BookmarkClipState.Missing, _fixture.Query.Describe(failedFileGone).State);
        Assert.False(_fixture.Query.Describe(completed).LastJobFailed);
    }

    [Fact]
    public async Task MadeAt_IsTheCompletedJobsTime_ElseTheFilesLastWrite_ElseNone()
    {
        var t = ClipListFixture.T0;
        var completed = await _fixture.AddAsync(Alice, new ClipSpec { Completed = t.AddHours(1), Written = t.AddHours(5) }, Ct);
        var failedRemake = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, Completed = t.AddHours(2), Written = t.AddHours(6) }, Ct);
        var keptWhileQueued = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Queued, Written = t.AddHours(7), Tombstone = true }, Ct);
        var gone = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Cancelled, OnDisk = false }, Ct);
        var noFile = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Failed, HasFile = false }, Ct);

        Assert.Equal(t.AddHours(1), _fixture.Query.Describe(completed).MadeAt);
        Assert.Equal(t.AddHours(6), _fixture.Query.Describe(failedRemake).MadeAt);

        // The tombstone of a clip whose remake was queued when its bookmark was deleted: Completed without a time, so
        // its file's time.
        Assert.Equal(BookmarkClipStatus.Completed, keptWhileQueued.Clip!.Status);
        Assert.Null(keptWhileQueued.Clip.CompletedAt);
        Assert.Equal(t.AddHours(7), _fixture.Query.Describe(keptWhileQueued).MadeAt);
        Assert.Null(_fixture.Query.Describe(gone).MadeAt);
        Assert.Null(_fixture.Query.Describe(noFile).MadeAt);
    }

    [Fact]
    public async Task Container_IsTheFilesExtension()
    {
        var mp4 = await _fixture.AddAsync(Alice, new ClipSpec { Mode = BookmarkClipMode.Accurate }, Ct);
        var mkv = await _fixture.AddAsync(Alice, new ClipSpec { Mode = BookmarkClipMode.Fast }, Ct);
        var upper = await _fixture.AddAsync(Alice, new ClipSpec { FileName = "Heat (1995) [00.01.00-00.01.30].MKV" }, Ct);
        var none = await _fixture.AddAsync(Alice, new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false }, Ct);

        Assert.Equal("mp4", _fixture.Query.Describe(mp4).Container);
        Assert.Equal("mkv", _fixture.Query.Describe(mkv).Container);
        Assert.Equal("mkv", _fixture.Query.Describe(upper).Container);
        Assert.Null(_fixture.Query.Describe(none).Container);
    }

    public void Dispose()
    {
        // A test that unplugged its folder leaves it aside; the temporary folder goes either way.
        _fixture.Dispose();
    }
}
