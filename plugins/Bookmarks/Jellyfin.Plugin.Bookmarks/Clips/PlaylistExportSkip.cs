namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// An entry of a playlist that a playlist export leaves out, and why.
/// </summary>
/// <param name="Name">The item's name, or <c>null</c> when the caller may not see it (or it is no longer in the library).</param>
/// <param name="Reason">Why, in a few words for the export dialog.</param>
internal sealed record PlaylistExportSkip(string? Name, string Reason);
