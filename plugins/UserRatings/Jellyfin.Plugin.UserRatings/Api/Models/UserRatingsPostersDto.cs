namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The poster badges' settings (<c>Display.Posters</c>; the badges come with 1.1).
/// </summary>
public sealed class UserRatingsPostersDto
{
    /// <summary>
    /// Gets or sets a value indicating whether posters show badges (<c>ShowOnPosters</c>).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets what a badge shows: <c>Average</c>, <c>Mine</c> (the viewer's own stars) or <c>Both</c>.
    /// </summary>
    public string Content { get; set; } = "Average";

    /// <summary>
    /// Gets or sets a badge's corner: <c>top-start</c>, <c>top-end</c>, <c>bottom-start</c> or <c>bottom-end</c> (start is
    /// the left in a left-to-right language).
    /// </summary>
    public string Position { get; set; } = "top-start";
}
