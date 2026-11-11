namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Which of Bookmarks' user pages this server offers, part of <c>BookmarkCapabilitiesDto</c>, so the
/// web client links only to pages that exist and are turned on.
/// </summary>
public sealed class BookmarkPagesDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the My Bookmarks page is there: the build has it and the admin didn't turn it
    /// off (<c>EnableBookmarksPage</c>).
    /// </summary>
    public bool Bookmarks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the My Clips page is there: the build has it (since 1.2.0; always
    /// <c>false</c> in 1.1.x) and the admin didn't turn it off (<c>EnableClipsPage</c>). It also decides the default of the
    /// delete dialogs' "Also delete its clip": unticked while it is <c>true</c>, since a kept clip is listed
    /// in My Clips; ticked, as in 1.1.0, while it is <c>false</c>.
    /// </summary>
    public bool Clips { get; set; }
}
