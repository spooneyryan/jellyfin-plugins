using System;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Library;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

public class BookmarkDtoMapperTests
{
    [Fact]
    public void ToDto_SaysWhetherTheVideoLeftTheLibrary_AndWhetherTheBookmarkWasRelinked()
    {
        // Detached is "Not in library", Relinked is "Relinked, check position"; the path never.
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());

        var attached = BookmarkDtoMapper.ToDto(bookmark, null);
        var detached = BookmarkDtoMapper.ToDto(bookmark with { DetachedUtc = DateTime.UtcNow }, null);
        var relinked = BookmarkDtoMapper.ToDto(bookmark with { RelinkedUtc = DateTime.UtcNow, RelinkStrength = Kit.Library.MatchStrength.Strong }, null);

        Assert.Equal((false, false), (attached.Detached, attached.Relinked));
        Assert.Equal((true, false), (detached.Detached, detached.Relinked));
        Assert.Equal((false, true), (relinked.Detached, relinked.Relinked));
    }

    [Fact]
    public void ToDto_CarriesTheSnapshotsYearSeriesAndNumbers_TheOrigin_AndItemAvailableOnlyWhenGiven()
    {
        // What the bookmark stored about its video; ItemAvailable is the queries' alone (null elsewhere).
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            Identity = new IdentitySnapshot { Kind = "Episode", Name = "The Detail", Year = 2002, SeriesName = "The Wire", Season = 1, Episode = 2, Path = @"C:\media\wire.mkv" },
            Origin = BookmarkOrigin.JellyfinEnhanced
        };

        var dto = BookmarkDtoMapper.ToDto(bookmark, null);
        var listed = BookmarkDtoMapper.ToDto(bookmark, null, itemAvailable: false);

        Assert.Equal(("Episode", 2002, "The Wire", 1, 2), (dto.ItemType, dto.Year, dto.SeriesName, dto.SeasonNumber, dto.EpisodeNumber));
        Assert.Equal(BookmarkOrigin.JellyfinEnhanced, dto.Origin);
        Assert.Null(dto.ItemAvailable);
        Assert.False(listed.ItemAvailable);

        // A blank series name (a hand edit) is none; a movie has none.
        var movie = BookmarkDtoMapper.ToDto(TestData.CreateBookmark(Guid.NewGuid()) with { Identity = new IdentitySnapshot { Kind = "Movie", SeriesName = " " } }, null);
        Assert.Equal(((string?)null, (int?)null, (int?)null, BookmarkOrigin.Native), (movie.SeriesName, movie.SeasonNumber, movie.EpisodeNumber, movie.Origin));
    }

    [Fact]
    public void ToDto_ExposesOnlyTheClipFileName()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            Clip = Completed(@"C:\data\bookmarks\clips\alice\Heat (1995) [00.01.00-00.01.30].mp4")
        };

        var dto = BookmarkDtoMapper.ToDto(bookmark, null);

        Assert.Equal("Heat (1995) [00.01.00-00.01.30].mp4", dto.Clip?.FileName);
        Assert.Equal(1234, dto.Clip?.FileSize);
        Assert.False(dto.Clip?.IsOutdated);
    }

    [Fact]
    public void ToDto_FailedRemake_StillOffersThePreviousFile()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        bookmark = bookmark with { Clip = Completed("old.mp4", bookmark) with { Status = BookmarkClipStatus.Failed, Error = "boom" } };

        var dto = BookmarkDtoMapper.ToDto(bookmark, null);

        Assert.Equal(BookmarkClipStatus.Failed, dto.Clip?.Status);
        Assert.Equal("boom", dto.Clip?.Error);
        Assert.Equal("old.mp4", dto.Clip?.FileName);
    }

    [Fact]
    public void ToDto_RangeChangedSinceTheFileWasMade_IsOutdated()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        bookmark = bookmark with { Clip = Completed("clip.mp4", bookmark), StartTicks = bookmark.StartTicks + TestData.OneSecond };

        Assert.True(BookmarkDtoMapper.ToDto(bookmark, null).Clip?.IsOutdated);
    }

    [Fact]
    public void ToDto_ProgressOnlyWhileRunning()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid());
        var running = bookmark with { Clip = new ClipInfo { Status = BookmarkClipStatus.Running } };
        var queued = bookmark with { Clip = new ClipInfo { Status = BookmarkClipStatus.Queued } };

        Assert.Equal(42.5, BookmarkDtoMapper.ToDto(running, 0.425).Clip?.Progress);
        Assert.Null(BookmarkDtoMapper.ToDto(queued, 0.425).Clip?.Progress);
    }

    [Fact]
    public void ToDto_NoFile_HasNoFileDetails()
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, FileSize = 5 } };

        var dto = BookmarkDtoMapper.ToDto(bookmark, null);

        Assert.Null(dto.Clip?.FileName);
        Assert.Null(dto.Clip?.FileSize);
        Assert.False(dto.Clip?.IsOutdated);
    }

    [Fact]
    public void ToDto_ItemTypeIsTheIdentitysKind_AndTheNameIsMapped()
    {
        // ItemType comes from Identity.Kind (null when unknown); the name is 1.1's new field.
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            Name = "Bank job",
            Identity = new IdentitySnapshot { Kind = "Episode", Path = @"Q:\TV\secret.mkv" }
        };

        var dto = BookmarkDtoMapper.ToDto(bookmark, null);

        Assert.Equal("Episode", dto.ItemType);
        Assert.Equal("Bank job", dto.Name);
        Assert.Null(BookmarkDtoMapper.ToDto(bookmark with { Identity = BookmarkRecordRules.NoIdentity }, null).ItemType);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(dto), StringComparison.Ordinal);
    }

    private static ClipInfo Completed(string path, Bookmark? source = null) => new()
    {
        Status = BookmarkClipStatus.Completed,
        FilePath = path,
        FileSize = 1234,
        FileStartTicks = source?.StartTicks ?? 60 * TestData.OneSecond,
        FileEndTicks = source?.EndTicks ?? 90 * TestData.OneSecond
    };
}
