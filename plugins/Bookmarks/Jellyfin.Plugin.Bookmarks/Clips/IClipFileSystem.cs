using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The file system calls of <see cref="ClipFileGuard"/> and <see cref="ClipFileOps"/>: a seam, so tests can make a
/// file "in use", or a folder a reparse point, without Windows or administrator rights. <see cref="PhysicalClipFileSystem"/>
/// is the real one.
/// </summary>
internal interface IClipFileSystem : IClipPathProbe
{
    /// <summary>
    /// Deletes a file.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <exception cref="IOException">The file is in use (<c>HResult</c> <c>0x80070020</c> or <c>0x80070021</c>), or can't
    /// be deleted for another reason.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not delete it.</exception>
    void DeleteFile(string path);

    /// <summary>
    /// Moves (renames) a file, never over an existing one.
    /// </summary>
    /// <param name="source">The file's full path.</param>
    /// <param name="destination">Its new full path, which must not exist.</param>
    /// <exception cref="IOException">The file is in use, the destination exists, or the move failed for another
    /// reason.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not move it.</exception>
    void MoveFile(string source, string destination);

    /// <summary>
    /// Moves a file over another, replacing it, or into place when there is none (since Bookmarks 1.2.1: a remade clip over
    /// its own file).
    /// </summary>
    /// <param name="source">The file's full path.</param>
    /// <param name="destination">The file to replace.</param>
    /// <exception cref="IOException">Either file is in use, or the move failed for another reason.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not move it.</exception>
    void ReplaceFile(string source, string destination);

    /// <summary>
    /// Lists the files in a clip folder and in the folders directly inside it (the per-user folders), with their last write
    /// times, for the <c>.partial</c> janitor: no deeper, never into a junction, symbolic link or mount point below the
    /// folder, and past folders the server may not read. The folder itself may be a link. The list is read as it is
    /// enumerated.
    /// </summary>
    /// <param name="folder">The clip folder's full path.</param>
    /// <returns>The files.</returns>
    /// <exception cref="IOException">The folder can't be listed (it is gone, or its drive isn't ready), also while the list is
    /// read.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not read it.</exception>
    IEnumerable<ClipFolderFile> ListClipFolder(string folder);

    /// <summary>
    /// Gets when a file was last written (UTC), for the My Clips list's date of a clip whose job's time isn't the file's.
    /// Never throws.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>The time, or <c>null</c> when the file isn't there or can't be read.</returns>
    DateTime? GetLastWriteTimeUtc(string path);

    /// <summary>
    /// Reads a clip folder's marker file (<see cref="ClipRootMarker"/>): at most <see cref="ClipRootMarker.MaxBytes"/>,
    /// never through a link. The test doubles over the real disk keep the real one (this default), so a double written
    /// before the marker reads and writes it as the server does.
    /// </summary>
    /// <param name="folder">The clip folder's full path.</param>
    /// <returns>The file's text; <c>null</c> when there is none; empty when what is at its path isn't a marker file (a folder,
    /// a link, a file that is too large).</returns>
    /// <exception cref="IOException">It can't be read.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not read it.</exception>
    string? ReadRootMarker(string folder) => PhysicalClipFileSystem.ReadMarkerFile(folder);

    /// <summary>
    /// Writes a clip folder's marker file (<see cref="ClipRootMarker"/>) into the folder, which must be there: a new file of
    /// its own, then moved into place over any earlier one.
    /// </summary>
    /// <param name="folder">The clip folder's full path.</param>
    /// <param name="text">The marker's text (<see cref="ClipRootMarker.Text"/>).</param>
    /// <exception cref="IOException">It can't be written (the folder isn't there, a folder is at its path).</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not write it.</exception>
    void WriteRootMarker(string folder, string text) => PhysicalClipFileSystem.WriteMarkerFile(folder, text);

    /// <summary>
    /// Gets what tells a clip file from another one at the same path (for the rename journal): its file id
    /// and volume where the file system gives them, its size and its last write time, which a rename keeps. Never throws.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>The file's identity, or <c>null</c> when no file is there or it can't be read.</returns>
    ClipFileIdentity? GetFileIdentity(string path) => PhysicalClipFileSystem.ReadFileIdentity(path);

    /// <summary>
    /// Makes the last rename of a file durable before the bookmark data names its new path (the data folder and
    /// a clip folder may be on two volumes, and a power cut could otherwise keep the record's write and lose the rename).
    /// Never throws.
    /// </summary>
    /// <param name="path">The file's full path (its new one).</param>
    /// <returns>Whether it was flushed; <c>false</c> when the file system wouldn't (the rename is then left to be judged
    /// again later).</returns>
    bool FlushFile(string path) => PhysicalClipFileSystem.FlushToDisk(path);

    /// <summary>
    /// Gets the name a file has in its folder, as the folder holds it (its case): for a rename that changes the case alone.
    /// Never throws.
    /// </summary>
    /// <param name="path">The file's full path, in any case.</param>
    /// <returns>The name, or <c>null</c> when no file is there or the folder can't be read.</returns>
    string? GetOnDiskName(string path) => PhysicalClipFileSystem.ReadOnDiskName(path);

    /// <summary>
    /// Reads the rename journal's entries in the plugin's data folder (<see cref="ClipRenameJournal"/>): every file there named
    /// <c>&lt;prefix&gt;*.json</c>, never through a link; one over <see cref="ClipRenameJournal.MaxBytes"/> without its text.
    /// </summary>
    /// <param name="folder">The data folder.</param>
    /// <param name="prefix">The entries' file name prefix.</param>
    /// <returns>Each entry's path and text (<c>null</c> for one too large); none when the folder isn't there.</returns>
    /// <exception cref="IOException">The folder can't be listed.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not read it.</exception>
    IReadOnlyList<(string Path, string? Text)> ReadRenameJournal(string folder, string prefix) => PhysicalClipFileSystem.ReadJournalFiles(folder, prefix);

    /// <summary>
    /// Deletes the leftovers of journal or history writes that stopped half way (<c>&lt;prefix&gt;*.tmp</c>).
    /// </summary>
    /// <param name="folder">The data folder.</param>
    /// <param name="prefix">The files' name prefix.</param>
    /// <returns>How many were deleted.</returns>
    /// <exception cref="IOException">The folder can't be listed.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not read it.</exception>
    int DeleteRenameJournalLeftovers(string folder, string prefix) => PhysicalClipFileSystem.DeleteJournalLeftovers(folder, prefix);

    /// <summary>
    /// Reads the clip file renames' history (<see cref="ClipRenameHistory"/>), never through a link.
    /// </summary>
    /// <param name="path">The history file's full path.</param>
    /// <returns>Its text; <c>null</c> when there is none; empty when what is there isn't one.</returns>
    /// <exception cref="IOException">It can't be read.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not read it.</exception>
    string? ReadRenameHistory(string path) => PhysicalClipFileSystem.ReadHistoryFile(path);

    /// <summary>
    /// Writes one rename journal entry (or the renames' history), flushed to the disk before it is moved into place (a new
    /// file of its own name first) and once more after, so a crash right after the clip file's move still finds it.
    /// </summary>
    /// <param name="path">The entry's full path.</param>
    /// <param name="text">Its text.</param>
    /// <exception cref="IOException">It can't be written.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not write it.</exception>
    void WriteRenameJournal(string path, string text) => PhysicalClipFileSystem.WriteJournalFile(path, text);

    /// <summary>
    /// Deletes one rename journal entry; one that isn't there counts as deleted.
    /// </summary>
    /// <param name="path">The entry's full path.</param>
    /// <exception cref="IOException">It can't be deleted.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not delete it.</exception>
    void DeleteRenameJournal(string path) => PhysicalClipFileSystem.DeleteJournalFile(path);

    /// <summary>
    /// Moves an unreadable renames' history aside (<see cref="ClipRenameHistory"/>), so nothing is written over it: a
    /// regular file only (never a folder or a link), never over another file.
    /// </summary>
    /// <param name="path">The history file's full path.</param>
    /// <param name="destination">The full path it goes to, in the same folder.</param>
    /// <exception cref="IOException">It can't be moved (or it isn't a regular file).</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not move it.</exception>
    void SetAsideRenameHistory(string path, string destination) => PhysicalClipFileSystem.SetAsideHistoryFile(path, destination);
}
