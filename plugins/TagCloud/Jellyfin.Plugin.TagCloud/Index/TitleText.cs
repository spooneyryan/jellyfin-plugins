using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// The name filter's text and the resolution steps of <c>GET /TagCloud/Titles</c>.
/// </summary>
internal static class TitleText
{
    /// <summary>
    /// Gets text as the name filter compares it: decomposed, without its accents and other combining marks, in lower
    /// case (invariant), so "Amelie" finds "Amélie" and "amélie" finds "AMELIE". The same on every server whatever its
    /// culture data, unlike a culture's accent-insensitive compare.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <returns>The folded text; empty for none.</returns>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                folded.Append(c);
            }
        }

        return folded.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>
    /// Gets how sharp a picture is, as the server's own labels step it: 0 below 480p, 1 for 480p (up to
    /// 1024×576), 2 for 720p (up to 1280×962), 3 for 1080p (up to 2560×1440), 4 for 4K; null when the size isn't known.
    /// A film cropped wide, 1920×800, is 1080p; an old 4:3 one at 640×480 is 480p.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <returns>The step, or null.</returns>
    public static int? ResolutionStep(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (width <= 720 && height <= 404)
        {
            return 0;
        }

        if (width <= 1024 && height <= 576)
        {
            return 1;
        }

        if (width <= 1280 && height <= 962)
        {
            return 2;
        }

        return width <= 2560 && height <= 1440 ? 3 : 4;
    }

    /// <summary>
    /// Gets whether the folded search text holds the folded query.
    /// </summary>
    /// <param name="searchText">The title's folded name and original title.</param>
    /// <param name="foldedQuery">The folded query.</param>
    /// <returns>Whether it does.</returns>
    public static bool Contains(string searchText, string foldedQuery)
        => searchText.Contains(foldedQuery, StringComparison.Ordinal);
}
