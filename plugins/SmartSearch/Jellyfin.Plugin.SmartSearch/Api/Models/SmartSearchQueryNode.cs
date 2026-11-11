using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// A node of the rule tree: a <b>group</b> has <see cref="Children"/> (with <see cref="Match"/> and
/// <see cref="Negate"/>), a <b>rule</b> has <see cref="Field"/> and <see cref="Op"/> and the values its operator reads.
/// Having both, or neither, is invalid (the validator says so).
/// </summary>
public sealed class SmartSearchQueryNode
{
    /// <summary>Gets or sets how a group combines its children; <c>null</c> is <see cref="SmartSearchMatchMode.All"/>.</summary>
    public SmartSearchMatchMode? Match { get; set; }

    /// <summary>Gets or sets a value indicating whether a group is negated ("None of").</summary>
    public bool Negate { get; set; }

    /// <summary>Gets or sets a group's children.</summary>
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "A query the JSON reader fills and the engine reads.")]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public List<SmartSearchQueryNode>? Children { get; set; }

    /// <summary>Gets or sets a rule's field id (or one of its aliases).</summary>
    public string? Field { get; set; }

    /// <summary>Gets or sets a rule's operator.</summary>
    public SmartSearchQueryOperator? Op { get; set; }

    /// <summary>Gets or sets a rule's one value: a string, number or bool.</summary>
    public JsonElement? Value { get; set; }

    /// <summary>Gets or sets a rule's values: strings, numbers, GUID strings or <c>{Id, Name}</c> references.</summary>
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "A query the JSON reader fills and the engine reads.")]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public List<JsonElement>? Values { get; set; }

    /// <summary>Gets or sets the lower bound of <c>Between</c>.</summary>
    public JsonElement? From { get; set; }

    /// <summary>Gets or sets the upper bound of <c>Between</c>.</summary>
    public JsonElement? To { get; set; }

    /// <summary>Gets or sets how many <see cref="Unit"/>s a relative date rule spans.</summary>
    public int? Amount { get; set; }

    /// <summary>Gets or sets the unit of a relative date rule.</summary>
    public SmartSearchRelativeUnit? Unit { get; set; }

    /// <summary>Gets or sets the person's role of a <c>People</c> rule; empty is any role.</summary>
    public string? Role { get; set; }

    /// <summary>Gets or sets the provider of an identity rule (<c>Tmdb</c>, <c>Imdb</c>, <c>Tvdb</c>).</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets a value indicating whether a <c>Title</c> rule also matches the original title.</summary>
    public bool? IncludeOriginalTitle { get; set; }

    /// <summary>Gets or sets the properties this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Gets a value indicating whether this node is a group (it has children).</summary>
    [JsonIgnore]
    public bool IsGroup => Children is not null;

    /// <summary>Creates an empty <c>All</c> group, the root of a query without rules.</summary>
    /// <returns>The group.</returns>
    public static SmartSearchQueryNode EmptyAll() => new() { Match = SmartSearchMatchMode.All, Children = [] };
}
