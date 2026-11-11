namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The server's keyboard shortcuts for the web player, part of
/// <c>BookmarkCapabilitiesDto</c>. A user may override each on their own device.
/// </summary>
public sealed class BookmarkShortcutsDto
{
    /// <summary>
    /// Gets or sets the combination that bookmarks the current moment, such as <c>Shift+B</c> (the default,
    /// <c>BookmarkNowShortcut</c>); empty when the admin turned it off.
    /// </summary>
    public string BookmarkNow { get; set; } = string.Empty;
}
