using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// What the planner did with one rule, for tests, logs and the plan's explanation.
/// </summary>
/// <param name="Path">The rule's place in the query, such as <c>Where.Children[1].Children[0]</c>.</param>
/// <param name="Field">The field's id.</param>
/// <param name="Op">The operator.</param>
/// <param name="Mandatory">Whether the rule is reachable from the root through <c>All</c> groups that aren't negated.</param>
/// <param name="Exactness">How the rule's mapping relates to its meaning.</param>
/// <param name="Properties">
/// The <c>InternalItemsQuery</c> properties the mapping sets, empty when declined. Its id lists can hold ids the planner
/// resolved itself with a server-wide read (a series name's series ids, read from every episode in the user's folders,
/// including ones parental controls hide), so they stay in tests and logs at Debug; see <see cref="ForExplain"/>.
/// </param>
/// <param name="Reason">Why a rule is Superset or Declined, or <c>null</c>.</param>
/// <param name="LeftResidual">Whether the final query implies the rule, so it left the residual.</param>
internal sealed partial record PushdownStep(
    string Path,
    string Field,
    SmartSearchQueryOperator Op,
    bool Mandatory,
    PushdownExactness Exactness,
    string Properties,
    string? Reason,
    bool LeftResidual)
{
    /// <summary>
    /// Gets the step as the Explain output shows it: property names and counts, every
    /// id list replaced by its count, so a resolved id never names an item the caller can't see.
    /// </summary>
    /// <returns>The redacted step.</returns>
    public PushdownStep ForExplain()
        => this with { Properties = IdList().Replace(Properties, m => "[" + (m.Value.Count(c => c == ',') + 1).ToString(CultureInfo.InvariantCulture) + " ids]") };

    // An id list as PushdownSet prints one: [n-format guid,...].
    [GeneratedRegex(@"\[[0-9a-f]{32}(?:,[0-9a-f]{32})*\]", RegexOptions.CultureInvariant)]
    private static partial Regex IdList();
}
