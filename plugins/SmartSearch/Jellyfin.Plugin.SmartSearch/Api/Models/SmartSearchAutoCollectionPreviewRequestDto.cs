using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The body of <c>POST /SmartSearch/AutoCollections/Preview</c>: an unsaved auto-collection (<c>Definition</c>, the
/// caller's own), or a saved one (<c>DefinitionId</c>) with the changes in <c>Definition</c>.
/// </summary>
public sealed class SmartSearchAutoCollectionPreviewRequestDto
{
    /// <summary>Gets or sets the saved auto-collection to start from, or <c>null</c> for a new one.</summary>
    public Guid? DefinitionId { get; set; }

    /// <summary>Gets or sets the auto-collection (a new one needs <c>Source</c>), or the changes to a saved one.</summary>
    public SmartSearchAutoCollectionDefinitionDto? Definition { get; set; }
}
