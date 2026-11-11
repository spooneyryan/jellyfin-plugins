namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// A junction or symbolic link <see cref="LinkGuard"/> found, or a folder it couldn't read.
/// </summary>
/// <param name="Path">The folder, spelled as it was asked.</param>
/// <param name="IsLocation">True when it is the library location itself.</param>
/// <param name="Unreadable">True when its attributes couldn't be read (it counts as a link: nothing below it is read).</param>
internal sealed record FoundLink(string Path, bool IsLocation, bool Unreadable)
{
    /// <summary>Gets the sentence a refusal or blocker says.</summary>
    public string Message => Unreadable
        ? "Move can't read " + Path + " to tell whether it is a junction or symbolic link, so it doesn't look below it."
        : IsLocation
            ? "The library folder " + Path + " is a junction or symbolic link; Move doesn't follow links, so it doesn't move media in or out of it."
            : Path + " is a junction or symbolic link; Move doesn't follow links, so it doesn't move what is below it.";
}
