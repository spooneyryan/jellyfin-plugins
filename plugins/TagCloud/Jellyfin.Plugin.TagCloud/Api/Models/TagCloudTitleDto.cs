using System;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// A title carrying a word: what the titles card and the held word's cloud show.
/// </summary>
public sealed class TagCloudTitleDto
{
    /// <summary>Gets or sets the title's item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the title's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the title's production year, or null.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the title's kind: <c>Movie</c> or <c>Series</c>.</summary>
    public string Kind { get; set; } = string.Empty;
}
