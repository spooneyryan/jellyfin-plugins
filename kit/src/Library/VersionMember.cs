using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What <see cref="VersionSets"/> needs of a video: its id, the <c>PrimaryVersionId</c> it
/// points at and the linked alternate versions it lists, as Jellyfin 10.11.5 stores them (<c>Video.cs:43-49</c>). A
/// projection, so an index can drop its <c>BaseItem</c>s.
/// </summary>
internal sealed record VersionMember
{
    /// <summary>
    /// Gets the item id.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the stored <c>PrimaryVersionId</c> text: empty or null for a primary or a standalone video, otherwise the id of
    /// the primary it is a version of (Jellyfin writes it in the "N" format, <c>VideosController.cs:220</c>). Text that
    /// isn't a GUID names no item, like a primary that was deleted.
    /// </summary>
    public string? PrimaryVersionId { get; init; }

    /// <summary>
    /// Gets the linked alternate versions the video lists as a primary.
    /// </summary>
    public IReadOnlyList<VersionLink> LinkedAlternateVersions { get; init; } = [];

    /// <summary>
    /// Gets the video's path: a relative link path is resolved against its folder, as Jellyfin does
    /// (<c>BaseItem.FindLinkedChild</c>).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the item's sort name, the tie-breaker <see cref="VersionSets.Collapse"/> uses after the caller's order.
    /// </summary>
    public string? SortName { get; init; }

    /// <summary>
    /// Gets the parsed <see cref="PrimaryVersionId"/>: null when it is empty, <see cref="Guid.Empty"/> when it is text that
    /// names no item (not a GUID, or the empty GUID).
    /// </summary>
    public Guid? PointsAt => string.IsNullOrWhiteSpace(PrimaryVersionId)
        ? null
        : Guid.TryParse(PrimaryVersionId, out var id) ? id : Guid.Empty;

    /// <summary>
    /// Projects a library item: a <see cref="Video"/>'s version fields; any other item is a standalone member.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The member.</returns>
    public static VersionMember From(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var video = item as Video;
        return new VersionMember
        {
            Id = item.Id,
            PrimaryVersionId = video?.PrimaryVersionId,
            LinkedAlternateVersions = video?.LinkedAlternateVersions is { Length: > 0 } links
                ? links.Where(l => l is not null).Select(l => new VersionLink(l.ItemId, l.Path, l.LibraryItemId)).ToList()
                : [],
            Path = item.Path,
            SortName = item.SortName
        };
    }

    /// <summary>
    /// Creates a member for a test or an index: a primary id given as a GUID is written the way Jellyfin writes it.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="primary">The primary it is a version of, or null.</param>
    /// <param name="links">The ids it lists as a primary.</param>
    /// <returns>The member.</returns>
    public static VersionMember Of(Guid id, Guid? primary = null, params Guid[] links) => new()
    {
        Id = id,
        PrimaryVersionId = primary?.ToString("N", CultureInfo.InvariantCulture),
        LinkedAlternateVersions = links.Select(l => new VersionLink(l, null)).ToList()
    };
}
