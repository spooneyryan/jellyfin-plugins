using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// A saved cloud as the caller sees it. It keeps a configuration, never counts: opening it counts again,
/// as the viewer.
/// </summary>
public sealed class TagCloudSavedCloudDto
{
    /// <summary>Gets or sets the cloud's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the cloud's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it is shared with everyone.</summary>
    public bool Shared { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller owns it (and so may change and delete it).</summary>
    public bool IsMine { get; set; }

    /// <summary>Gets or sets the owner's name, "Former user" for a deleted one.</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was saved first.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets when it was last saved.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets what it counts.</summary>
    public TagCloudSource Source { get; set; }

    /// <summary>Gets or sets which titles it counts over.</summary>
    public TagCloudScope Scope { get; set; }

    /// <summary>Gets or sets the libraries it counts over; empty for every library.</summary>
    public IReadOnlyList<Guid> LibraryIds { get; set; } = [];

    /// <summary>Gets or sets the words it is narrowed to.</summary>
    public IReadOnlyList<string> NarrowKeys { get; set; } = [];

    /// <summary>Gets or sets how it looks.</summary>
    public TagCloudLookDto Look { get; set; } = new();
}
