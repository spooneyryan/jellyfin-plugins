using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// What a compiled rule needs besides the row: the time relative rules count from, the server's time
/// zone (whose calendar days date-only values name), the rating scores for <c>OfficialRating Le/Ge</c>, the
/// members of libraries for <c>Library</c> rules (the <c>LibraryMembershipResolver</c>), and the items a person is credited
/// on for <c>People</c> rules (the <c>PeopleRoleResolver</c>).
/// </summary>
internal sealed class EvaluationEnvironment
{
    /// <summary>Gets the time <c>InLast</c> and <c>NotInLast</c> count back from, in UTC.</summary>
    public required DateTime NowUtc { get; init; }

    /// <summary>
    /// Gets the server's time zone (<see cref="TimeProvider.LocalTimeZone"/>): an instant such as <c>DateCreated</c> is on
    /// the calendar day it has there, and a time stored without a kind is taken as local, as core's database converter
    /// takes it.
    /// </summary>
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Gets the score of a parental rating (<c>ILocalizationManager.GetRatingScore</c>), or <c>null</c> for an unknown one.</summary>
    public Func<string, ParentalRatingScore?> RatingScore { get; init; } = _ => null;

    /// <summary>
    /// Gets the ids of the items in the given libraries, or <c>null</c> when no resolver is set; a <c>Library</c> rule then
    /// can't be compiled.
    /// </summary>
    public Func<IReadOnlyCollection<Guid>, IReadOnlySet<Guid>>? LibraryMembers { get; init; }

    /// <summary>
    /// Gets whether an item is credited to a person (the person's id and name as the rule holds them, and the role,
    /// <c>null</c> for any): a test on an item id, made once per reference when a rule is compiled; or <c>null</c> when no
    /// resolver is set, and a <c>People</c> rule then can't be compiled.
    /// </summary>
    public Func<Guid, string?, string?, Func<Guid, bool>>? PeopleMembers { get; init; }

    /// <summary>Creates an environment over the server's rating scores and clock.</summary>
    /// <param name="localization">The localization manager.</param>
    /// <param name="time">The clock and time zone (the plugin's <see cref="TimeProvider"/>).</param>
    /// <param name="libraryMembers">The library resolver, or <c>null</c>.</param>
    /// <returns>The environment.</returns>
    public static EvaluationEnvironment For(ILocalizationManager localization, TimeProvider time, Func<IReadOnlyCollection<Guid>, IReadOnlySet<Guid>>? libraryMembers = null)
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(time);
        return new EvaluationEnvironment
        {
            NowUtc = time.GetUtcNow().UtcDateTime,
            TimeZone = time.LocalTimeZone,
            RatingScore = rating => localization.GetRatingScore(rating),
            LibraryMembers = libraryMembers
        };
    }
}
