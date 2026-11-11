using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// The pages Plugin Pages lists right now: the list its manager holds in memory, which <c>GET /PluginPages/User</c> returns
/// and the drawer shows (Plugin Pages 2.4.x reads its <c>config.json</c> only when it starts).
/// </summary>
/// <param name="Installed">Whether Plugin Pages' assembly is loaded.</param>
/// <param name="Pages">Its pages.</param>
/// <param name="Problem">Why the list couldn't be read, or null.</param>
internal sealed record PluginPagesState(bool Installed, IReadOnlyList<PluginPagesEntry> Pages, string? Problem)
{
    /// <summary>Gets the state when Plugin Pages isn't loaded.</summary>
    public static PluginPagesState NotInstalled { get; } = new(false, [], null);
}
