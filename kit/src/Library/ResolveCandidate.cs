using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A library item that matches an <see cref="IdentitySnapshot"/>, for the consumer to apply or to list in
/// Diagnostics.
/// </summary>
/// <param name="Item">The item.</param>
/// <param name="Strength">How it matched.</param>
/// <param name="Source">Which step found it.</param>
/// <param name="PassesValidation">
/// For a Strong candidate: what the policy's validator said (true without a validator). Null for other strengths.
/// </param>
internal sealed record ResolveCandidate(BaseItem Item, MatchStrength Strength, MatchSource Source, bool? PassesValidation)
{
    /// <summary>
    /// Gets the candidate's item id.
    /// </summary>
    public Guid ItemId => Item.Id;
}
