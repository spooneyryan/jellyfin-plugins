using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Model;

/// <summary>
/// Which records may be looked up in the library, and which are tombstones.
/// </summary>
public sealed class BookmarkRecordRulesTests
{
    [Theory]
    [InlineData(BookmarkOrigin.Native, false, true)]
    [InlineData(BookmarkOrigin.JellyfinEnhanced, false, true)]
    [InlineData(BookmarkOrigin.Adopted, false, false)]
    [InlineData(BookmarkOrigin.Native, true, false)]
    [InlineData(BookmarkOrigin.Adopted, true, false)]
    public void HasLibraryItem_NeedsAnIdAndNoAdoption(BookmarkOrigin origin, bool emptyId, bool expected)
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid(), itemId: emptyId ? Guid.Empty : Guid.NewGuid()) with { Origin = origin };

        Assert.Equal(expected, BookmarkRecordRules.HasLibraryItem(bookmark));
    }

    [Fact]
    public void Tombstone_IsARecordWithADeletedTime()
    {
        var live = TestData.CreateBookmark(Guid.NewGuid());
        var tombstone = live with { DeletedUtc = DateTime.UtcNow };

        Assert.True(BookmarkRecordRules.IsLive(live));
        Assert.False(BookmarkRecordRules.IsTombstone(live));
        Assert.True(BookmarkRecordRules.IsTombstone(tombstone));
        Assert.False(BookmarkRecordRules.IsLive(tombstone));
    }

    [Fact]
    public void IsLiveBookmarkOf_IsTheOwnersLiveRecordOnly()
    {
        var owner = Guid.NewGuid();
        var live = TestData.CreateBookmark(owner);

        Assert.True(BookmarkRecordRules.IsLiveBookmarkOf(live, owner));
        Assert.False(BookmarkRecordRules.IsLiveBookmarkOf(live, Guid.NewGuid()));
        Assert.False(BookmarkRecordRules.IsLiveBookmarkOf(live with { DeletedUtc = DateTime.UtcNow }, owner));
    }

    [Fact]
    public void ToTombstone_KeepsTheRecord_WithItsClipReducedToTheFile()
    {
        // A remake that was still running when the bookmark was deleted.
        var deleted = new DateTime(2026, 10, 3, 19, 0, 0, DateTimeKind.Utc);
        var requested = new DateTime(2026, 10, 3, 18, 59, 0, DateTimeKind.Utc);
        var extra = new Dictionary<string, JsonElement> { ["Later"] = JsonDocument.Parse("1").RootElement };
        var bookmark = TestData.CreateBookmark(Guid.NewGuid(), 70, 95) with
        {
            Name = "Bank job",
            Note = "the reflection",
            Extra = extra,
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Running,
                Mode = BookmarkClipMode.Accurate,
                StartTicks = 70 * TestData.OneSecond,
                EndTicks = 95 * TestData.OneSecond,
                RequestedAt = requested,
                Error = "an old error",
                FilePath = @"C:\clips\alice\Heat (1995) - Bank job [00.01.00-00.01.30].MKV",
                FileSize = 1234,
                FileStartTicks = 60 * TestData.OneSecond,
                FileEndTicks = 90 * TestData.OneSecond,
                EstimatedBytes = 99_000,
                MissingSinceUtc = requested,
                Extra = extra
            }
        };

        var tombstone = BookmarkRecordRules.ToTombstone(bookmark, deleted);

        Assert.NotNull(tombstone);
        Assert.True(BookmarkRecordRules.IsTombstone(tombstone));
        Assert.Equal(deleted, tombstone.DeletedUtc);
        Assert.Equal(deleted, tombstone.UpdatedAt);
        Assert.Equal(bookmark with { DeletedUtc = deleted, UpdatedAt = deleted, Clip = tombstone.Clip }, tombstone);
        Assert.Equal(
            bookmark.Clip with
            {
                Status = BookmarkClipStatus.Completed,
                Error = null,
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                Mode = BookmarkClipMode.Fast,
                EstimatedBytes = null
            },
            tombstone.Clip);
        Assert.Null(tombstone.Clip!.CompletedAt);
        Assert.Same(extra, tombstone.Clip.Extra);
        Assert.Same(tombstone, BookmarkRecordRules.ToTombstone(tombstone, deleted.AddDays(1)));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("clip.mp4", true, true)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void ToTombstone_WithoutAClipFile_IsNull(string? filePath, bool hasClip, bool expectTombstone)
    {
        // Step 3: without a file (no clip, or a first job that hasn't written one) the record is removed instead.
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            Clip = hasClip ? new ClipInfo { Status = BookmarkClipStatus.Queued, FilePath = filePath } : null
        };

        Assert.Equal(expectTombstone, BookmarkRecordRules.ToTombstone(bookmark, DateTime.UtcNow) is not null);
    }

    [Theory]
    [InlineData("a.mp4", BookmarkClipMode.Accurate)]
    [InlineData("a.mkv", BookmarkClipMode.Fast)]
    [InlineData("a.MKV", BookmarkClipMode.Fast)]
    [InlineData("a.mkv.partial", BookmarkClipMode.Accurate)]
    [InlineData("a", BookmarkClipMode.Accurate)]
    public void FileOnly_TakesTheModeFromTheExtension_AndFallsBackToTheJobsRange(string file, BookmarkClipMode mode)
    {
        // A 1.0 clip has no file range (FileStartTicks and FileEndTicks came later): the job's range stays.
        var clip = new ClipInfo
        {
            Status = BookmarkClipStatus.Failed,
            Mode = mode == BookmarkClipMode.Fast ? BookmarkClipMode.Accurate : BookmarkClipMode.Fast,
            StartTicks = 5,
            EndTicks = 9,
            FilePath = file,
            CompletedAt = DateTime.UtcNow
        };

        var fileOnly = BookmarkRecordRules.FileOnly(clip);

        Assert.Equal(mode, fileOnly.Mode);
        Assert.Equal(mode, ClipFileNaming.GetMode(file));
        Assert.Equal(BookmarkClipStatus.Completed, fileOnly.Status);
        Assert.Equal(5, fileOnly.StartTicks);
        Assert.Equal(9, fileOnly.EndTicks);
        Assert.Null(fileOnly.CompletedAt); // the failed job's time, not the file's
    }

    [Theory]
    [InlineData(BookmarkClipStatus.Completed, true)]
    [InlineData(BookmarkClipStatus.Queued, false)]
    [InlineData(BookmarkClipStatus.Running, false)]
    [InlineData(BookmarkClipStatus.Failed, false)]
    [InlineData(BookmarkClipStatus.Cancelled, false)]
    public void FileOnly_KeepsTheCompletionTime_OnlyOfTheJobThatMadeTheFile(BookmarkClipStatus status, bool kept)
    {
        // Only a Completed clip's CompletedAt is its file's. In any other state with a file, the file is an earlier job's and
        // the time a later job's: a remake that failed or was cancelled (the delete cancels a queued one at its own time), or
        // one still queued or running (normally without a time; here with one, as another writer could leave it). The
        // request time stays the last job's.
        var requested = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        var clip = new ClipInfo
        {
            Status = status,
            RequestedAt = requested,
            CompletedAt = requested.AddMinutes(1),
            FilePath = "a.mp4",
            FileSize = 4
        };

        var fileOnly = BookmarkRecordRules.FileOnly(clip);

        Assert.Equal(BookmarkClipStatus.Completed, fileOnly.Status);
        Assert.Equal(kept ? clip.CompletedAt : null, fileOnly.CompletedAt);
        Assert.Equal(requested, fileOnly.RequestedAt);
        Assert.Equal(clip with { Status = BookmarkClipStatus.Completed, CompletedAt = fileOnly.CompletedAt }, fileOnly);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("2026-09-30T08:00:00.1234567Z", true)]
    [InlineData("2026-09-30T08:00:00.123Z", true)]
    [InlineData("2026-09-30T10:00:00.1234567+02:00", true)]
    [InlineData("2026-09-30T08:00:00.1234567", true)]
    [InlineData("2026-09-30T08:00:00.1234568Z", false)]
    [InlineData("2026-09-30T08:00:00.1234566Z", false)]
    [InlineData("2026-09-30T08:00:00.124Z", false)]
    [InlineData("2026-09-30T08:00:00.122Z", false)]
    [InlineData("2026-09-30T08:00:00Z", false)]
    [InlineData("2026-09-30T08:00:00.1230001Z", false)]
    [InlineData("2026-09-30T08:00:00.1234567+01:00", false)]
    public void IsUnchangedSince_TheTokenIsUpdatedAt_ToTheTick_OrItsJavaScriptMillisecond(string? token, bool expected)
    {
        // IfUpdatedAt. A JavaScript Date keeps milliseconds only, so ".123Z" is the token ".1234567Z" read
        // back; a token with ticks below the millisecond must be exact. A token with an offset names a UTC instant; one
        // without a zone is UTC, as the API always sends.
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with { UpdatedAt = new DateTime(2026, 9, 30, 8, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567) };
        var parsed = token is null ? (DateTime?)null : JsonSerializer.Deserialize<DateTime>("\"" + token + "\"");

        Assert.Equal(expected, BookmarkRecordRules.IsUnchangedSince(bookmark, parsed));
    }

    [Fact]
    public void IsUnchangedSince_AStoredTimeOnAWholeMillisecond_MatchesItsOwnJavaScriptForm()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with { UpdatedAt = new DateTime(2026, 9, 30, 8, 0, 0, 123, DateTimeKind.Utc) };

        Assert.True(BookmarkRecordRules.IsUnchangedSince(bookmark, new DateTime(2026, 9, 30, 8, 0, 0, 123, DateTimeKind.Utc)));
        Assert.False(BookmarkRecordRules.IsUnchangedSince(bookmark, new DateTime(2026, 9, 30, 8, 0, 0, 122, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("2026-09-30T08:00:00.1234567Z", "2026-09-30T08:00:05.0000000Z", "2026-09-30T08:00:05.0000000Z")]
    [InlineData("2026-09-30T08:00:00.1234567Z", "2026-09-30T08:00:00.1239999Z", "2026-09-30T08:00:00.1240000Z")]
    [InlineData("2026-09-30T08:00:00.1230000Z", "2026-09-30T08:00:00.1230000Z", "2026-09-30T08:00:00.1240000Z")]
    [InlineData("2026-09-30T08:00:00.1234567Z", "2026-09-30T07:00:00.0000000Z", "2026-09-30T08:00:00.1240000Z")]
    [InlineData("2026-09-30T08:00:00.1234567Z", "2026-09-30T08:00:00.1245000Z", "2026-09-30T08:00:00.1245000Z")]
    public void NextUpdatedAt_IsNow_ButAlwaysInALaterMillisecond(string previous, string now, string expected)
    {
        // Every change gives a token that no JavaScript form of an earlier one matches, also within one millisecond or with
        // the clock set back.
        static DateTime Utc(string text) => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

        var next = BookmarkRecordRules.NextUpdatedAt(Utc(previous), Utc(now));

        Assert.Equal(Utc(expected), next);
        Assert.Equal(DateTimeKind.Utc, next.Kind);
        var old = TestData.CreateBookmark(Guid.NewGuid()) with { UpdatedAt = Utc(previous) };
        var changed = old with { UpdatedAt = next };
        Assert.False(BookmarkRecordRules.IsUnchangedSince(changed, new DateTime(old.UpdatedAt.Ticks - (old.UpdatedAt.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc)));
        Assert.False(BookmarkRecordRules.IsUnchangedSince(changed, old.UpdatedAt));
    }

    [Fact]
    public void Identity_IsNeverNull()
    {
        var bookmark = new Bookmark { Identity = null! };

        Assert.Same(BookmarkRecordRules.NoIdentity, bookmark.Identity);
        Assert.Same(BookmarkRecordRules.NoIdentity, new Bookmark().Identity);
        Assert.Equal(Guid.Empty, BookmarkRecordRules.NoIdentity.ItemId);
        Assert.Empty(BookmarkRecordRules.NoIdentity.ProviderIds);
    }
}
