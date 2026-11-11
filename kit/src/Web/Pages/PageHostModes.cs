using System;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Reads <see cref="PageHostMode"/> from the plugin configuration's string.
/// </summary>
internal static class PageHostModes
{
    /// <summary>
    /// Parses a configured mode, ignoring case. Empty means the default, <see cref="PageHostMode.FallbackOnly"/>.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="mode">The mode; <see cref="PageHostMode.FallbackOnly"/> when the value isn't one.</param>
    /// <returns><c>true</c> when the value is empty or one of the three modes.</returns>
    public static bool TryParse(string? value, out PageHostMode mode)
    {
        mode = PageHostMode.FallbackOnly;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (var candidate in Enum.GetValues<PageHostMode>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses a configured mode; anything that isn't one of the three is <see cref="PageHostMode.FallbackOnly"/>.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns>The mode.</returns>
    public static PageHostMode Parse(string? value)
    {
        return TryParse(value, out var mode) ? mode : PageHostMode.FallbackOnly;
    }
}
