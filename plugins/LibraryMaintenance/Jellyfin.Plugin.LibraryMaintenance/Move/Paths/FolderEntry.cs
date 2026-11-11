using System.IO;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// A folder's entry found by name (<see cref="DestinationResolver.FindEntry"/>).
/// </summary>
/// <param name="Name">Its name on disk, or for an alias the name as asked.</param>
/// <param name="Attributes">Its attributes, read and never followed; none for an alias.</param>
/// <param name="IsAlias">True when no entry has the name as its long name but Windows opens one by it, such as another
/// entry's 8.3 short name (<c>FOLDER~1</c>): the name exists already under another name, so
/// it is never made, moved to or opened as if it were free.</param>
internal readonly record struct FolderEntry(string Name, FileAttributes Attributes, bool IsAlias);
