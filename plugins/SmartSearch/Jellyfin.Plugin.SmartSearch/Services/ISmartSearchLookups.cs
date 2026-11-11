using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The rule builder's lookups, each scoped to what the signed-in user can see: their libraries, the values of a field
/// with their counts, and series by name. Nothing from a library the user can't open, or from an item above their
/// parental limit or carrying a tag they're blocked from, is listed or counted.
/// </summary>
public interface ISmartSearchLookups
{
    /// <summary>Gets the libraries the user can open.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <returns>The libraries.</returns>
    IReadOnlyList<SmartSearchLibraryDto> GetLibraries(User user);

    /// <summary>Gets the values of a field (Genres, Tags, Studios, OfficialRating, Container) with their counts, counting the
    /// items of the kinds the server allows only.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="fieldId">The field's id (or alias).</param>
    /// <param name="prefix">Text the value contains, ignoring case and accents (those that start with it first); none lists the most common.</param>
    /// <param name="libraryIds">The libraries to count in, within the user's; none for all of them.</param>
    /// <param name="limit">The most values, 1 to 100.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The values.</returns>
    Task<IReadOnlyList<SmartSearchValueDto>> GetValuesAsync(User user, string fieldId, string? prefix, IReadOnlyCollection<Guid>? libraryIds, int limit, CancellationToken cancellationToken);

    /// <summary>Gets the series whose name contains the text, ignoring case and accents.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="prefix">The text, at least 2 characters.</param>
    /// <param name="limit">The most series, 1 to 100.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The series, those whose name starts with the text first, then by name.</returns>
    Task<IReadOnlyList<SmartSearchSeriesDto>> GetSeriesAsync(User user, string? prefix, int limit, CancellationToken cancellationToken);
}
