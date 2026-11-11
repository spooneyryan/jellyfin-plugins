using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// One option a field takes: whether a rule must set it, and its values when it is a string from a set.
/// </summary>
/// <param name="Option">The option.</param>
/// <param name="Required">Whether every rule on the field sets it.</param>
/// <param name="Choices">Its values, for <see cref="FieldOption.Role"/> and <see cref="FieldOption.Provider"/>; <c>null</c> for a bool.</param>
internal sealed record FieldOptionSpec(FieldOption Option, bool Required, IReadOnlyList<string>? Choices);
