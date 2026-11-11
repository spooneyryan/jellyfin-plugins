using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The two native policy fields Access Roles owns for a managed user: <c>EnableAllFolders</c>
/// and <c>EnabledFolders</c>. <c>PolicyWriter</c> writes nothing else.
/// </summary>
/// <param name="EnableAllFolders">Whether every library is granted, future ones included.</param>
/// <param name="EnabledFolders">The granted libraries when <paramref name="EnableAllFolders"/> is off (empty when on).</param>
internal sealed record LibraryFields(bool EnableAllFolders, IReadOnlyList<Guid> EnabledFolders)
{
    /// <summary>Gets every library, future ones included: what the stock page writes for "all libraries".</summary>
    public static LibraryFields All { get; } = new(true, []);

    /// <summary>
    /// Builds the fields, without repeated ids, and with no list when every library is granted.
    /// </summary>
    /// <param name="enableAllFolders">Whether every library is granted.</param>
    /// <param name="enabledFolders">The granted libraries otherwise.</param>
    /// <returns>The fields.</returns>
    public static LibraryFields Of(bool enableAllFolders, IEnumerable<Guid> enabledFolders)
    {
        ArgumentNullException.ThrowIfNull(enabledFolders);
        return new LibraryFields(enableAllFolders, enableAllFolders ? [] : [.. enabledFolders.Distinct()]);
    }

    /// <summary>
    /// Gets whether a write of these fields would change nothing on a policy with the other fields: both every library, or
    /// both the same set (the order of <c>EnabledFolders</c> doesn't matter to the core).
    /// </summary>
    /// <param name="enableAllFolders">The other's <c>EnableAllFolders</c>.</param>
    /// <param name="enabledFolders">The other's <c>EnabledFolders</c>.</param>
    /// <returns><c>true</c> when they are the same access.</returns>
    public bool Same(bool enableAllFolders, IEnumerable<Guid> enabledFolders)
    {
        ArgumentNullException.ThrowIfNull(enabledFolders);
        return enableAllFolders == EnableAllFolders && (enableAllFolders || enabledFolders.ToHashSet().SetEquals(EnabledFolders));
    }

    /// <summary>
    /// Gets whether two sets of fields are the same access.
    /// </summary>
    /// <param name="other">The other fields.</param>
    /// <returns><c>true</c> when they are the same access.</returns>
    public bool Same(LibraryFields other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Same(other.EnableAllFolders, other.EnabledFolders);
    }

    /// <summary>Describes the fields for a log line: <c>all</c>, or the number of libraries.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
        => EnableAllFolders ? "every library" : EnabledFolders.Count.ToString(CultureInfo.InvariantCulture) + " libraries";
}
