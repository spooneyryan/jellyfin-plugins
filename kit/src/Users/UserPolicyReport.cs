using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// The answer of <c>POST /&lt;Route&gt;/Admin/Users/ApplyPolicy</c> (<c>{Affected}</c>).
/// </summary>
/// <param name="Policy">The policy applied.</param>
/// <param name="Affected">How many records were removed or changed.</param>
/// <param name="Users">Each id with its count.</param>
/// <param name="SafetySnapshots">The pre-operation snapshots taken first.</param>
internal sealed record UserPolicyReport(DeletedUserPolicy Policy, int Affected, IReadOnlyDictionary<Guid, int> Users, IReadOnlyList<string> SafetySnapshots)
{
    /// <summary>
    /// Describes the report with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Policy"] = Policy.ToString(),
        ["Affected"] = Affected,
        ["Users"] = Users.Select(u => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["UserId"] = KnownUsers.FormatId(u.Key),
            ["Affected"] = u.Value
        }).ToList(),
        ["SafetySnapshots"] = SafetySnapshots
    };
}
