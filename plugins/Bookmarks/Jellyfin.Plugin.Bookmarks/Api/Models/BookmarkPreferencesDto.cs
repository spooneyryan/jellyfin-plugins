namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The caller's own naming preferences, kept on the server so they follow the user
/// across devices: <c>GET /Bookmarks/Preferences</c> returns them, <c>POST /Bookmarks/Preferences</c> replaces them. The
/// per-device shortcut override stays in the browser.
/// </summary>
public sealed class BookmarkPreferencesDto
{
    /// <summary>
    /// Gets or sets whether the player asks for a name as soon as a bookmark is made; <c>null</c> follows the server's
    /// default ("Use server default"), which <c>BookmarkCapabilitiesDto.AskForName</c> resolves.
    /// </summary>
    public bool? AskForName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether playback pauses while the name dialog is open; off by default.
    /// </summary>
    public bool PauseWhileNaming { get; set; }
}
