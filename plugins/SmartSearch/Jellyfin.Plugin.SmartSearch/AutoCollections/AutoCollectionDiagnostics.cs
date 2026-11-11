using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The auto-collections' entry in the administrator's Diagnostics (<c>AutoCollectionRuns</c>): how many there are by
/// status, the held removals waiting for a manager, and the collections carrying a definition's mark more than once (a
/// restore or a copy of the collection folder): the oldest is used, the others are listed here and never deleted. Also
/// the collections carrying the mark of a definition that isn't there (a definitions file restored from before it was
/// made): ordinary collections now, which nothing updates (the first 100 by name).
/// </summary>
/// <remarks>Reads the stores without their locks and the library once (every collection, with its provider ids).</remarks>
internal sealed class AutoCollectionDiagnostics
{
    private const int MaxListed = 100;

    private readonly AutoCollectionStore _store;
    private readonly AutoCollectionStateStore _state;
    private readonly CollectionLocator _locator;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionDiagnostics"/> class.
    /// </summary>
    /// <param name="store">The definitions.</param>
    /// <param name="state">The run state.</param>
    /// <param name="locator">Finds the marked collections.</param>
    public AutoCollectionDiagnostics(AutoCollectionStore store, AutoCollectionStateStore state, CollectionLocator locator)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(locator);
        _store = store;
        _state = state;
        _locator = locator;
    }

    /// <summary>Describes the auto-collections now.</summary>
    /// <returns>Dictionaries, lists, strings, numbers and dates.</returns>
    public IDictionary<string, object?> Describe()
    {
        var definitions = _store.Current.Definitions.Where(d => d is not null).ToList();
        var states = definitions.ToDictionary(d => d.Id, d => _state.Find(d.Id));
        var marked = _locator.MarkedCollections();
        var duplicates = new List<IDictionary<string, object?>>();
        foreach (var definition in definitions)
        {
            if (!marked.TryGetValue(AutoCollectionDefinition.FormatTether(definition.Id), out var boxes) || boxes.Count < 2)
            {
                continue;
            }

            var used = boxes.FirstOrDefault(b => definition.CollectionId is { } id && b.Id.Equals(id))
                ?? boxes.OrderBy(b => b.DateCreated).ThenBy(b => b.Id).First();
            duplicates.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Id"] = definition.Id,
                ["Name"] = definition.Name,
                ["CollectionId"] = used.Id,
                ["Others"] = boxes.Where(b => !b.Id.Equals(used.Id)).Select(b => new Dictionary<string, object?>(StringComparer.Ordinal) { ["Id"] = b.Id, ["Name"] = b.Name }).ToList()
            });
        }

        var tethers = definitions.Select(d => AutoCollectionDefinition.FormatTether(d.Id)).ToHashSet(StringComparer.Ordinal);
        var unowned = marked.Where(p => !tethers.Contains(p.Key)).SelectMany(p => p.Value).ToList();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Count"] = definitions.Count,
            ["ByStatus"] = definitions
                .GroupBy(d => (states[d.Id]?.Status.Value ?? SmartSearchAutoCollectionStatus.NotCreated).ToString())
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (object?)g.Count(), StringComparer.Ordinal),
            ["PendingGuards"] = definitions
                .Where(d => states[d.Id]?.PendingGuard is not null)
                .Select(d =>
                {
                    var guard = states[d.Id]!.PendingGuard!;
                    return (IDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Id"] = d.Id,
                        ["Name"] = d.Name,
                        ["DetectedUtc"] = guard.DetectedUtc,
                        ["WouldRemove"] = guard.WouldRemoveCount,
                        ["Reasons"] = guard.Reasons,
                        ["Approved"] = guard.ApprovedUtc is not null
                    };
                })
                .ToList(),
            ["DuplicateMarkedCollections"] = duplicates,
            ["MarkedWithoutDefinition"] = unowned.Count,
            ["CollectionsWithoutDefinition"] = unowned
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Id)
                .Take(MaxListed)
                .Select(b => (IDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal) { ["Id"] = b.Id, ["Name"] = b.Name })
                .ToList(),
            ["UnprunableDeadLinks"] = states.Values.Sum(s => s?.UnprunableDeadLinks ?? 0)
        };
    }
}
