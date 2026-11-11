using System.Collections.Generic;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// A page of a word's titles.
/// </summary>
public sealed class TagCloudTitlesDto
{
    /// <summary>Gets or sets the titles of this page.</summary>
    public IReadOnlyList<TagCloudTitleDto> Items { get; set; } = [];

    /// <summary>Gets or sets how many titles carry the word in all.</summary>
    public int Total { get; set; }
}
