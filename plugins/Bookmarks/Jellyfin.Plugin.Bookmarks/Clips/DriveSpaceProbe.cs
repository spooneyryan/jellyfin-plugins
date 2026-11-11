using System;
using System.Collections.Frozen;
using System.IO;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The free-space probe: the nearest existing folder at or above the given one, probed with <c>GetDiskFreeSpaceExW</c>
/// and a trailing backslash on Windows (never <see cref="DriveInfo"/>, which reports the drive letter's volume through
/// a junction or mount point and refuses UNC paths), <see cref="DriveInfo"/> elsewhere.
/// </summary>
/// <remarks>
/// <para><b>Why the walk.</b> A per-user clip folder, and a newly configured clip folder, don't exist before the first clip
/// is written there, and the call fails on a missing folder (error 3). The nearest existing folder above it is on the volume
/// the new folder will be created on, also for a folder under a junction or a mounted volume.</para>
/// <para><b>Classification</b>: no existing folder at or above it (a drive letter that is gone, a share or server
/// that doesn't exist) is <see cref="DriveSpaceStatus.Unavailable"/>; so is a call that fails on an existing folder with an
/// error saying the path or its volume is gone (<see cref="GoneErrors"/>: a junction or mount point whose target volume is
/// offline, which <see cref="Directory.Exists"/> still reports as there). Any other failure (access denied,
/// …) is <see cref="DriveSpaceStatus.Unknown"/>: the guard is off for the folder.</para>
/// </remarks>
internal sealed class DriveSpaceProbe : IDriveSpaceProbe
{
    /// <summary>
    /// The Win32 errors that say a path or its volume is gone: <c>ERROR_FILE_NOT_FOUND</c> (2),
    /// <c>ERROR_PATH_NOT_FOUND</c> (3), <c>ERROR_INVALID_DRIVE</c> (15), <c>ERROR_NOT_READY</c> (21),
    /// <c>ERROR_BAD_NETPATH</c> (53), <c>ERROR_DEV_NOT_EXIST</c> (55), <c>ERROR_BAD_NET_NAME</c> (67),
    /// <c>ERROR_NETWORK_UNREACHABLE</c> (1231), <c>ERROR_CANT_ACCESS_FILE</c> (1920) and <c>ERROR_NOT_CONNECTED</c> (2250).
    /// </summary>
    public static readonly FrozenSet<int> GoneErrors = FrozenSet.ToFrozenSet([2, 3, 15, 21, 53, 55, 67, 1231, 1920, 2250]);

    private readonly IDriveSpaceCalls _calls;

    /// <summary>
    /// Initializes a new instance of the <see cref="DriveSpaceProbe"/> class with the operating system's calls.
    /// </summary>
    public DriveSpaceProbe()
        : this(OperatingSystem.IsWindows() ? new WindowsCalls() : new PortableCalls())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DriveSpaceProbe"/> class over given calls (tests).
    /// </summary>
    /// <param name="calls">The calls.</param>
    internal DriveSpaceProbe(IDriveSpaceCalls calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        _calls = calls;
    }

    /// <inheritdoc />
    public DriveSpace Get(string folder)
    {
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // Not a path at all: nothing could be written there.
            return new DriveSpace { Status = DriveSpaceStatus.Unavailable, Folder = folder ?? string.Empty };
        }

        var probed = NearestExistingFolder(full);
        if (probed is null)
        {
            return new DriveSpace { Status = DriveSpaceStatus.Unavailable, Folder = full };
        }

        var call = _calls.GetFreeSpace(WithTrailingSeparator(probed));
        if (!call.Ok)
        {
            return new DriveSpace
            {
                Status = GoneErrors.Contains(call.Error) ? DriveSpaceStatus.Unavailable : DriveSpaceStatus.Unknown,
                Folder = full,
                ProbedPath = probed,
                Error = call.Error
            };
        }

        return new DriveSpace
        {
            Status = DriveSpaceStatus.Ok,
            Folder = full,
            ProbedPath = probed,
            Volume = _calls.GetVolume(probed),
            AvailableBytes = Math.Max(0, call.AvailableBytes),
            TotalBytes = Math.Max(0, call.TotalBytes)
        };
    }

    /// <summary>
    /// Adds the separator <c>GetDiskFreeSpaceExW</c> documents for a folder (UNC share roots need it; it was found harmless
    /// everywhere else).
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The folder ending in a separator.</returns>
    internal static string WithTrailingSeparator(string folder)
        => Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;

    private static long ToBytes(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    // The folder itself or the nearest one above it that exists; null when none does (a drive letter or share that is gone).
    private string? NearestExistingFolder(string full)
    {
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (_calls.FolderExists(current))
            {
                return current;
            }
        }

        return null;
    }

    // Windows: GetDiskFreeSpaceExW, with dialogs for a drive without a disk turned off for the call.
    private sealed class WindowsCalls : IDriveSpaceCalls
    {
        public bool FolderExists(string path) => Directory.Exists(path);

        public DriveSpaceCall GetFreeSpace(string folder)
        {
            var restore = NativeMethods.SetThreadErrorMode(NativeMethods.SemFailCriticalErrors | NativeMethods.SemNoOpenFileErrorBox, out var previous);
            try
            {
                return NativeMethods.GetDiskFreeSpaceEx(folder, out var available, out var total, out _)
                    ? DriveSpaceCall.Worked(ToBytes(available), ToBytes(total))
                    : DriveSpaceCall.Failed(Marshal.GetLastPInvokeError());
            }
            finally
            {
                if (restore)
                {
                    NativeMethods.SetThreadErrorMode(previous, out _);
                }
            }
        }

        public string? GetVolume(string folder)
        {
            var buffer = new char[1024];
            var restore = NativeMethods.SetThreadErrorMode(NativeMethods.SemFailCriticalErrors | NativeMethods.SemNoOpenFileErrorBox, out var previous);
            try
            {
                if (!NativeMethods.GetVolumePathName(folder, buffer, (uint)buffer.Length))
                {
                    // A subst drive's root, for one: its root names the volume well enough for a message.
                    return Path.GetPathRoot(folder);
                }

                var end = Array.IndexOf(buffer, '\0');
                return new string(buffer, 0, end < 0 ? buffer.Length : end);
            }
            finally
            {
                if (restore)
                {
                    NativeMethods.SetThreadErrorMode(previous, out _);
                }
            }
        }
    }

    // Elsewhere: DriveInfo, whose statvfs call works on any folder of the file system.
    private sealed class PortableCalls : IDriveSpaceCalls
    {
        public bool FolderExists(string path) => Directory.Exists(path);

        public DriveSpaceCall GetFreeSpace(string folder)
        {
            try
            {
                var drive = new DriveInfo(folder);
                return DriveSpaceCall.Worked(drive.AvailableFreeSpace, drive.TotalSize);
            }
            catch (Exception ex) when (ex is DriveNotFoundException or DirectoryNotFoundException)
            {
                return DriveSpaceCall.Failed(3);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return DriveSpaceCall.Failed(ex.HResult);
            }
        }

        public string? GetVolume(string folder)
        {
            try
            {
                return new DriveInfo(folder).RootDirectory.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }
}
