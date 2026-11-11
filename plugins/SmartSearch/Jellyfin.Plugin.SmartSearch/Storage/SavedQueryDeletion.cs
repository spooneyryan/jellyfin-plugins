using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>A deletion of a saved query, for <see cref="ISavedQueryReferenceGuard.DeleteAsync"/>.</summary>
/// <param name="Current">The stored query.</param>
/// <param name="Caller">Who deletes: the owner or an administrator.</param>
/// <param name="CallerIsAdmin">Whether the caller passes <c>RequiresElevation</c> on this request.</param>
/// <param name="DetachReferences">Whether references are detached (they keep a copy of the query) rather than refusing.</param>
internal sealed record SavedQueryDeletion(SavedQueryRecord Current, User Caller, bool CallerIsAdmin, bool DetachReferences);
