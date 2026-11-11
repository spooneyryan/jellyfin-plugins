namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// A page the Plugin Pages plugin lists in the drawer (its <c>PluginPage</c>).
/// </summary>
/// <param name="Id">The page's id, such as <c>Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage</c>.</param>
/// <param name="Url">The page's URL.</param>
/// <param name="DisplayText">The link's text.</param>
internal sealed record PluginPagesEntry(string Id, string? Url, string? DisplayText);
