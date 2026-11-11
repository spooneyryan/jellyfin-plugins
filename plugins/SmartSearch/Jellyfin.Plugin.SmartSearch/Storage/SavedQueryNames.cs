using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// The rules for saved queries' names and descriptions: a name is 1 to 100 characters, trimmed, and unique among its
/// owner's queries ignoring case; a description is plain text of at most 500 characters.
/// </summary>
internal static class SavedQueryNames
{
    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest description.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>The name an imported query without a usable one gets.</summary>
    public const string ImportedName = "Imported query";

    /// <summary>Gets the comparer of names: ignoring case, culture-invariant.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Returns a free name: <paramref name="name"/> itself, or with " (2)", " (3)" and so on, shortened to fit.
    /// </summary>
    /// <param name="name">The wanted name, already trimmed and at most <see cref="MaxNameLength"/>.</param>
    /// <param name="taken">The owner's names.</param>
    /// <returns>The name.</returns>
    public static string Free(string name, ICollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var suffix = " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
            var candidate = name[..Math.Min(name.Length, MaxNameLength - suffix.Length)].TrimEnd() + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Makes every owner's names unique, in the order given: a name already used by an earlier query of the same owner gets
    /// a number (<see cref="Free"/>).
    /// </summary>
    /// <param name="queries">The queries.</param>
    /// <returns>The queries, renamed where needed.</returns>
    public static List<SavedQueryRecord> MakeUnique(IEnumerable<SavedQueryRecord> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var list = queries.ToList();
        var taken = new Dictionary<Guid, HashSet<string>>();
        foreach (var query in list)
        {
            if (!taken.TryGetValue(query.OwnerUserId, out var names))
            {
                taken[query.OwnerUserId] = names = new HashSet<string>(Comparer);
            }

            names.Add(query.Name);
        }

        // Each owner's names counted once: the second of two equal names (by creation) is the one renamed.
        var seen = new Dictionary<Guid, HashSet<string>>();
        for (var i = 0; i < list.Count; i++)
        {
            var query = list[i];
            if (!seen.TryGetValue(query.OwnerUserId, out var used))
            {
                seen[query.OwnerUserId] = used = new HashSet<string>(Comparer);
            }

            if (used.Add(query.Name))
            {
                continue;
            }

            var name = Free(query.Name, taken[query.OwnerUserId]);
            taken[query.OwnerUserId].Add(name);
            used.Add(name);
            list[i] = query with { Name = name };
        }

        return list;
    }

    /// <summary>Cleans an imported name: trimmed, cut to <see cref="MaxNameLength"/>, <see cref="ImportedName"/> when empty.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The name to store.</returns>
    public static string Clean(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length == 0 ? ImportedName : trimmed[..Math.Min(trimmed.Length, MaxNameLength)].TrimEnd();
    }

    /// <summary>Cleans an imported description: cut to <see cref="MaxDescriptionLength"/>.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The description to store.</returns>
    public static string CleanDescription(string? description)
    {
        var text = description ?? string.Empty;
        return text.Length <= MaxDescriptionLength ? text : text[..MaxDescriptionLength];
    }
}
