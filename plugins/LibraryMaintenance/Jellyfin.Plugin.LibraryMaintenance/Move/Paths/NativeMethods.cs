using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// The Win32 calls of Move's planning: a path's volume mount point and GUID, and
/// its free space (<see cref="VolumeInfoProvider"/>); <c>CreateFileW</c> for the lock probe, which opens a link itself and
/// never what it points at (<see cref="Preflight.FileLockProbe"/>), and for the write check that writes nothing, with a
/// volume's read-only flag (<see cref="Preflight.FolderWriteAccess"/>). <c>MoveFileExW</c> joins them with the job side.
/// Never called with a device, network or mapped-drive path (<see cref="PathRules.Classify"/>).
/// </summary>
internal static class NativeMethods
{
    /// <summary><c>GENERIC_READ</c>.</summary>
    public const uint GenericRead = 0x80000000;

    /// <summary><c>OPEN_EXISTING</c>.</summary>
    public const uint OpenExisting = 3;

    /// <summary><c>FILE_FLAG_OPEN_REPARSE_POINT</c>: open a junction or symbolic link itself, never its target.</summary>
    public const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary><c>FILE_FLAG_BACKUP_SEMANTICS</c>: needed to open a folder.</summary>
    public const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary><c>ERROR_FILE_NOT_FOUND</c>.</summary>
    public const int ErrorFileNotFound = 2;

    /// <summary><c>ERROR_PATH_NOT_FOUND</c>.</summary>
    public const int ErrorPathNotFound = 3;

    /// <summary>
    /// <c>SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX</c>: a drive without a disk answers an error instead of a dialog on
    /// the server's desktop (Bookmarks' precedent).
    /// </summary>
    public const uint QuietErrorMode = 0x0001 | 0x8000;

    /// <summary>
    /// <c>GetVolumePathNameW</c>: the mount point of the volume a path is on (<c>C:\</c>, or the folder a volume is mounted
    /// into). It follows a junction to the target's volume.
    /// </summary>
    /// <param name="fileName">The path.</param>
    /// <param name="volumePathName">Receives the mount point.</param>
    /// <param name="bufferLength">The buffer's length in characters.</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    /// <summary>
    /// <c>GetVolumeNameForVolumeMountPointW</c>: the volume GUID path (<c>\\?\Volume{…}\</c>) of a mount point, the volume's
    /// identity; drive letters are not trusted as one.
    /// </summary>
    /// <param name="volumeMountPoint">The mount point, with its trailing backslash.</param>
    /// <param name="volumeName">Receives the GUID path.</param>
    /// <param name="bufferLength">The buffer's length in characters (50 is enough).</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, uint bufferLength);

    /// <summary>
    /// <c>GetDiskFreeSpaceExW</c>: the free space of the volume a folder is on.
    /// </summary>
    /// <param name="directoryName">A folder on the volume, with a trailing backslash.</param>
    /// <param name="freeBytesAvailableToCaller">The bytes the server's account may still write.</param>
    /// <param name="totalNumberOfBytes">The bytes the account may use in all.</param>
    /// <param name="totalNumberOfFreeBytes">The volume's free bytes.</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    /// <summary>
    /// <c>SetThreadErrorMode</c>: which errors the calling thread answers with a dialog.
    /// </summary>
    /// <param name="newMode">The new mode.</param>
    /// <param name="oldMode">The previous mode, to put back.</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);

    /// <summary>
    /// <c>CreateFileW</c>: opens a file or (with <see cref="FileFlagBackupSemantics"/>) a folder for the access asked, or
    /// fails with the reason in the last error.
    /// </summary>
    /// <param name="fileName">The path.</param>
    /// <param name="desiredAccess">The access asked.</param>
    /// <param name="shareMode">What other handles may do meanwhile (0: nothing, so it fails while one is open).</param>
    /// <param name="securityAttributes">Zero: the default security.</param>
    /// <param name="creationDisposition"><see cref="OpenExisting"/> only: it never creates anything.</param>
    /// <param name="flagsAndAttributes">The flags.</param>
    /// <param name="templateFile">Zero: no template file.</param>
    /// <returns>The handle, invalid on failure.</returns>
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>
    /// <c>GetVolumeInformationW</c>: a volume's flags (<c>FILE_READ_ONLY_VOLUME</c>), the only part asked for.
    /// </summary>
    /// <param name="rootPathName">The mount point, with its trailing backslash.</param>
    /// <param name="volumeNameBuffer">Null: the label isn't asked.</param>
    /// <param name="volumeNameSize">Zero.</param>
    /// <param name="volumeSerialNumber">Receives the serial number.</param>
    /// <param name="maximumComponentLength">Receives the longest name allowed.</param>
    /// <param name="fileSystemFlags">Receives the flags.</param>
    /// <param name="fileSystemNameBuffer">Null: the file system's name isn't asked.</param>
    /// <param name="fileSystemNameSize">0, as no buffer is passed.</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(string rootPathName, [Out] char[]? volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, [Out] char[]? fileSystemNameBuffer, uint fileSystemNameSize);

    /// <summary>
    /// Gets a full local path in the form Win32 takes beyond <c>MAX_PATH</c> (<c>\\?\C:\…</c>) when it is that long.
    /// </summary>
    /// <param name="path">A full local path.</param>
    /// <returns>The path to pass.</returns>
    public static string Long(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return full.Length >= 248 && full[1] == ':' ? @"\\?\" + full : full;
    }
}
