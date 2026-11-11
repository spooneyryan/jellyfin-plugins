using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// What the planner reads from core's database to decide whether a filter is safe to push (the clean forms):
/// read-only, and every answer that can't be had is "unknown", which means "don't push".
/// </summary>
internal interface IPushdownFacts
{
    /// <summary>Gets the clean-form snapshot, or <c>null</c> while the probe hasn't run, failed or found too many rows; then
    /// <c>Title</c>, <c>Genres</c>, <c>Tags</c> and <c>Studios</c> rules aren't pushed.</summary>
    CleanFormSnapshot? CleanForms { get; }

    /// <summary>
    /// Reads the <c>ItemValues</c> rows (genres, studios, tags) whose value holds a <c>|</c>, now: items load their lists by
    /// splitting a <c>|</c>-joined column, so such a row reaches the evaluator as parts SQL never matches. New rows can appear
    /// at any time, so this is read per plan.
    /// </summary>
    /// <param name="cancellationToken">The evaluation's cancellation token, checked before and while reading.</param>
    /// <returns>The rows, or <c>null</c> when they can't be read or are too many.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    IReadOnlyList<ItemValueRow>? PipeValues(CancellationToken cancellationToken);

    /// <summary>
    /// Tells whether, for every item, the user's user-data rows agree on a flag. Core saves one row per user-data key with
    /// the same values, and its SQL filter reads an arbitrary one of them, while <c>GetUserData</c> reads the row of the
    /// item's current keys; rows of keys an item no longer has keep old values. Only when they all agree is the SQL filter
    /// <c>= true</c> a superset of the in-memory rule.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="flag">The flag.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token, checked before reading.</param>
    /// <returns>Whether they agree; <c>false</c> when unknown.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    bool UserDataAgrees(Guid userId, UserDataFlag flag, CancellationToken cancellationToken);

    /// <summary>Reads the distinct <c>(SeriesId, SeriesName)</c> pairs episodes under the top parents store.</summary>
    /// <param name="topParentIds">The libraries' physical folder ids.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token, checked before and while reading.</param>
    /// <returns>The pairs, or <c>null</c> when they can't be read or are too many.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    IReadOnlyList<EpisodeSeries>? EpisodeSeries(IReadOnlyCollection<Guid> topParentIds, CancellationToken cancellationToken);
}
