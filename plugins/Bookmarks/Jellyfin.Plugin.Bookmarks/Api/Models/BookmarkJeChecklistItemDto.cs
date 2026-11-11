namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One step of the switch-off checklist in the report of an
/// applied Jellyfin Enhanced import, as the server sees it right after the import: JE's four Bookmarks settings (then
/// saving JE's settings), the Custom Tab, the restart for the Plugin Pages link, and last JE's shortcut, set to Disabled or
/// another key, not blank, for the server default and every user's own JE shortcuts.
/// </summary>
public sealed class BookmarkJeChecklistItemDto
{
    /// <summary>
    /// Gets or sets the step's key: a JE setting's name, <c>CustomTab</c>, <c>PluginPagesLink</c>, <c>ShortcutAdmin</c> or
    /// <c>ShortcutUsers</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what to do.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the server sees it done.
    /// </summary>
    public bool Done { get; set; }

    /// <summary>
    /// Gets or sets what the server found, and what stays until a restart.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a restart is still needed for it (the Plugin Pages link), or the tick isn't
    /// durable: a cleared JE key comes back as B at JE's next start ("set it to Disabled or another key instead").
    /// </summary>
    public bool NeedsRestart { get; set; }
}
