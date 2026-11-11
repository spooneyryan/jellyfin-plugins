using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The setting <c>EnableSearchPage</c> for the search and lookup endpoints: while it is off they answer 403 with the
/// kit's "turned off" type, as the page does.
/// </summary>
internal static class SearchPageGate
{
    /// <summary>The message of the 403.</summary>
    public const string DisabledMessage = "Advanced Search is turned off by your server admin.";

    /// <summary>Throws the 403 when the page is off.</summary>
    /// <param name="enabled">The setting, read now.</param>
    /// <exception cref="PluginException">403 <c>FeatureDisabled</c>.</exception>
    public static void Ensure(bool enabled)
    {
        if (!enabled)
        {
            throw PluginException.FeatureDisabled(DisabledMessage);
        }
    }
}
