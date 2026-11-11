using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.TagCloud.Configuration;

namespace Jellyfin.Plugin.TagCloud.Services;

/// <summary>
/// The setting <c>EnableTagCloudPage</c> for the cloud and saved-cloud endpoints: while it is off they answer 403 with the
/// kit's "turned off" type, as the page does. The admin endpoints and the capabilities stay.
/// </summary>
internal static class TagCloudPageGate
{
    /// <summary>The message of the 403.</summary>
    public const string DisabledMessage = "The Tag Cloud is turned off by your server admin.";

    /// <summary>Throws the 403 when the page is off.</summary>
    /// <param name="configuration">The settings, read now.</param>
    /// <exception cref="PluginException">403 <c>FeatureDisabled</c>.</exception>
    public static void Ensure(PluginConfiguration configuration)
    {
        if (!configuration.EnableTagCloudPage)
        {
            throw PluginException.FeatureDisabled(DisabledMessage);
        }
    }
}
