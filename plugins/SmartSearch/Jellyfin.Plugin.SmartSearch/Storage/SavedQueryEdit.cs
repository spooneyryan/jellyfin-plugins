using System;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>An edit of a saved query, for <see cref="ISavedQueryReferenceGuard.UpdateAsync"/>.</summary>
/// <param name="Current">The stored query before the edit.</param>
/// <param name="Query">The edited query, valid for the caller.</param>
/// <param name="UsesPerUserFields">Whether the edited query reads the user's own data.</param>
/// <param name="Caller">Who edits: the owner or an administrator.</param>
/// <param name="CallerIsAdmin">Whether the caller passes Jellyfin's <c>RequiresElevation</c> on this request.</param>
/// <param name="AcknowledgeWarnings">Whether the caller confirmed the warnings of an earlier attempt.</param>
internal sealed record SavedQueryEdit(
    SavedQueryRecord Current,
    SmartSearchQueryDefinition Query,
    bool UsesPerUserFields,
    User Caller,
    bool CallerIsAdmin,
    bool AcknowledgeWarnings)
{
    /// <summary>Gets the edited query as it will be stored; <c>Undefined</c> when not given (then it is written from <see cref="Query"/>).</summary>
    public JsonElement QueryJson { get; init; }

    /// <summary>Gets the version the caller read (<c>IfUpdatedAt</c>), checked again before references are detached.</summary>
    public DateTime? IfUpdatedAt { get; init; }

    /// <summary>
    /// Gets a value indicating whether the references are detached first (each keeps a copy of the query as it is
    /// stored now), so the edit no longer touches them.
    /// </summary>
    public bool DetachReferences { get; init; }

    /// <summary>
    /// Gets the save's own checks (its name, the version read), run on the stored document under the guard's lock just
    /// before references are detached, so a save they would refuse detaches nothing; they throw the save's refusal.
    /// </summary>
    public Action<SavedQueriesDocument>? CheckSave { get; init; }
}
