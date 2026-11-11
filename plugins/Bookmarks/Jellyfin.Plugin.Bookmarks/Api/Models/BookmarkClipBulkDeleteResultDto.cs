using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// What <c>POST /Bookmarks/Clips/BulkDelete</c> did with each id it was given: every distinct id is in
/// exactly one list, in the order of the request. The page keeps the ids of <see cref="InProgress"/>, <see cref="InUse"/>,
/// <see cref="FolderUnavailable"/> and <see cref="Failed"/> selected, since nothing of them changed.
/// </summary>
public sealed class BookmarkClipBulkDeleteResultDto
{
    /// <summary>
    /// Gets or sets the clips deleted while their bookmarks stay: the file deleted (or gone already, or none: a failed
    /// clip's), the bookmark without a clip.
    /// </summary>
    public IReadOnlyList<Guid> FilesDeleted { get; set; } = [];

    /// <summary>
    /// Gets or sets the clips of deleted bookmarks, gone with their files: nothing of those bookmarks is left.
    /// </summary>
    public IReadOnlyList<Guid> RecordsRemoved { get; set; } = [];

    /// <summary>
    /// Gets or sets the ids that are no clip of the caller: unknown ids, other users' clips, bookmarks without a clip,
    /// alike (no information leak).
    /// </summary>
    public IReadOnlyList<Guid> NotFound { get; set; } = [];

    /// <summary>
    /// Gets or sets the clips left unchanged because they were being made (a job waiting or running): their job and
    /// any earlier file stay. Cancel the job, or wait for it, first.
    /// </summary>
    public IReadOnlyList<Guid> InProgress { get; set; } = [];

    /// <summary>
    /// Gets or sets the clips left unchanged because their file was in use (being played or downloaded) after three attempts
    /// 200 ms apart.
    /// </summary>
    public IReadOnlyList<Guid> InUse { get; set; } = [];

    /// <summary>
    /// Gets or sets the clips left unchanged because their clip folder isn't available (an unplugged drive, a folder moved
    /// or removed): the file is probably fine there.
    /// </summary>
    public IReadOnlyList<Guid> FolderUnavailable { get; set; } = [];

    /// <summary>
    /// Gets or sets the clips left unchanged because their file couldn't be deleted otherwise: it isn't in this server's clip
    /// folders, its folder can't be read, or the file system refused. Nothing of them was touched.
    /// </summary>
    public IReadOnlyList<Guid> Failed { get; set; } = [];
}
