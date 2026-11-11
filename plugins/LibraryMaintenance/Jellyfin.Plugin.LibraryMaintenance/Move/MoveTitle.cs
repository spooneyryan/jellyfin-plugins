using System;
using System.Globalization;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// A movie's title with its year, as Move's texts and the wrap folder's name show it: <c>Title (Year)</c>, the year once.
/// Jellyfin keeps the year in the name of a movie whose folder or file name it didn't parse ("Folder Movie (2008)" with
/// the year 2008), which used to read "Folder Movie (2008) (2008)"; the dialog's own title has the same rule.
/// </summary>
internal static class MoveTitle
{
    /// <summary>
    /// Adds <c> (year)</c> to a name, unless there is no year or the name already ends with it.
    /// </summary>
    /// <param name="name">The item's name, or null.</param>
    /// <param name="year">Its production year, or null.</param>
    /// <returns>The name with its year once.</returns>
    public static string WithYear(string? name, int? year)
    {
        var text = name ?? string.Empty;
        if (year is not > 0)
        {
            return text;
        }

        var suffix = "(" + year.Value.ToString(CultureInfo.InvariantCulture) + ")";
        return text.TrimEnd().EndsWith(suffix, StringComparison.Ordinal) ? text : text + " " + suffix;
    }
}
