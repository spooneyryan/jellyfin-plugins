using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The real file system behind <see cref="ClipFileOps"/>: the only code of Bookmarks that deletes or moves a file (checked
/// on the compiled code by the tests' <c>Clips/FileOperationsCoverageTests</c>), so every clip file operation goes
/// through <see cref="ClipFileGuard"/> first. Since 1.2.0 it also reads and writes the clip folders' marker files
/// (<see cref="ClipRootMarker"/>), the one file Bookmarks writes in a clip folder besides clips; it also writes
/// and deletes the entries of the clip file renames' journal (<see cref="ClipRenameJournal"/>), in the plugin's data folder,
/// and the renames' history next to it (<see cref="ClipRenameHistory"/>), and makes a rename durable
/// (<see cref="FlushFile"/>).
/// </summary>
internal sealed class PhysicalClipFileSystem : IClipFileSystem
{
    /// <summary>
    /// Gets the one instance.
    /// </summary>
    public static PhysicalClipFileSystem Instance { get; } = new();

    // The enumeration: the folder and one level of folders below it, never through a reparse point (a junction, a
    // symbolic link, a mount point; a file that is one is left out too), hidden and system files included, and folders
    // that can't be read left out.
    private static EnumerationOptions ClipFolderListing { get; } = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 1,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault,
        ReturnSpecialDirectories = false
    };

    /// <inheritdoc />
    public FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool FolderExists(string path)
    {
        // Directory.Exists sees a junction or a symbolic link itself, which stays when its target's drive is unplugged, so a
        // link's final target must be there too. Any error (a drive not ready, no permission) is "not there": never throws.
        try
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists)
            {
                return false;
            }

            return !folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || folder.ResolveLinkTarget(returnFinalTarget: true) is not { } target
                || target is DirectoryInfo { Exists: true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public void MoveFile(string source, string destination) => File.Move(source, destination, overwrite: false);

    /// <inheritdoc />
    public void ReplaceFile(string source, string destination) => File.Move(source, destination, overwrite: true);

    /// <inheritdoc />
    public IEnumerable<ClipFolderFile> ListClipFolder(string folder)
        => new DirectoryInfo(folder).EnumerateFiles("*", ClipFolderListing).Select(file => new ClipFolderFile(file.FullName, file.LastWriteTimeUtc));

    /// <inheritdoc />
    public DateTime? GetLastWriteTimeUtc(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.LastWriteTimeUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? ReadRootMarker(string folder) => ReadMarkerFile(folder);

    /// <inheritdoc />
    public void WriteRootMarker(string folder, string text) => WriteMarkerFile(folder, text);

    /// <inheritdoc />
    public ClipFileIdentity? GetFileIdentity(string path) => ReadFileIdentity(path);

    /// <inheritdoc />
    public IReadOnlyList<(string Path, string? Text)> ReadRenameJournal(string folder, string prefix) => ReadJournalFiles(folder, prefix);

    /// <inheritdoc />
    public void WriteRenameJournal(string path, string text) => WriteJournalFile(path, text);

    /// <inheritdoc />
    public void DeleteRenameJournal(string path) => DeleteJournalFile(path);

    /// <inheritdoc />
    public void SetAsideRenameHistory(string path, string destination) => SetAsideHistoryFile(path, destination);

    /// <inheritdoc />
    public int DeleteRenameJournalLeftovers(string folder, string prefix) => DeleteJournalLeftovers(folder, prefix);

    /// <inheritdoc />
    public string? ReadRenameHistory(string path) => ReadHistoryFile(path);

    /// <inheritdoc />
    public bool FlushFile(string path) => FlushToDisk(path);

    /// <inheritdoc />
    public string? GetOnDiskName(string path) => ReadOnDiskName(path);

    /// <summary>
    /// Reads a clip folder's marker file (<see cref="IClipFileSystem.ReadRootMarker"/>).
    /// </summary>
    /// <param name="folder">The clip folder's full path.</param>
    /// <returns>Its text, <c>null</c> when there is none, empty when it isn't a marker file.</returns>
    internal static string? ReadMarkerFile(string folder)
    {
        var path = ClipRootMarker.PathIn(folder);
        try
        {
            // Never through a link: a junction or symbolic link at the marker's name isn't one.
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > ClipRootMarker.MaxBytes)
            {
                return string.Empty;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a clip folder's marker file (<see cref="IClipFileSystem.WriteRootMarker"/>): a new file of a name of its own
    /// (never one that is there already, so never through a link someone left there), flushed, then moved into place.
    /// </summary>
    /// <param name="folder">The clip folder's full path.</param>
    /// <param name="text">The text.</param>
    internal static void WriteMarkerFile(string folder, string text)
    {
        var path = ClipRootMarker.PathIn(folder);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder is gone, or the file can't be deleted either: left as it is (it isn't a clip file's name).
            }

            throw;
        }
    }

    /// <summary>
    /// Reads a file's identity (<see cref="IClipFileSystem.GetFileIdentity"/>).
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>Its size and times, or <c>null</c>.</returns>
    internal static ClipFileIdentity? ReadFileIdentity(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.Directory))
            {
                return null;
            }

            var identity = new ClipFileIdentity(file.Length, file.LastWriteTimeUtc);
            return OperatingSystem.IsWindows() && ReadWindowsFileId(file.FullName) is { } id ? identity with { Volume = id.Volume, FileId = id.FileId } : identity;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes a file's last rename durable (<see cref="IClipFileSystem.FlushFile"/>): <c>FlushFileBuffers</c> on the file (a
    /// handle with write access that writes nothing, so the file's times stay), which on NTFS writes the volume's log up to
    /// the file's last change, its rename included; where the file can't be opened for writing (a reader that doesn't share
    /// writing), on Windows the folder's handle instead. Elsewhere <c>fsync</c> of the file.
    /// </summary>
    /// <param name="path">The file's full path.</param>
    /// <returns>Whether it was flushed.</returns>
    internal static bool FlushToDisk(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The folder's handle, on Windows: its entry is what the rename changed.
        }

        if (!OperatingSystem.IsWindows() || Path.GetDirectoryName(path) is not { Length: > 0 } folder)
        {
            return false;
        }

        using var handle = NativeMethods.CreateFile(Extended(folder), NativeMethods.GenericWrite, NativeMethods.ShareAll, IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.BackupSemantics, IntPtr.Zero);
        return !handle.IsInvalid && NativeMethods.FlushFileBuffers(handle);
    }

    /// <summary>
    /// Gets a file's name as its folder holds it (<see cref="IClipFileSystem.GetOnDiskName"/>): its case, which a change of
    /// case alone is about.
    /// </summary>
    /// <param name="path">The file's full path, in any case.</param>
    /// <returns>The name, or <c>null</c> when no file is there or the folder can't be read.</returns>
    internal static string? ReadOnDiskName(string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is not { Length: > 0 } folder || Path.GetFileName(path) is not { Length: > 0 } name)
            {
                return null;
            }

            return new DirectoryInfo(folder).EnumerateFiles(name, new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.Directory,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
                MatchCasing = MatchCasing.CaseInsensitive
            }).Select(f => f.Name).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the rename journal's entries (<see cref="IClipFileSystem.ReadRenameJournal"/>): files only, never a link; one over
    /// <see cref="ClipRenameJournal.MaxBytes"/> is listed without its text (the journal warns, and holds its clip).
    /// </summary>
    /// <param name="folder">The data folder.</param>
    /// <param name="prefix">The entries' file name prefix.</param>
    /// <returns>Each entry's path and text (<c>null</c> for one too large).</returns>
    internal static IReadOnlyList<(string Path, string? Text)> ReadJournalFiles(string folder, string prefix)
    {
        var entries = new List<(string, string?)>();
        if (!Directory.Exists(folder))
        {
            return entries;
        }

        foreach (var file in new DirectoryInfo(folder).EnumerateFiles(prefix + "*.json", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseInsensitive
        }))
        {
            if (file.Length > ClipRenameJournal.MaxBytes)
            {
                entries.Add((file.FullName, null));
                continue;
            }

            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                entries.Add((file.FullName, Encoding.UTF8.GetString(bytes)));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted while the folder was read: a rename that finished meanwhile.
            }
        }

        return entries;
    }

    /// <summary>
    /// Writes one rename journal entry (<see cref="IClipFileSystem.WriteRenameJournal"/>), or the renames' history: a new file
    /// of a name of its own, flushed to the disk, moved into place, and flushed again there (the move into place
    /// is on the disk before the clip file moves, which may be on another volume), so a power cut after the clip's move never
    /// finds the clip moved and the entry gone. A flush that fails fails the write.
    /// </summary>
    /// <param name="path">The entry's full path.</param>
    /// <param name="text">Its text.</param>
    internal static void WriteJournalFile(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            using (var placed = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete, 1, FileOptions.None))
            {
                placed.Flush(flushToDisk: true);
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left as it is: a .tmp of no use, never read as an entry.
            }

            throw;
        }
    }

    /// <summary>
    /// Deletes one rename journal entry (<see cref="IClipFileSystem.DeleteRenameJournal"/>).
    /// </summary>
    /// <param name="path">The entry's full path.</param>
    internal static void DeleteJournalFile(string path) => File.Delete(path);

    /// <summary>
    /// Moves an unreadable renames' history aside (<see cref="IClipFileSystem.SetAsideRenameHistory"/>): a regular file only,
    /// never over another file.
    /// </summary>
    /// <param name="path">The history file's full path.</param>
    /// <param name="destination">Where it goes.</param>
    internal static void SetAsideHistoryFile(string path, string destination)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The renames' history isn't a regular file, so it isn't moved.");
        }

        File.Move(path, destination, overwrite: false);
    }

    /// <summary>
    /// Deletes what a write of the journal or the history that stopped half way left (<c>&lt;prefix&gt;*.tmp</c>, the files
    /// <see cref="WriteJournalFile"/> writes first; <see cref="IClipFileSystem.DeleteRenameJournalLeftovers"/>): never a link, and
    /// only while no write of this process is under way (the journal's first read).
    /// </summary>
    /// <param name="folder">The data folder.</param>
    /// <param name="prefix">The files' name prefix.</param>
    /// <returns>How many were deleted.</returns>
    internal static int DeleteJournalLeftovers(string folder, string prefix)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles(prefix + "*.tmp", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseInsensitive
        }).ToList())
        {
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use, or not ours to delete: left for the next start.
            }
        }

        return deleted;
    }

    /// <summary>
    /// Reads the renames' history file (<see cref="IClipFileSystem.ReadRenameHistory"/>): at most
    /// <see cref="ClipRenameHistory.MaxBytes"/>, never through a link.
    /// </summary>
    /// <param name="path">The file's full path.</param>
    /// <returns>Its text; <c>null</c> when there is none; empty when what is there isn't a history file.</returns>
    internal static string? ReadHistoryFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > ClipRenameHistory.MaxBytes)
            {
                return string.Empty;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // The file id and the volume of a file, on Windows: FILE_ID_INFO (NTFS and ReFS, Windows 8 and later), else
    // BY_HANDLE_FILE_INFORMATION's 64-bit index; the handle opened for its attributes only, never through a link. Null when
    // neither answers (a share that gives none), or both are zero.
    private static (ulong Volume, string FileId)? ReadWindowsFileId(string path)
    {
        using var handle = NativeMethods.CreateFile(Extended(path), NativeMethods.FileReadAttributes, NativeMethods.ShareAll, IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.OpenReparsePoint | NativeMethods.BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        if (NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileIdInfoClass, out var info, (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>())
            && info.VolumeSerialNumber != 0
            && (info.FileIdLow != 0 || info.FileIdHigh != 0))
        {
            return (info.VolumeSerialNumber, string.Create(CultureInfo.InvariantCulture, $"{info.FileIdHigh:x16}{info.FileIdLow:x16}"));
        }

        if (NativeMethods.GetFileInformationByHandle(handle, out var byHandle)
            && byHandle.VolumeSerialNumber != 0
            && (byHandle.FileIndexLow != 0 || byHandle.FileIndexHigh != 0))
        {
            var index = ((ulong)byHandle.FileIndexHigh << 32) | byHandle.FileIndexLow;
            return (byHandle.VolumeSerialNumber, string.Create(CultureInfo.InvariantCulture, $"{0UL:x16}{index:x16}"));
        }

        return null;
    }

    // A path Win32 takes at any length: \\?\ in front of a long one (a UNC path as \\?\UNC\).
    private static string Extended(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
