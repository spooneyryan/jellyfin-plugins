using System;
using System.IO;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;

/// <summary>
/// The Win32 <see cref="IFolderWriteAccess"/>: the volume's read-only flag (<c>GetVolumeInformationW</c>,
/// <c>FILE_READ_ONLY_VOLUME</c>), then <c>CreateFileW</c> on the folder asking <c>FILE_ADD_FILE</c> and/or
/// <c>FILE_ADD_SUBDIRECTORY</c> with <c>FILE_FLAG_BACKUP_SEMANTICS</c> (to open a folder) and
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> (never through a link). Opening a handle is an access check against the folder's ACL
/// and changes nothing: no entry, no last-write time, no watcher event. It can't see a share that refuses writes, a full
/// disk or a privilege that bypasses the ACL; the job-start probe file covers those (<c>DestinationWriteCheck.ProbeFile</c>).
/// A device, UNC or mapped-drive path is never passed to Win32 (<see cref="PathRules.Classify"/>), and off Windows only
/// the folder's existence is told.
/// </summary>
internal sealed class FolderWriteAccess : IFolderWriteAccess
{
    /// <summary><c>FILE_ADD_FILE</c>: create a file in the folder.</summary>
    public const uint FileAddFile = 0x0002;

    /// <summary><c>FILE_ADD_SUBDIRECTORY</c>: create a subfolder in the folder.</summary>
    public const uint FileAddSubdirectory = 0x0004;

    /// <summary><c>FILE_READ_ONLY_VOLUME</c>, a file system flag.</summary>
    public const uint FileReadOnlyVolume = 0x00080000;

    // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE: the check never gets in another handle's way.
    private const uint ShareAll = 0x1 | 0x2 | 0x4;

    private readonly Func<string, bool> _isReadOnlyVolume;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderWriteAccess"/> class.
    /// </summary>
    /// <param name="isReadOnlyVolume">Whether a folder's volume is read-only; by default Windows is asked (tests pass one).</param>
    public FolderWriteAccess(Func<string, bool>? isReadOnlyVolume = null)
    {
        _isReadOnlyVolume = isReadOnlyVolume ?? IsReadOnlyVolume;
    }

    /// <inheritdoc />
    public bool CanAdd(string directory, bool files, bool folders)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!OperatingSystem.IsWindows())
        {
            return Directory.Exists(directory);
        }

        if (PathRules.Classify(directory) != LocationKind.Local || _isReadOnlyVolume(directory))
        {
            return false;
        }

        var access = (files ? FileAddFile : 0) | (folders ? FileAddSubdirectory : 0);
        using var handle = NativeMethods.CreateFile(
            NativeMethods.Long(directory),
            access == 0 ? FileAddFile : access,
            ShareAll,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        return !handle.IsInvalid;
    }

    private static bool IsReadOnlyVolume(string directory)
    {
        NativeMethods.SetThreadErrorMode(NativeMethods.QuietErrorMode, out var oldMode);
        try
        {
            var buffer = new char[1024];
            if (!NativeMethods.GetVolumePathName(directory, buffer, (uint)buffer.Length))
            {
                return false;
            }

            var mountPoint = new string(buffer).TrimEnd('\0');
            if (!mountPoint.EndsWith('\\'))
            {
                mountPoint += "\\";
            }

            return NativeMethods.GetVolumeInformation(mountPoint, null, 0, out _, out _, out var flags, null, 0) && (flags & FileReadOnlyVolume) != 0;
        }
        finally
        {
            NativeMethods.SetThreadErrorMode(oldMode, out _);
        }
    }
}
