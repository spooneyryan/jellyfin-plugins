using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The body of a new auto-collection (<c>POST /SmartSearch/AutoCollections</c>) or of a change (<c>PUT …/{id}</c>).
/// </summary>
public sealed class SmartSearchAutoCollectionRequestDto
{
    /// <summary>
    /// Gets or sets the auto-collection. A new one needs <c>Name</c> and <c>Source</c>; on a change, what is left out stays
    /// as it was.
    /// </summary>
    public SmartSearchAutoCollectionDefinitionDto? Definition { get; set; }

    /// <summary>
    /// Gets or sets the <c>PreviewHash</c> of the preview the caller accepted: required for a new one (refused with 409
    /// <c>PreviewStale</c> when the members now differ from it by more than 5%), optional on a change.
    /// </summary>
    public string? AcceptedPreviewHash { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a referenced saved query that another user shared is first copied into the
    /// caller's own queries, and the copy is followed. Without it, only the caller's own queries can be referenced.
    /// </summary>
    public bool? DuplicateSourceQuery { get; set; }

    /// <summary>Gets or sets the <c>UpdatedUtc</c> the caller read: required on a change.</summary>
    public DateTime? IfUpdatedAt { get; set; }
}
