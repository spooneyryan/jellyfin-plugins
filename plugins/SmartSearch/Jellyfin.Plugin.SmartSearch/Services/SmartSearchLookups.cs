using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Search;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// <c>/Libraries</c>, <c>/Values/{fieldId}</c>, <c>/Series</c> and <c>/People</c>: the user's libraries (the kit's
/// <c>LibraryAccess</c>), the value index (<see cref="ValueIndexService"/>, per folder and parental profile), the series
/// picker (<see cref="SeriesTypeahead"/>, the user's own query) and the person picker (<see cref="PersonTypeahead"/>,
/// checked through the user's items). Each answers from what the user can see only.
/// </summary>
internal sealed class SmartSearchLookups : ISmartSearchLookups
{
    /// <summary>The path every field with a value index names as its <c>ValueSource</c>, before the field's id.</summary>
    public const string ValuesSource = "/SmartSearch/Values/";

    private readonly LibraryAccess _access;
    private readonly IFieldCatalog _catalog;
    private readonly ValueIndexService _values;
    private readonly SeriesTypeahead _series;
    private readonly PersonTypeahead _people;
    private readonly Func<IReadOnlyList<BaseItemKind>> _allowedKinds;
    private readonly Func<bool> _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchLookups"/> class.
    /// </summary>
    /// <param name="access">The kit's library access.</param>
    /// <param name="catalog">The field registry.</param>
    /// <param name="values">The value index.</param>
    /// <param name="series">The series picker.</param>
    /// <param name="people">The person picker.</param>
    /// <param name="allowedKinds">The kinds a query may name (the setting), read on each request: values count items of these only.</param>
    /// <param name="enabled">The setting <c>EnableSearchPage</c>, read on each request.</param>
    public SmartSearchLookups(LibraryAccess access, IFieldCatalog catalog, ValueIndexService values, SeriesTypeahead series, PersonTypeahead people, Func<IReadOnlyList<BaseItemKind>> allowedKinds, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(allowedKinds);
        ArgumentNullException.ThrowIfNull(enabled);
        _allowedKinds = allowedKinds;
        _enabled = enabled;
        _access = access;
        _catalog = catalog;
        _values = values;
        _series = series;
        _people = people;
    }

    /// <inheritdoc />
    public IReadOnlyList<SmartSearchLibraryDto> GetLibraries(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        SearchPageGate.Ensure(_enabled());
        return _access.VisibleLibraries(user)
            .Select(l => new SmartSearchLibraryDto { Id = l.Id, Name = l.Name, CollectionType = l.CollectionType?.ToString() })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SmartSearchValueDto>> GetValuesAsync(User user, string fieldId, string? prefix, IReadOnlyCollection<Guid>? libraryIds, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        SearchPageGate.Ensure(_enabled());
        var field = string.IsNullOrWhiteSpace(fieldId) ? null : _catalog.Find(fieldId.Trim());
        if (field?.ValueSource is not { } source || !source.StartsWith(ValuesSource, StringComparison.Ordinal)
            || !ValueIndexService.Fields.Contains(field.Id, StringComparer.Ordinal))
        {
            throw PluginException.Invalid("This field has no values to suggest.");
        }

        CheckLimit(limit);
        var text = CheckPrefix(prefix, 0);
        var folded = RuleValues.Fold(text);
        var values = await _values.ValuesAsync(user, field.Id, libraryIds, _allowedKinds(), cancellationToken).ConfigureAwait(false);
        return values
            .Select(v => (v.Key, v.Value, Folded: RuleValues.Fold(v.Key)))
            .Where(v => v.Folded.Contains(folded, StringComparison.Ordinal))
            .OrderByDescending(v => folded.Length > 0 && v.Folded.StartsWith(folded, StringComparison.Ordinal))
            .ThenByDescending(v => v.Value)
            .ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(v => new SmartSearchValueDto { Value = v.Key, Count = v.Value })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SmartSearchSeriesDto>> GetSeriesAsync(User user, string? prefix, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        SearchPageGate.Ensure(_enabled());
        CheckLimit(limit);
        var text = CheckPrefix(prefix, SmartSearchRequestLimits.MinSeriesPrefix);
        var series = await _series.FindAsync(user, text, limit, cancellationToken).ConfigureAwait(false);
        return series
            .Select(s => new SmartSearchSeriesDto { Id = s.Series.Id, Name = s.Series.Name ?? string.Empty, ProductionYear = s.Series.ProductionYear, LibraryId = s.LibraryId })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SmartSearchPersonDto>> GetPeopleAsync(User user, string? prefix, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        SearchPageGate.Ensure(_enabled());
        CheckLimit(limit);
        var text = CheckPrefix(prefix, SmartSearchRequestLimits.MinPersonPrefix);

        // The kinds a People rule applies to that the server lets users search.
        var fieldKinds = _catalog.Find("People")?.Kinds ?? [];
        var kinds = _allowedKinds().Where(fieldKinds.Contains).ToList();
        var people = await _people.FindAsync(user, text, limit, kinds, cancellationToken).ConfigureAwait(false);
        return people.Select(p => new SmartSearchPersonDto { Id = p.Id, Name = p.Name ?? string.Empty }).ToList();
    }

    private static void CheckLimit(int limit)
    {
        if (limit < 1 || limit > SmartSearchRequestLimits.MaxLookupLimit)
        {
            throw PluginException.Invalid("Limit must be from 1 to 100.");
        }
    }

    private static string CheckPrefix(string? prefix, int minLength)
    {
        var text = (prefix ?? string.Empty).Trim();
        if (text.Length > SmartSearchRequestLimits.MaxPrefix)
        {
            throw PluginException.Invalid("The text is longer than 200 characters.");
        }

        if (text.Length < minLength)
        {
            throw PluginException.Invalid("Type at least 2 characters.");
        }

        return text;
    }
}
