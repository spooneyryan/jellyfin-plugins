using System;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>One reason a collection name can't be used.</summary>
/// <param name="Kind">What is in the way.</param>
/// <param name="Id">The collection, item or auto-collection in the way, when there is one.</param>
/// <param name="Name">Its name, or the folder's; <c>null</c> when the caller can't see the collection in the way.</param>
internal sealed record CollectionNameConflict(CollectionNameConflictKind Kind, Guid? Id, string? Name);
