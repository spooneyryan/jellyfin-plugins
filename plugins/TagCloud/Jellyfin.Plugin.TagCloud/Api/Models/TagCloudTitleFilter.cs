namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// How <c>GET /TagCloud/Titles</c> filters and sorts a word's titles, on the server, before it pages them: the request's
/// <c>sort</c>, <c>descending</c>, <c>q</c>, <c>minResolution</c> and <c>unwatched</c>.
/// </summary>
internal sealed class TagCloudTitleFilter
{
    /// <summary>The longest name filter.</summary>
    public const int MaxQueryLength = 100;

    /// <summary>Gets or sets what to sort by; null for the request's <c>order</c> (Name, or Random).</summary>
    public TagCloudTitleSort? Sort { get; set; }

    /// <summary>Gets or sets a value indicating whether the sort runs the other way round.</summary>
    public bool Descending { get; set; }

    /// <summary>Gets or sets text the name or the original title must hold, ignoring case and accents; at most
    /// <see cref="MaxQueryLength"/> characters. Null or blank for none.</summary>
    public string? Query { get; set; }

    /// <summary>Gets or sets the least sharp picture kept (movies only).</summary>
    public TagCloudMinResolution MinResolution { get; set; }

    /// <summary>Gets or sets a value indicating whether only the titles the caller hasn't watched are kept: a movie not
    /// played, a series with an episode not played.</summary>
    public bool Unwatched { get; set; }
}
