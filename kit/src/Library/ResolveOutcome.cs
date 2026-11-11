namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What a consumer does with an <see cref="IdentityResolver"/> result.
/// </summary>
internal enum ResolveOutcome
{
    /// <summary>
    /// No candidate that may be applied, and no Strong one: the record stays detached. Weak candidates, if any, are
    /// suggestions for an admin.
    /// </summary>
    Unmatched,

    /// <summary>
    /// Relink the record to <see cref="ResolveResult.ItemId"/>: an Exact match, or the single Strong candidate the
    /// consumer's <see cref="RelinkPolicy"/> allows and validates.
    /// </summary>
    Relink,

    /// <summary>
    /// Strong candidates exist but none is applied (<see cref="ResolveResult.Reason"/>): the record stays detached and is
    /// listed as ambiguous in Diagnostics, with the candidates.
    /// </summary>
    Ambiguous
}
