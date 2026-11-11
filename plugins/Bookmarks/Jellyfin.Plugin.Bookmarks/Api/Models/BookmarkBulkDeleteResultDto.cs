using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// What <c>POST /Bookmarks/BulkDelete</c> did with each id it was given: every distinct id is in exactly one
/// list. The page keeps the ids of <see cref="InUse"/> and <see cref="Failed"/> selected, since nothing of them changed.
/// </summary>
public sealed class BookmarkBulkDeleteResultDto
{
    /// <summary>
    /// Gets or sets the bookmarks that are gone, with their clip files when <c>DeleteClips</c> was set.
    /// </summary>
    public IReadOnlyList<Guid> Deleted { get; set; } = [];

    /// <summary>
    /// Gets or sets the bookmarks deleted while their clip files were kept (without <c>DeleteClips</c>): clip-only
    /// tombstones, whose files still download and can be deleted by the bookmark's id.
    /// </summary>
    public IReadOnlyList<Guid> Tombstoned { get; set; } = [];

    /// <summary>
    /// Gets or sets the ids that are no bookmark of the caller: unknown ids, other users' bookmarks and bookmarks deleted
    /// already, alike, so no answer tells them apart.
    /// </summary>
    public IReadOnlyList<Guid> NotFound { get; set; } = [];

    /// <summary>
    /// Gets or sets the bookmarks left unchanged because their clip file was in use (being played or downloaded) after
    /// three attempts 200 ms apart, with <c>DeleteClips</c>.
    /// </summary>
    public IReadOnlyList<Guid> InUse { get; set; } = [];

    /// <summary>
    /// Gets or sets the bookmarks left unchanged because their clip file couldn't be deleted otherwise, with
    /// <c>DeleteClips</c>: its folder isn't available (an unplugged drive), it isn't in this server's clip folders, or the
    /// file system refused. Nothing of them was touched.
    /// </summary>
    public IReadOnlyList<Guid> Failed { get; set; } = [];
}
