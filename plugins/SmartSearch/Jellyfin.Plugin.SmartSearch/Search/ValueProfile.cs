using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.SmartSearch.Search;

/// <summary>
/// What decides which items of a library a user may see, apart from the library itself: their parental limit (score and
/// sub-score), the unrated kinds they're blocked from, and their blocked and allowed tags. Core's SQL filters
/// (<c>InternalItemsQuery.SetUser</c>) and the item's own <c>IsVisible(user)</c> for the video kinds read these and nothing
/// else of the user, so two users with equal profiles see the same items of a library, and share its value index.
/// </summary>
internal static class ValueProfile
{
    /// <summary>Gets the profile's key: every part, in a fixed order and form.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var unrated = user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Distinct().Order().Select(k => k.ToString());
        var blocked = user.GetPreference(PreferenceKind.BlockedTags).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var allowed = user.GetPreference(PreferenceKind.AllowedTags).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return string.Join(
            '\u001f',
            user.MaxParentalRatingScore?.ToString(CultureInfo.InvariantCulture) ?? "-",
            user.MaxParentalRatingSubScore?.ToString(CultureInfo.InvariantCulture) ?? "-",
            string.Join('\u001e', unrated),
            string.Join('\u001e', blocked),
            string.Join('\u001e', allowed));
    }
}
