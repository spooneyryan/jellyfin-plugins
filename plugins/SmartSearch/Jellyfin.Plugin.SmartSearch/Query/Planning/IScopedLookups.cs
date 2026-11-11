using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// What the planner looks up in the caller's own scope before it pushes a name down: the series names of the episodes the
/// user can see, and whether a studio is on an item they can see. Both read the user's access query (parental limit,
/// unrated kinds, tags) and <c>IsVisible(user)</c>, so a name that only hidden items carry is never found, and a rule
/// naming it plans exactly like a rule naming nothing at all.
/// </summary>
internal interface IScopedLookups
{
    /// <summary>Gets the <c>(SeriesId, SeriesName)</c> pairs of the episodes the user can see under the folders.</summary>
    /// <param name="user">The user.</param>
    /// <param name="folders">The physical folders of the libraries searched.</param>
    /// <param name="includeMissing">Whether missing (virtual) episodes are in scope.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token.</param>
    /// <returns>The distinct pairs, or <c>null</c> when they can't be had now (then the rule isn't pushed).</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    IReadOnlyList<EpisodeSeries>? EpisodeSeries(User user, IReadOnlyList<Guid> folders, bool includeMissing, CancellationToken cancellationToken);

    /// <summary>Tells whether an item the user can see under the folders carries the studio.</summary>
    /// <param name="user">The user.</param>
    /// <param name="folders">The physical folders of the libraries searched.</param>
    /// <param name="kinds">The kinds searched.</param>
    /// <param name="includeMissing">Whether missing (virtual) items are in scope.</param>
    /// <param name="name">The studio's name.</param>
    /// <param name="studioId">The id of its Studio item.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token.</param>
    /// <returns>Whether one was found; <c>false</c> also when it can't be told now.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    bool HasStudio(User user, IReadOnlyList<Guid> folders, IReadOnlyCollection<BaseItemKind> kinds, bool includeMissing, string name, Guid studioId, CancellationToken cancellationToken);
}
