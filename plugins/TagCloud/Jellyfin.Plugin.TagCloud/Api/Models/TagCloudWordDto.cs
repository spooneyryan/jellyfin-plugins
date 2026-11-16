using System;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// One word of a cloud: its key, the text shown and how many of the caller's titles carry it.
/// </summary>
public sealed class TagCloudWordDto
{
    /// <summary>
    /// Gets or sets the word's key: its text in lower case, which narrowing (<c>narrow=</c>) and the titles list
    /// (<c>key=</c>) take.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the word as shown: the casing most titles use.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets how many titles carry it.</summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the person's item id, for a word of Actors, Directors or Writers whose person the library has; null
    /// otherwise. The web client shows their photo when the pointer rests on the word.
    /// </summary>
    public Guid? PersonId { get; set; }

    /// <summary>Gets or sets the tag of the person's primary image (their photo), or null when there is none.</summary>
    public string? ImageTag { get; set; }
}
