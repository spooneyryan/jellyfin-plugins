using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The Win32 calls of the free-space probe (<see cref="DriveSpaceProbe"/>). .NET's <see cref="System.IO.DriveInfo"/>
/// takes only a drive letter's root, so through a junction, symbolic link or mount point it reports the wrong volume,
/// and it refuses UNC and volume GUID paths; <c>GetDiskFreeSpaceExW</c> on the folder itself reports the volume the
/// folder is on. Since 1.2.0 also those of a clip file's identity (its file id and volume,
/// <see cref="ClipFileIdentity"/>) and of a rename made durable (<c>FlushFileBuffers</c> on a folder), for
/// <see cref="PhysicalClipFileSystem"/> only.
/// </summary>
internal static class NativeMethods
{
    /// <summary>
    /// <c>SEM_FAILCRITICALERRORS</c>: a drive without a disk answers an error instead of showing "There is no disk in the
    /// drive" on the server's desktop.
    /// </summary>
    public const uint SemFailCriticalErrors = 0x0001;

    /// <summary>
    /// <c>SEM_NOOPENFILEERRORBOX</c>: no dialog when a file can't be found either.
    /// </summary>
    public const uint SemNoOpenFileErrorBox = 0x8000;

    /// <summary>
    /// <c>FILE_READ_ATTRIBUTES</c>: enough to read a file's id, whatever sharing the handles open on it allow.
    /// </summary>
    public const uint FileReadAttributes = 0x80;

    /// <summary>
    /// <c>GENERIC_WRITE</c>, which <c>FlushFileBuffers</c> needs.
    /// </summary>
    public const uint GenericWrite = 0x40000000;

    /// <summary>
    /// <c>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE</c>: nobody else's use of the file is in the way.
    /// </summary>
    public const uint ShareAll = 0x7;

    /// <summary>
    /// <c>OPEN_EXISTING</c>.
    /// </summary>
    public const uint OpenExisting = 3;

    /// <summary>
    /// <c>FILE_FLAG_BACKUP_SEMANTICS</c>, which opening a folder needs.
    /// </summary>
    public const uint BackupSemantics = 0x02000000;

    /// <summary>
    /// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>: a link itself, never what it points to.
    /// </summary>
    public const uint OpenReparsePoint = 0x00200000;

    /// <summary>
    /// <c>FileIdInfo</c> of <c>FILE_INFO_BY_HANDLE_CLASS</c>.
    /// </summary>
    public const int FileIdInfoClass = 18;

    /// <summary>
    /// <c>GetDiskFreeSpaceExW</c>: the free space of the volume a folder is on.
    /// </summary>
    /// <param name="directoryName">A folder on the volume, with a trailing backslash (which UNC paths need).</param>
    /// <param name="freeBytesAvailableToCaller">The bytes the server's account may still write (quotas applied).</param>
    /// <param name="totalNumberOfBytes">The bytes the account may use in all.</param>
    /// <param name="totalNumberOfFreeBytes">The volume's free bytes.</param>
    /// <returns>Whether it worked; <see cref="Marshal.GetLastPInvokeError"/> says why not.</returns>
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    /// <summary>
    /// <c>GetVolumePathNameW</c>: the mount point of the volume a path is on (<c>C:\</c>, <c>D:\</c> through a junction to D:,
    /// or the folder a volume is mounted into), for messages only: it isn't reliable enough to probe with.
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
    /// <c>CreateFileW</c>, for a handle .NET's own calls don't open: a file with no access but its attributes, a folder.
    /// </summary>
    /// <param name="fileName">The path.</param>
    /// <param name="desiredAccess">The access.</param>
    /// <param name="shareMode">The sharing.</param>
    /// <param name="securityAttributes">No security attributes (zero).</param>
    /// <param name="creationDisposition"><see cref="OpenExisting"/>.</param>
    /// <param name="flagsAndAttributes">The flags.</param>
    /// <param name="templateFile">No template file (zero).</param>
    /// <returns>The handle; invalid when it failed.</returns>
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>
    /// <c>GetFileInformationByHandleEx</c> with <see cref="FileIdInfoClass"/>: the 128-bit file id and the volume's serial
    /// number (Windows 8 and later; NTFS and ReFS).
    /// </summary>
    /// <param name="file">The handle.</param>
    /// <param name="informationClass"><see cref="FileIdInfoClass"/>.</param>
    /// <param name="information">The answer.</param>
    /// <param name="bufferSize">Its size.</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo information, uint bufferSize);

    /// <summary>
    /// <c>GetFileInformationByHandle</c>: the 64-bit file index and the volume's serial number, where the call above isn't
    /// answered.
    /// </summary>
    /// <param name="file">The handle.</param>
    /// <param name="information">The answer.</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>
    /// <c>FlushFileBuffers</c>: on NTFS also the volume's log, so a rename made before it is on the disk once it returns.
    /// </summary>
    /// <param name="file">A handle opened with <see cref="GenericWrite"/>.</param>
    /// <returns>Whether it worked.</returns>
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle file);

    /// <summary>
    /// <c>FILE_ID_INFO</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        /// <summary>The volume's serial number.</summary>
        public ulong VolumeSerialNumber;

        /// <summary>The id's low 64 bits.</summary>
        public ulong FileIdLow;

        /// <summary>The id's high 64 bits.</summary>
        public ulong FileIdHigh;
    }

    /// <summary>
    /// <c>BY_HANDLE_FILE_INFORMATION</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        /// <summary>The attributes.</summary>
        public uint FileAttributes;

        /// <summary>The creation time, low part.</summary>
        public uint CreationTimeLow;

        /// <summary>The creation time, high part.</summary>
        public uint CreationTimeHigh;

        /// <summary>The last access time, low part.</summary>
        public uint LastAccessTimeLow;

        /// <summary>The last access time, high part.</summary>
        public uint LastAccessTimeHigh;

        /// <summary>The last write time, low part.</summary>
        public uint LastWriteTimeLow;

        /// <summary>The last write time, high part.</summary>
        public uint LastWriteTimeHigh;

        /// <summary>The volume's serial number.</summary>
        public uint VolumeSerialNumber;

        /// <summary>The size, high part.</summary>
        public uint FileSizeHigh;

        /// <summary>The size, low part.</summary>
        public uint FileSizeLow;

        /// <summary>The number of links.</summary>
        public uint NumberOfLinks;

        /// <summary>The file index, high part.</summary>
        public uint FileIndexHigh;

        /// <summary>The file index, low part.</summary>
        public uint FileIndexLow;
    }
}
