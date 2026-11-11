using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>What a collection's page says about the auto-collection that fills it.</summary>
public sealed class SmartSearchAutoCollectionNoteDto
{
    /// <summary>Gets or sets the auto-collection's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets when it last updated the collection successfully; none before its first update.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller manages collections, so may edit and refresh it.</summary>
    public bool CanManage { get; set; }

    /// <summary>Gets or sets the auto-collection's id: only for a caller who manages collections.</summary>
    public Guid? DefinitionId { get; set; }
}
