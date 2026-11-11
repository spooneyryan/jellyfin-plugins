using Jellyfin.Plugin.AccessRoles.Access;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What an admin's (or API key's) full-policy save of a user gets in its library fields (the user policy pre-merge):
/// what the next library sync run would write for a user whose native access is the posted one
/// (<see cref="LibrarySync.DecidePreMerge"/>).
/// </summary>
/// <param name="Plan">The run's plan for the user as posted (its <c>Native</c> is the posted access), or <c>null</c> when
/// nothing applies: not managed, in sync, a release, or the sync paused.</param>
/// <param name="Merge">The library fields to put into the post (the rules' result, or under <c>ReportOnly</c> the posted
/// access with the safe-direction removals), or <c>null</c> to leave the post's library fields as they are.</param>
/// <param name="Before">The library fields the server enforced before the save.</param>
/// <param name="Posted">The library fields the save posted.</param>
/// <param name="Why">Why nothing is merged, for the log: a pause reason, <c>NotManaged</c>, <c>InSync</c>, a skip's reason.</param>
internal sealed record PreMergeDecision(SyncUserResult? Plan, LibraryFields? Merge, LibraryFields Before, LibraryFields Posted, string? Why)
{
    /// <summary>Gets a value indicating whether the save changed the user's library access (it posted other fields than enforced).</summary>
    public bool Edited => !Posted.Same(Before);

    /// <summary>
    /// Builds the decision to leave a post alone.
    /// </summary>
    /// <param name="before">The library fields enforced before the save.</param>
    /// <param name="posted">The posted ones.</param>
    /// <param name="why">Why.</param>
    /// <returns>The decision.</returns>
    public static PreMergeDecision None(LibraryFields before, LibraryFields posted, string why) => new(null, null, before, posted, why);
}
