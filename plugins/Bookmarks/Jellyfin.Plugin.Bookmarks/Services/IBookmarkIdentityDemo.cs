using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's item identity behind <see cref="Api.BookmarksIdentityDemoController"/>,
/// turned on by the setting <c>EnableIdentityDemo</c>. Public only so the public controller can take it;
/// its results are plain dictionaries, like the Diagnostics body.
/// </summary>
/// <remarks>
/// Every call takes the caller: ids resolve as that admin, and nothing names an item they can't see; null, for an
/// API key, works over the whole server.
/// </remarks>
public interface IBookmarkIdentityDemo
{
    /// <summary>
    /// Takes the identity snapshot of a library item, as a record would store it.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="caller">The signed-in admin, or null for an API key.</param>
    /// <returns><c>{Identity, StrongKeys}</c>.</returns>
    IDictionary<string, object?> Capture(Guid itemId, User? caller);

    /// <summary>
    /// Resolves a snapshot against the library and the move journal with one of the plans' relink policies, as a record of
    /// the caller's would be resolved.
    /// </summary>
    /// <param name="snapshot">The snapshot, in the store's JSON form (as <see cref="Capture"/> returns it).</param>
    /// <param name="policy"><c>Default</c>, <c>Bookmarks</c> (runtime within 2 %, at most 60 s) or <c>ExactOnly</c>.</param>
    /// <param name="attachedUtc">When the record was attached to the snapshot's item, or null for the whole journal.</param>
    /// <param name="caller">The signed-in admin, or null for an API key.</param>
    /// <returns>The result: outcome, strength, source, target, pending flag, reason, candidates and journal chain.</returns>
    IDictionary<string, object?> Resolve(JsonElement snapshot, string? policy, DateTime? attachedUtc, User? caller);

    /// <summary>
    /// Reads the move journal and works out a consumer's catch-up from a stored position.
    /// </summary>
    /// <param name="journalId">The stored <c>MoveJournalId</c>, or null.</param>
    /// <param name="lastMoveSeq">The stored <c>LastMoveSeq</c>.</param>
    /// <param name="caller">The signed-in admin, or null for an API key.</param>
    /// <returns>The journal's state, the catch-up and the pending relinks.</returns>
    IDictionary<string, object?> Journal(Guid? journalId, long lastMoveSeq, User? caller);
}
