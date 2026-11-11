using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// "None of the item's values is one of these" (<c>ExcludeTags</c>, compared by clean form): under <c>All</c> the union,
/// under <c>Any</c> the intersection (nothing in common: no constraint).
/// </summary>
internal sealed class ExcludeSetConstraint : PushConstraint
{
    private readonly HashSet<string> _set;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExcludeSetConstraint"/> class.
    /// </summary>
    /// <param name="values">The values, in order.</param>
    public ExcludeSetConstraint(IEnumerable<string> values)
    {
        _set = new HashSet<string>(FoldComparer.Instance);
        Values = values.Where(_set.Add).ToList();
    }

    /// <summary>Gets the values, in order.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <inheritdoc />
    public override PushConstraint And(PushConstraint other)
        => new ExcludeSetConstraint(Values.Concat(((ExcludeSetConstraint)other).Values));

    /// <inheritdoc />
    public override PushConstraint? Or(PushConstraint other)
    {
        var that = (ExcludeSetConstraint)other;
        var common = Values.Where(that._set.Contains).ToList();
        return common.Count == 0 ? null : new ExcludeSetConstraint(common);
    }

    /// <inheritdoc />
    public override bool Implies(PushConstraint other) => ((ExcludeSetConstraint)other).Values.All(_set.Contains);
}
