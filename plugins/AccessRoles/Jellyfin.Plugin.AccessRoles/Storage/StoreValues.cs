using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// How the store's records read collections: a missing or <c>null</c> collection is empty and <c>null</c> entries are
/// dropped, so code never meets a null (the rule Bookmarks' store follows too).
/// </summary>
internal static class StoreValues
{
    /// <summary>
    /// Gets a list of records without <c>null</c> entries; empty for <c>null</c>.
    /// </summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="value">The list as read or set.</param>
    /// <returns>The list; the same instance when nothing needed dropping.</returns>
    public static IReadOnlyList<T> Records<T>(IReadOnlyList<T>? value)
        where T : class
        => value is null ? [] : value.Any(v => v is null) ? value.Where(v => v is not null).ToList() : value;

    /// <summary>
    /// Gets a list of strings without <c>null</c> entries; empty for <c>null</c>.
    /// </summary>
    /// <param name="value">The list as read or set.</param>
    /// <returns>The list; the same instance when nothing needed dropping.</returns>
    public static IReadOnlyList<string> Strings(IReadOnlyList<string>? value)
        => value is null ? [] : value.Any(v => v is null) ? value.Where(v => v is not null).ToList() : value;

    /// <summary>
    /// Gets a list of values; empty for <c>null</c>.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The list as read or set.</param>
    /// <returns>The list.</returns>
    public static IReadOnlyList<T> Values<T>(IReadOnlyList<T>? value)
        where T : struct
        => value ?? [];

    /// <summary>
    /// Gets a map; empty for <c>null</c>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="value">The map as read or set.</param>
    /// <returns>The map.</returns>
    public static IReadOnlyDictionary<TKey, TValue> Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? value)
        where TKey : notnull
        => value ?? ImmutableDictionary<TKey, TValue>.Empty;

    /// <summary>
    /// Reads a stored time as UTC: a time without a kind (a hand edit without <c>Z</c>) is taken as UTC, and a local one
    /// converted.
    /// </summary>
    /// <param name="value">The time as read.</param>
    /// <returns>The UTC time.</returns>
    public static DateTime Utc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
