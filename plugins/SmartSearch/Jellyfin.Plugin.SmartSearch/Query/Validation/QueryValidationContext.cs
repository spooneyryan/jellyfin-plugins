using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.SmartSearch.Query.Validation;

/// <summary>
/// Who validates and under which settings.
/// </summary>
/// <param name="IsAdmin">Whether the caller is an administrator (decided with <c>CurrentUser.IsElevatedAsync</c>).</param>
/// <param name="EnableRegex">The setting <c>EnableRegex</c>; off for now (<c>Matches</c> needs a query-level cancellation first).</param>
/// <param name="AllowedItemKinds">The setting <c>AllowedItemKinds</c>; only the kinds of <see cref="QueryLimits"/> count.</param>
internal sealed record QueryValidationContext(bool IsAdmin, bool EnableRegex, IReadOnlyCollection<BaseItemKind> AllowedItemKinds)
{
    /// <summary>Gets the defaults (a non-administrator, regex off, every kind).</summary>
    public static QueryValidationContext User { get; } = new(false, false, QueryLimits.ItemKinds);

    /// <summary>Gets the defaults for an administrator.</summary>
    public static QueryValidationContext Admin { get; } = new(true, false, QueryLimits.ItemKinds);
}
