using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;

namespace Jellyfin.Plugin.SmartSearch.Search;

/// <summary>
/// The <c>Series</c> rule's picker: series whose name contains the typed text, ignoring case and accents, from the
/// user's own view: per library the user can open (box set libraries aside), the series the value index holds for the
/// library's folders and the user's parental profile (their access query and <c>IsVisible(user)</c>). User-scoped by
/// construction, so a series of a hidden library, above the limit or carrying a blocked tag is never offered.
/// </summary>
/// <remarks>
/// The name is matched in memory, not with SQL's <c>NameContains</c>, which compares a stored clean name that a database
/// carried over from an old version may hold in an older form (and widens on <c>%</c> and <c>_</c>). The series come from
/// the value index, so a keystroke reads no library: the index is cached per folder and profile, built under its slot and
/// timeout rules, and goes stale with it.
/// </remarks>
internal sealed class SeriesTypeahead
{
    private readonly ValueIndexService _index;
    private readonly LibraryAccess _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesTypeahead"/> class.
    /// </summary>
    /// <param name="index">The value index.</param>
    /// <param name="access">The kit's library access.</param>
    public SeriesTypeahead(ValueIndexService index, LibraryAccess access)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(access);
        _index = index;
        _access = access;
    }

    /// <summary>Finds series for a user.</summary>
    /// <param name="user">The user.</param>
    /// <param name="text">The text the name contains (already checked for length).</param>
    /// <param name="limit">The most series.</param>
    /// <param name="cancellationToken">The request's token, for its waits.</param>
    /// <returns>Those whose name starts with the text first, then by sort name, name and id; each series once.</returns>
    /// <exception cref="Query.Execution.SmartSearchRefusedException">The index couldn't be built in time (<c>Busy</c>).</exception>
    public async Task<IReadOnlyList<SeriesSuggestion>> FindAsync(User user, string text, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(text);
        var folded = RuleValues.Fold(text.Trim());
        var found = new Dictionary<Guid, (IndexedSeries Series, Guid LibraryId, bool Starts)>();
        foreach (var library in _access.VisibleLibraries(user).Where(l => l.CollectionType != CollectionType.boxsets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = LibraryAccess.TopParentIds([library]);
            if (folders.Length == 0)
            {
                continue;
            }

            foreach (var series in await _index.SeriesAsync(user, folders, cancellationToken).ConfigureAwait(false))
            {
                if (!found.ContainsKey(series.Id) && series.Folded.Contains(folded, StringComparison.Ordinal))
                {
                    found[series.Id] = (series, library.Id, series.Folded.StartsWith(folded, StringComparison.Ordinal));
                }
            }
        }

        return found.Values
            .OrderByDescending(s => s.Starts)
            .ThenBy(s => s.Series.SortName ?? s.Series.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Series.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Series.Id)
            .Take(limit)
            .Select(s => new SeriesSuggestion(s.Series, s.LibraryId))
            .ToList();
    }
}
