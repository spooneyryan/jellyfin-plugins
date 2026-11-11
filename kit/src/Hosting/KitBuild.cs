using System;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// Build facts compiled into the plugin by <c>kit/build/Kit.props</c> as assembly metadata.
/// </summary>
internal static class KitBuild
{
    /// <summary>The metadata key of <see cref="JellyfinTarget"/>.</summary>
    public const string JellyfinTargetKey = "Jellyfin.Plugin.Kit.JellyfinTarget";

    /// <summary>The metadata key of <see cref="KitVersion"/>.</summary>
    public const string KitVersionKey = "Jellyfin.Plugin.Kit.Version";

    /// <summary>
    /// Gets the Jellyfin line this build is for: <c>10.11</c> (any 10.11.x server) or <c>12</c> (any 12.x server).
    /// </summary>
    /// <exception cref="InvalidOperationException">The build carries no target; <see cref="KitAbiGuard"/> then keeps the plugin inert.</exception>
    public static string JellyfinTarget => Read(JellyfinTargetKey);

    /// <summary>
    /// Gets the kit's version (<c>kit/VERSION</c>).
    /// </summary>
    public static string KitVersion => Read(KitVersionKey);

    private static string Read(string key)
        => typeof(KitBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?.Value is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The build has no '{key}' assembly metadata; kit/build/Kit.props sets it.");
}
