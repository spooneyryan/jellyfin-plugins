using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The server-side context of a whole evaluation (the auto-collections): no searching user, the libraries it may read,
/// whose watch state and ratings the rules read, and the time relative dates are measured from.
/// </summary>
/// <param name="Purpose">What the evaluation is for; it picks the engine slot.</param>
/// <param name="EvaluationUser">The user whose watch state per-user rules read, or <c>null</c>. Their access applies too
/// (<c>SetUser</c>: libraries, parental limits, blocked tags, and <c>IsVisible</c> on every result).</param>
/// <param name="AllowedLibraries">The library ids the evaluation may read (the scope's libraries the definition's creator
/// can see), or <c>null</c> for every library the evaluation user (or the server) has. Boxsets libraries never count
/// unless the query asks for collections.</param>
/// <param name="RatingsViewerId">The user whose view the ratings facts are read with (their own stars), or <c>null</c>.</param>
/// <param name="NowUtc">The time relative dates are measured from (one value for a whole pass), or <c>null</c> for now.</param>
/// <param name="WholeSets">Whether a version set is one result even where <c>/Items</c> would show a row per
/// presentation key (a collection links one item per set).</param>
/// <param name="ItemCache">The candidates already loaded in this pass over the auto-collections, or <c>null</c>.</param>
internal sealed record EvaluationContext(
    EvaluationPurpose Purpose,
    User? EvaluationUser = null,
    IReadOnlyCollection<Guid>? AllowedLibraries = null,
    Guid? RatingsViewerId = null,
    DateTime? NowUtc = null,
    bool WholeSets = true,
    PassItemCache? ItemCache = null);
