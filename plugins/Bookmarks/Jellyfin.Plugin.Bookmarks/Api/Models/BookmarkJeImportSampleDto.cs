namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One sample row of the import report: a Jellyfin Enhanced bookmark, what the import does
/// with it and why. The report shows at most 50, the unusual outcomes first. Admins only: it holds users' labels.
/// </summary>
public sealed class BookmarkJeImportSampleDto
{
    /// <summary>
    /// Gets or sets the user id of the Jellyfin Enhanced folder (N format).
    /// </summary>
    public string JeUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user's name, or <c>null</c> when unknown.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's key in JE's file (<c>Bm_…</c>).
    /// </summary>
    public string JeKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets JE's label as JE holds it; the bookmark's name is the label normalised.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets the video: this server's display name when it matched one, else the title JE stored.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the position the bookmark gets, in ticks, when it is imported or updated.
    /// </summary>
    public long? StartTicks { get; set; }

    /// <summary>
    /// Gets or sets what happens to it.
    /// </summary>
    public BookmarkJeImportOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets why, in a sentence.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
