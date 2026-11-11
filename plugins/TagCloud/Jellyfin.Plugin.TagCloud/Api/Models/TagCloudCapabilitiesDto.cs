using System.Collections.Generic;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// What this server's Tag Cloud offers the caller, read once by the page.
/// </summary>
public sealed class TagCloudCapabilitiesDto
{
    /// <summary>Gets or sets the sources the admin left on, in the toolbar's order.</summary>
    public IReadOnlyList<TagCloudSource> Sources { get; set; } = [];

    /// <summary>Gets or sets the words slider's top.</summary>
    public int MaxWordsLimit { get; set; }

    /// <summary>Gets or sets the words slider's step and bottom.</summary>
    public int WordStep { get; set; }

    /// <summary>Gets or sets how many words a cloud starts with on the TV layout.</summary>
    public int TvMaxWordsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether clouds may be shared.</summary>
    public bool AllowSharedClouds { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller is an administrator (Recount, deleting shared clouds).</summary>
    public bool IsAdmin { get; set; }
}
