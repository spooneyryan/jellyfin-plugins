using System;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Maps stored bookmarks to API models. Called only inside the services, so controllers receive DTOs.
/// </summary>
internal static class BookmarkDtoMapper
{
    /// <summary>
    /// Maps a bookmark. The identity snapshot's path and the clip file's folder never reach a DTO.
    /// </summary>
    /// <param name="bookmark">The bookmark.</param>
    /// <param name="clipProgress">Progress (0 to 1) of the bookmark's running clip job, if any.</param>
    /// <param name="itemAvailable">Whether the viewer can open the video now, which only the queries of the My Bookmarks page
    /// work out (<c>BookmarkQueryService</c>); <c>null</c> everywhere else.</param>
    /// <returns>The API model.</returns>
    public static BookmarkDto ToDto(Bookmark bookmark, double? clipProgress, bool? itemAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(bookmark);

        var identity = bookmark.Identity;
        return new BookmarkDto
        {
            Id = bookmark.Id,
            ItemId = bookmark.ItemId,
            ItemName = bookmark.ItemName,
            ItemType = string.IsNullOrEmpty(identity.Kind) ? null : identity.Kind,

            // What the bookmark stored about its video, never the library's item: a video the viewer can no longer open
            // tells nothing new (it shows as "Unavailable").
            Year = identity.Year,
            SeriesName = string.IsNullOrWhiteSpace(identity.SeriesName) ? null : identity.SeriesName,
            SeasonNumber = identity.Season,
            EpisodeNumber = identity.Episode,
            MediaSourceId = bookmark.MediaSourceId,
            AudioStreamIndex = bookmark.AudioStreamIndex,
            StartTicks = bookmark.StartTicks,
            EndTicks = bookmark.EndTicks,
            Name = bookmark.Name,
            Note = bookmark.Note,
            CreatedAt = bookmark.CreatedAt,
            UpdatedAt = bookmark.UpdatedAt,
            Clip = bookmark.Clip is null ? null : ToDto(bookmark, bookmark.Clip, clipProgress),
            Detached = bookmark.DetachedUtc is not null,
            Relinked = bookmark.RelinkedUtc is not null,
            ItemAvailable = itemAvailable,
            Origin = bookmark.Origin
        };
    }

    /// <summary>
    /// Maps a record's clip for the My Clips page: the bookmark's own fields, what it stored about its
    /// video, and the clip with what the page works out about it. Neither the clip file's folder nor the video's path reach
    /// it (users get the file's name only).
    /// </summary>
    /// <param name="record">The record, a live bookmark or a clip-only tombstone, with its clip.</param>
    /// <param name="facts">What the clip is right now (<see cref="ClipRow.Facts"/>).</param>
    /// <param name="clipProgress">Progress (0 to 1) of the clip's running job, if any.</param>
    /// <param name="itemAvailable">Whether the viewer can open the video now.</param>
    /// <param name="inLibrary">Whether the clip file lies in a folder of a Jellyfin library.</param>
    /// <param name="libraryName">That library's name, when the viewer may know it; else <c>null</c>.</param>
    /// <returns>The API model.</returns>
    public static BookmarkClipItemDto ToClipItemDto(Bookmark record, ClipFacts facts, double? clipProgress, bool itemAvailable, bool inLibrary, string? libraryName)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(facts);
        var clip = record.Clip ?? throw new ArgumentException("The record has no clip.", nameof(record));

        var dto = ToDto(record, clip, clipProgress);
        dto.IsOutdated = facts.IsOutdated;
        dto.State = facts.State;
        dto.Container = facts.Container;
        dto.DurationTicks = facts.DurationTicks;
        dto.FileExists = facts.FileExists;
        dto.IsNameOutdated = facts.IsNameOutdated;
        dto.LastJobFailed = facts.LastJobFailed;
        dto.InLibrary = inLibrary;
        dto.LibraryName = inLibrary ? libraryName : null;
        dto.RootAvailable = facts.RootAvailable;
        dto.MadeAt = facts.MadeAt;

        var identity = record.Identity;
        return new BookmarkClipItemDto
        {
            Id = record.Id,
            BookmarkDeleted = BookmarkRecordRules.IsTombstone(record),
            Origin = record.Origin,
            Name = record.Name,
            Note = record.Note,
            ItemId = record.ItemId,
            ItemName = record.ItemName,
            ItemType = string.IsNullOrEmpty(identity.Kind) ? null : identity.Kind,
            ItemAvailable = itemAvailable,
            MediaSourceId = record.MediaSourceId,
            AudioStreamIndex = record.AudioStreamIndex,
            BookmarkStartTicks = record.StartTicks,
            BookmarkEndTicks = record.EndTicks,
            Clip = dto,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };
    }

    /// <summary>
    /// Maps a record's clip for the admins' "All users" scope of the My Clips page: the clip as the
    /// owner's own list maps it (<see cref="ToClipItemDto"/>), with its owner, its file's full path and its clip folder's id,
    /// and <b>never the bookmark's note</b> (the admin DTO has no member for it).
    /// </summary>
    /// <param name="record">The record, a live bookmark or a clip-only tombstone, with its clip.</param>
    /// <param name="facts">What the clip is right now.</param>
    /// <param name="clipProgress">Progress (0 to 1) of the clip's running job, if any.</param>
    /// <param name="itemAvailable">Whether the caller (the admin) can open the video now.</param>
    /// <param name="inLibrary">Whether the clip file lies in a folder of a Jellyfin library.</param>
    /// <param name="libraryName">That library's name, when the caller may know it; else <c>null</c>.</param>
    /// <param name="userName">The owner's name now, or <c>null</c> when no user of this server has the id.</param>
    /// <returns>The API model.</returns>
    public static BookmarkAdminClipItemDto ToAdminClipItemDto(Bookmark record, ClipFacts facts, double? clipProgress, bool itemAvailable, bool inLibrary, string? libraryName, string? userName)
    {
        var item = ToClipItemDto(record, facts, clipProgress, itemAvailable, inLibrary, libraryName);
        return new BookmarkAdminClipItemDto
        {
            Id = item.Id,
            UserId = record.UserId,
            UserName = userName ?? DeletedUserName(record.UserId),
            UserDeleted = userName is null,
            BookmarkDeleted = item.BookmarkDeleted,
            Origin = item.Origin,
            Name = item.Name,
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            ItemType = item.ItemType,
            ItemAvailable = item.ItemAvailable,
            MediaSourceId = item.MediaSourceId,
            AudioStreamIndex = item.AudioStreamIndex,
            BookmarkStartTicks = item.BookmarkStartTicks,
            BookmarkEndTicks = item.BookmarkEndTicks,
            Clip = item.Clip,
            FilePath = string.IsNullOrEmpty(record.Clip!.FilePath) ? null : record.Clip.FilePath,
            RootId = facts.Check?.Root is { } root ? ClipRootPaths.RootId(root) : null,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    /// <summary>
    /// Gets the name the admins' answers give an owner no user of this server has: <c>Deleted user (&lt;id&gt;)</c>.
    /// </summary>
    /// <param name="userId">The owner's id.</param>
    /// <returns>The name.</returns>
    public static string DeletedUserName(Guid userId) => "Deleted user (" + userId.ToString("N", CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// Maps a record's clip for the My Clips page's poll of the clips being made (<c>Clips/Status</c>).
    /// </summary>
    /// <param name="record">The record, with its clip.</param>
    /// <param name="state">What the clip is right now (<see cref="ClipRow.State"/>).</param>
    /// <param name="clipProgress">Progress (0 to 1) of the clip's running job, if any.</param>
    /// <returns>The API model.</returns>
    public static BookmarkClipStatusDto ToClipStatusDto(Bookmark record, BookmarkClipState state, double? clipProgress)
    {
        ArgumentNullException.ThrowIfNull(record);
        var clip = record.Clip ?? throw new ArgumentException("The record has no clip.", nameof(record));
        var hasFile = clip.FilePath is not null;
        return new BookmarkClipStatusDto
        {
            Id = record.Id,
            Status = clip.Status,
            State = state,
            Progress = Progress(clip, clipProgress),
            Error = clip.Error,
            FileName = hasFile ? Path.GetFileName(clip.FilePath) : null,
            FileSize = hasFile ? clip.FileSize : null
        };
    }

    // A running job's progress as a percentage with one decimal; none for any other job.
    private static double? Progress(ClipInfo clip, double? progress)
        => clip.Status == BookmarkClipStatus.Running && progress is double value ? Math.Round(value * 100, 1) : null;

    private static BookmarkClipDto ToDto(Bookmark bookmark, ClipInfo clip, double? progress)
    {
        var hasFile = clip.FilePath is not null;
        return new BookmarkClipDto
        {
            Status = clip.Status,
            Mode = clip.Mode,
            StartTicks = clip.StartTicks,
            EndTicks = clip.EndTicks,
            Progress = Progress(clip, progress),
            RequestedAt = clip.RequestedAt,
            CompletedAt = clip.CompletedAt,
            Error = clip.Error,

            // The file can be from an earlier job. Only its name is exposed, not the server's directory layout.
            FileName = hasFile ? Path.GetFileName(clip.FilePath) : null,
            FileSize = hasFile ? clip.FileSize : null,
            IsOutdated = hasFile && clip.FileStartTicks is long fileStart
                && (fileStart != bookmark.StartTicks || clip.FileEndTicks != bookmark.EndTicks)
        };
    }
}
