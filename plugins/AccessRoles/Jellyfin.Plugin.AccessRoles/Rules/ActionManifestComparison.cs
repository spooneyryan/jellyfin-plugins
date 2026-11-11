using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// How a set of actions differs from the <see cref="ActionManifest"/> (for Diagnostics, the <c>RuleTableSelfCheck</c> and the manifest test).
/// </summary>
/// <param name="Compared">How many distinct actions were compared.</param>
/// <param name="Missing">Manifest actions the set doesn't have: renamed or removed, so a rule may no longer bind.</param>
/// <param name="Unreviewed">Actions of the set the manifest doesn't list: new, not yet reviewed.</param>
/// <param name="Changed">Actions whose routes, policies, <c>[AllowAnonymous]</c> or parameters differ, each with how.</param>
/// <param name="Duplicates">Keys the set gave twice (two methods with one action name), which the manifest can't tell apart.</param>
internal sealed record ActionManifestComparison(int Compared, IReadOnlyList<string> Missing, IReadOnlyList<string> Unreviewed, IReadOnlyList<string> Changed, IReadOnlyList<string> Duplicates)
{
    /// <summary>Gets a value indicating whether the set and the manifest agree.</summary>
    public bool Matches => Missing.Count == 0 && Unreviewed.Count == 0 && Changed.Count == 0 && Duplicates.Count == 0;
}
