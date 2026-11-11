namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Why Strong candidates were not applied (<see cref="ResolveOutcome.Ambiguous"/>).
/// </summary>
internal enum AmbiguousReason
{
    /// <summary>Not ambiguous.</summary>
    None,

    /// <summary>More than one Strong candidate (for example two copies of a film with the same TMDb id).</summary>
    SeveralStrongCandidates,

    /// <summary>The consumer's policy applies Exact matches only (<see cref="RelinkPolicy.AllowStrongRelink"/> is false).</summary>
    StrongRelinkNotAllowed,

    /// <summary>The single Strong candidate failed the policy's validator (for example its runtime differs).</summary>
    FailedValidation,

    /// <summary>
    /// An Exact match (the same id, the journal's end, the same path) whose strong ids contradict the snapshot's,
    /// refused under <see cref="RelinkPolicy.RefuseConflictingExact"/>, and no Strong candidate elsewhere (another film
    /// at the same path gets the same id back).
    /// </summary>
    ConflictingProviderIds
}
