using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>One row of core's <c>ItemValues</c> table, as SQL matches it and as items carry it.</summary>
/// <param name="Type">The value's kind (<see cref="ItemValueType.Genre"/>, <see cref="ItemValueType.Studios"/> or <see cref="ItemValueType.Tags"/>).</param>
/// <param name="Value">The stored value, as items list it (the item's column joins its values with <c>|</c>).</param>
/// <param name="CleanValue">The stored clean form SQL compares with, written when the row was created.</param>
internal sealed record ItemValueRow(ItemValueType Type, string Value, string? CleanValue);
