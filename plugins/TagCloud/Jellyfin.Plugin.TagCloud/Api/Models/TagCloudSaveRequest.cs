using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// The body of <c>POST /TagCloud/Clouds</c> and <c>PUT /TagCloud/Clouds/{id}</c>: everything a saved cloud
/// keeps.
/// </summary>
public sealed class TagCloudSaveRequest
{
    /// <summary>Gets or sets the cloud's name: 1–80 characters, unique among the caller's own clouds, ignoring case.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether everyone may open it (only while the admin allows sharing).</summary>
    public bool Shared { get; set; }

    /// <summary>Gets or sets what it counts.</summary>
    public TagCloudSource Source { get; set; }

    /// <summary>Gets or sets which titles it counts over.</summary>
    public TagCloudScope Scope { get; set; }

    /// <summary>Gets or sets the libraries it counts over (at most 50); empty for every library.</summary>
    public IReadOnlyList<Guid> LibraryIds { get; set; } = [];

    /// <summary>Gets or sets the words it is narrowed to (at most 10 keys).</summary>
    public IReadOnlyList<string> NarrowKeys { get; set; } = [];

    /// <summary>Gets or sets how it looks.</summary>
    public TagCloudLookDto Look { get; set; } = new();

    /// <summary>
    /// Gets or sets, for an update, the <c>UpdatedUtc</c> the caller last read: a cloud changed since is a 409, so a save
    /// never overwrites a newer one unseen. Null updates whatever is there.
    /// </summary>
    public DateTime? IfUpdatedUtc { get; set; }
}
