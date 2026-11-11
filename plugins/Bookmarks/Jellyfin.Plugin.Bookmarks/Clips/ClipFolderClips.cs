namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The clips recorded in one clip folder, as <see cref="ClipFileOps.CountClips(string)"/> counted them.
/// </summary>
/// <param name="Clips">How many records' clip files lie in the folder.</param>
/// <param name="Present">How many of those files are there.</param>
/// <param name="Gone">How many aren't (their file, or their folder, isn't there), the clips being made not counted.</param>
/// <param name="Marked">How many of the gone ones are marked missing already.</param>
internal sealed record ClipFolderClips(int Clips, int Present, int Gone, int Marked);
