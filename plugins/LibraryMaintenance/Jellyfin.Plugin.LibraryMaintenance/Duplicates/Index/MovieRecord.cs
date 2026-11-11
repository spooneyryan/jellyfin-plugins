using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// One indexed movie: what grouping, the cards and the suggested keep need, projected from the library item so
/// the index holds no <see cref="BaseItem"/>.
/// </summary>
internal sealed record MovieRecord
{
    /// <summary>Gets the item id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the item's <c>Name</c> (what the title key is made of).</summary>
    public required string Name { get; init; }

    /// <summary>Gets the item's <c>OriginalTitle</c>.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the item's <c>SortName</c>, the kit's tie-breaker.</summary>
    public string? SortName { get; init; }

    /// <summary>Gets the item's <c>ProductionYear</c>.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the year of the item's <c>PremiereDate</c>.</summary>
    public int? PremiereYear { get; init; }

    /// <summary>Gets the video's path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the folder the video is in (<c>ContainingFolderPath</c>).</summary>
    public string? ContainingFolder { get; init; }

    /// <summary>Gets a value indicating whether the video shares its folder with other movies.</summary>
    public bool IsInMixedFolder { get; init; }

    /// <summary>Gets the video type (a file, a disc folder, an ISO).</summary>
    public VideoType VideoType { get; init; }

    /// <summary>Gets a value indicating whether the video is 3D.</summary>
    public bool Is3D { get; init; }

    /// <summary>Gets a value indicating whether the item is a <c>.strm</c> shortcut.</summary>
    public bool IsShortcut { get; init; }

    /// <summary>Gets a value indicating whether the item is a disc placeholder.</summary>
    public bool IsPlaceHolder { get; init; }

    /// <summary>Gets the video's width, or null when unknown.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the video's height, or null when unknown.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the file size in bytes.</summary>
    public long? Size { get; init; }

    /// <summary>Gets the total bitrate.</summary>
    public int? TotalBitrate { get; init; }

    /// <summary>Gets the runtime in ticks.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the container.</summary>
    public string? Container { get; init; }

    /// <summary>Gets when the item was added.</summary>
    public DateTime DateCreated { get; init; }

    /// <summary>Gets the TMDb id (only TMDb and IMDb are kept).</summary>
    public string? Tmdb { get; init; }

    /// <summary>Gets the IMDb id.</summary>
    public string? Imdb { get; init; }

    /// <summary>Gets the stored <c>PrimaryVersionId</c>.</summary>
    public string? PrimaryVersionId { get; init; }

    /// <summary>Gets the item's <c>PresentationUniqueKey</c>.</summary>
    public string? PresentationUniqueKey { get; init; }

    /// <summary>Gets the linked alternate versions the item lists as a primary.</summary>
    public IReadOnlyList<VersionLink> Links { get; init; } = [];

    /// <summary>Gets the number of local alternate versions (owned items, not in the index).</summary>
    public int LocalAlternateCount { get; init; }

    /// <summary>Gets the number of parts (1 for a single file; stacked parts add theirs).</summary>
    public int PartCount { get; init; } = 1;

    /// <summary>Gets the libraries the item is in (one folder can be in two libraries).</summary>
    public IReadOnlyList<Guid> LibraryIds { get; init; } = [];

    /// <summary>Gets the record as the kit's version-set member.</summary>
    public VersionMember Version => new()
    {
        Id = Id,
        PrimaryVersionId = PrimaryVersionId,
        LinkedAlternateVersions = Links,
        Path = Path,
        SortName = SortName
    };

    /// <summary>
    /// Projects a video.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="libraryIds">The libraries the index found it in.</param>
    /// <returns>The record.</returns>
    public static MovieRecord From(Video video, IReadOnlyList<Guid> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(video);
        var member = VersionMember.From(video);
        return new MovieRecord
        {
            Id = video.Id,
            Name = video.Name ?? string.Empty,
            OriginalTitle = video.OriginalTitle,
            SortName = video.SortName,
            ProductionYear = video.ProductionYear,
            PremiereYear = video.PremiereDate?.Year,
            Path = video.Path ?? string.Empty,
            ContainingFolder = string.IsNullOrEmpty(video.Path) ? null : System.IO.Path.GetDirectoryName(video.Path),
            IsInMixedFolder = video.IsInMixedFolder,
            VideoType = video.VideoType,
            Is3D = video.Is3D,
            IsShortcut = video.IsShortcut,
            IsPlaceHolder = video.IsPlaceHolder,
            Width = video.Width > 0 ? video.Width : null,
            Height = video.Height > 0 ? video.Height : null,
            Size = video.Size,
            TotalBitrate = video.TotalBitrate,
            RunTimeTicks = video.RunTimeTicks,
            Container = video.Container,
            DateCreated = video.DateCreated,
            Tmdb = NullIfBlank(video.GetProviderId(MetadataProvider.Tmdb)),
            Imdb = NullIfBlank(video.GetProviderId(MetadataProvider.Imdb)),
            PrimaryVersionId = NullIfBlank(video.PrimaryVersionId),
            PresentationUniqueKey = video.PresentationUniqueKey,
            Links = member.LinkedAlternateVersions,
            LocalAlternateCount = video.LocalAlternateVersions?.Length ?? 0,
            PartCount = 1 + (video.AdditionalParts?.Length ?? 0),
            LibraryIds = libraryIds.ToArray()
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
