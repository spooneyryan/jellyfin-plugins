using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>One reason a collection name can't be used.</summary>
public sealed class SmartSearchAutoCollectionNameConflictDto
{
    /// <summary>
    /// Gets or sets what is in the way: <c>ExistingCollection</c> (a collection of this name, ignoring case, or on its
    /// folder), <c>FolderExists</c> (the folder exists with no collection), <c>ItemIdExists</c> (the new collection's id
    /// is taken) or <c>AutoCollection</c> (another auto-collection, not created yet, has the name).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection or item in the way, when there is one.</summary>
    public Guid? CollectionId { get; set; }

    /// <summary>Gets or sets the auto-collection in the way, for <c>AutoCollection</c>.</summary>
    public Guid? DefinitionId { get; set; }

    /// <summary>Gets or sets the name of what is in the way (the folder's, for <c>FolderExists</c>); <c>null</c> for a collection the caller can't see.</summary>
    public string? Name { get; set; }
}
