using System;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// Reads <see cref="DeletedUserPolicy"/> from the plugin configuration's string (plugin settings are public, kit types
/// internal, so the setting is a string, like <c>PageHostMode</c>).
/// </summary>
internal static class DeletedUserPolicies
{
    /// <summary>
    /// Parses a configured policy, ignoring case and surrounding spaces.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="policy">The policy; <paramref name="fallback"/> when the value isn't one.</param>
    /// <param name="fallback">The plugin's default.</param>
    /// <returns><c>true</c> when the value names a policy.</returns>
    public static bool TryParse(string? value, out DeletedUserPolicy policy, DeletedUserPolicy fallback = DeletedUserPolicy.KeepAsOrphan)
    {
        policy = fallback;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<DeletedUserPolicy>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                policy = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses a configured policy; anything else (empty, a typo) is the plugin's default.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="fallback">The plugin's default.</param>
    /// <returns>The policy.</returns>
    public static DeletedUserPolicy Parse(string? value, DeletedUserPolicy fallback = DeletedUserPolicy.KeepAsOrphan)
    {
        return TryParse(value, out var policy, fallback) ? policy : fallback;
    }
}
