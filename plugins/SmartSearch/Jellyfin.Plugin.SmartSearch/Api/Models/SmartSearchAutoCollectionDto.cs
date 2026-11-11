using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>An auto-collection: a Jellyfin collection whose members follow a saved query (or a copy of one).</summary>
public sealed class SmartSearchAutoCollectionDto
{
    /// <summary>Gets or sets its id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the collection's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection's id, once created.</summary>
    public Guid? CollectionId { get; set; }

    /// <summary>Gets or sets a value indicating whether the collection is kept up to date.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets what the members follow. The query itself is given only to a caller who may read it.</summary>
    public SmartSearchAutoCollectionSourceDto Source { get; set; } = new();

    /// <summary>Gets or sets whose watch state the query reads.</summary>
    public Guid? EvaluationUserId { get; set; }

    /// <summary>Gets or sets the evaluation user's name: <c>null</c> without one, or for a deleted user.</summary>
    public string? EvaluationUserName { get; set; }

    /// <summary>Gets or sets how the collection looks.</summary>
    public SmartSearchAutoCollectionPresentationDto Presentation { get; set; } = new();

    /// <summary>Gets or sets the manual changes (left out of a list).</summary>
    public SmartSearchAutoCollectionManualEditsDto? ManualEdits { get; set; }

    /// <summary>Gets or sets the safeguards.</summary>
    public SmartSearchAutoCollectionSafetyDto Safety { get; set; } = new();

    /// <summary>Gets or sets who created it: their libraries are its scope.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Gets or sets the creator's name: <c>null</c> for a deleted user, or for an API key.</summary>
    public string? CreatedByName { get; set; }

    /// <summary>Gets or sets when it was created.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets who changed it last.</summary>
    public Guid UpdatedByUserId { get; set; }

    /// <summary>Gets or sets when it was last changed (a change names it as <c>IfUpdatedAt</c>).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets its state: queued, running, or how its last run went.</summary>
    public SmartSearchAutoCollectionStateDto State { get; set; } = new();
}
