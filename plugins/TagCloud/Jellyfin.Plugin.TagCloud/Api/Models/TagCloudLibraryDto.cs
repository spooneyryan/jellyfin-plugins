using System;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// A library the caller can open that holds titles in the scope asked for, for the library picker.
/// </summary>
public sealed class TagCloudLibraryDto
{
    /// <summary>Gets or sets the library's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how many titles in the scope the caller can see in it.</summary>
    public int TitleCount { get; set; }
}
