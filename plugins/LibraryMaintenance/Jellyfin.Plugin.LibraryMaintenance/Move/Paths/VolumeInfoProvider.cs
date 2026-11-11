using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// The Win32 <see cref="IVolumeInfoProvider"/>: <c>GetVolumePathNameW</c> for the mount point (it follows a
/// junction to its target's volume), <c>GetVolumeNameForVolumeMountPointW</c> for the volume GUID, <c>GetDiskFreeSpaceExW</c>
/// for the free space, with the thread's error mode quiet. A device, UNC or mapped-drive path is never passed to Win32
/// (<see cref="PathRules.Classify"/>); off Windows every answer is null.
/// </summary>
internal sealed class VolumeInfoProvider : IVolumeInfoProvider
{
    /// <summary>
    /// The development builds' stand-in for a second volume (Debug only, never in a release): <c>path=name;…</c>, each path
    /// and everything under it reported on a made-up volume <c>\\?\Volume{jflm-test-name}\</c>, for the sandbox's
    /// cross-volume checks without writing to another drive (a fake volume provider).
    /// </summary>
    public const string TestVolumesVariable = "JFLM_MOVE_TEST_VOLUMES";

    private readonly Func<string, bool>? _isMappedNetworkDrive;
    private readonly IReadOnlyList<(string Path, string Name)> _testVolumes;

    /// <summary>
    /// Initializes a new instance of the <see cref="VolumeInfoProvider"/> class.
    /// </summary>
    /// <param name="isMappedNetworkDrive">Whether a drive root is mapped to a share; by default Windows is asked.</param>
    /// <param name="testVolumes">The made-up volumes (tests; Debug builds also read <see cref="TestVolumesVariable"/>).</param>
    public VolumeInfoProvider(Func<string, bool>? isMappedNetworkDrive = null, IReadOnlyList<(string Path, string Name)>? testVolumes = null)
    {
        _isMappedNetworkDrive = isMappedNetworkDrive;
        _testVolumes = testVolumes ?? ReadTestVolumes();
    }

    /// <inheritdoc />
    public VolumeInfo? GetVolume(string path)
    {
        if (!OperatingSystem.IsWindows() || PathRules.Classify(path, _isMappedNetworkDrive) != LocationKind.Local)
        {
            return null;
        }

        foreach (var (root, name) in _testVolumes)
        {
            if (PathRules.IsSameOrUnder(path, root))
            {
                return new VolumeInfo(PathRules.TrimEnd(root), "\\\\?\\Volume{jflm-test-" + name + "}\\", null, "test " + name);
            }
        }

        var existing = NearestExisting(path);
        if (existing is null)
        {
            return null;
        }

        NativeMethods.SetThreadErrorMode(NativeMethods.QuietErrorMode, out var oldMode);
        try
        {
            var buffer = new char[1024];
            if (!NativeMethods.GetVolumePathName(existing, buffer, (uint)buffer.Length))
            {
                return null;
            }

            var mountPoint = new string(buffer).TrimEnd('\0');
            if (!mountPoint.EndsWith('\\'))
            {
                mountPoint += "\\";
            }

            var guid = new char[64];
            if (!NativeMethods.GetVolumeNameForVolumeMountPoint(mountPoint, guid, (uint)guid.Length))
            {
                return null;
            }

            long? free = NativeMethods.GetDiskFreeSpaceEx(mountPoint, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            return new VolumeInfo(mountPoint, new string(guid).TrimEnd('\0'), free, Label(mountPoint));
        }
        finally
        {
            NativeMethods.SetThreadErrorMode(oldMode, out _);
        }
    }

    // The deepest folder of the path that exists (the path itself when it does): a destination's new folders don't yet.
    private static string? NearestExisting(string path)
    {
        try
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static string? Label(string mountPoint)
    {
        if (mountPoint.Length != 3)
        {
            return null;
        }

        try
        {
            var label = new DriveInfo(mountPoint).VolumeLabel;
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            return null;
        }
    }

    private static List<(string Path, string Name)> ReadTestVolumes()
    {
        var result = new List<(string Path, string Name)>();
#if DEBUG
        var value = Environment.GetEnvironmentVariable(TestVolumesVariable);
        foreach (var entry in (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = entry.LastIndexOf('=');
            if (at > 0 && at < entry.Length - 1 && PathRules.Classify(entry[..at]) == LocationKind.Local)
            {
                result.Add((entry[..at], entry[(at + 1)..]));
            }
        }
#endif
        return result;
    }
}
