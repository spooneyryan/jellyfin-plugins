using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// What an auto-collection would hold now. The members are worked out in the collection's own scope (its creator's
/// libraries); the samples show only items the caller may open, the others are counted in <see cref="HiddenFromCaller"/>.
/// </summary>
public sealed class SmartSearchAutoCollectionPreviewDto
{
    /// <summary>Gets or sets the hash of the members in order; a create can name it to confirm what was previewed.</summary>
    public string PreviewHash { get; set; } = string.Empty;

    /// <summary>Gets or sets how many members the collection would have.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets how many of them the collection doesn't hold yet.</summary>
    public int WouldAdd { get; set; }

    /// <summary>Gets or sets how many of its current members it would lose.</summary>
    public int WouldRemove { get; set; }

    /// <summary>Gets or sets up to 20 of the items it would gain, that the caller may open, as <c>/Items</c> returns them.</summary>
    public IReadOnlyList<BaseItemDto> AddSamples { get; set; } = [];

    /// <summary>Gets or sets up to 20 of the items it would lose, that the caller may open.</summary>
    public IReadOnlyList<BaseItemDto> RemoveSamples { get; set; } = [];

    /// <summary>Gets or sets how many of the items it would gain or lose the caller can't open (not shown).</summary>
    public int HiddenFromCaller { get; set; }

    /// <summary>Gets or sets how many pins were left out (gone, or outside the creator's libraries).</summary>
    public int PinsSkipped { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether some people who can see the collection can't open all of its items (they
    /// see only those they can).
    /// </summary>
    public bool MixedAudience { get; set; }

    /// <summary>Gets or sets whether the mass-removal guard would hold the removals back if the change ran now.</summary>
    public SmartSearchAutoCollectionGuardPreviewDto Guard { get; set; } = new();

    /// <summary>Gets or sets the warnings, in plain English.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>Gets or sets how long the preview took, in milliseconds.</summary>
    public long ElapsedMs { get; set; }
}
