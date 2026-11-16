namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What an artwork file's name says (<see cref="ClipArtworkNames.Parse"/>).
/// </summary>
/// <param name="ClipStem">The clip file's name without its extension.</param>
/// <param name="Kind">poster, fanart or thumb.</param>
/// <param name="Extension">The image's extension.</param>
/// <param name="IsTemporary">Whether it is a file being written (<c>.&lt;hex&gt;.tmp</c>).</param>
internal sealed record ClipArtworkName(string ClipStem, string Kind, string Extension, bool IsTemporary);
