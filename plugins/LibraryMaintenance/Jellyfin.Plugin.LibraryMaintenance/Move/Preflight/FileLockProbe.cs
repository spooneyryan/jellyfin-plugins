using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;

/// <summary>
/// Tells whether anything has a unit's files open (<c>FileLockProbe</c>): each file is opened for reading
/// with <see cref="FileShare.None"/>, which fails while any other handle is open (a player, an ffmpeg transcode, trickplay
/// or chapter extraction, Intro Skipper, a Bookmarks clip job), three tries over three seconds. Stricter than the
/// rename itself (a reader that shares delete doesn't block a file rename), and it is what a folder's rename needs:
/// <c>MoveFileExW</c> refuses a folder with anything open inside (error 5). Folder handles aren't probed; the
/// commit's error 5 covers them. A link is opened itself, never followed (<see cref="IsOpen"/>).
/// </summary>
internal sealed class FileLockProbe
{
    /// <summary>How many times a file is tried.</summary>
    public const int Tries = 3;

    /// <summary>The wait between tries.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, bool> _isOpen;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLockProbe"/> class.
    /// </summary>
    /// <param name="delay">Waits between tries; by default <see cref="Task.Delay(TimeSpan, CancellationToken)"/> (tests pass none).</param>
    /// <param name="isOpen">Tells whether one file is open; by default <see cref="IsOpen"/> (tests record the paths asked).</param>
    public FileLockProbe(Func<TimeSpan, CancellationToken, Task>? delay = null, Func<string, bool>? isOpen = null)
    {
        _delay = delay ?? Task.Delay;
        _isOpen = isOpen ?? IsOpen;
    }

    /// <summary>
    /// Finds the files something has open. The first try covers every file; only the busy ones are tried again.
    /// </summary>
    /// <param name="files">The files.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <returns>The files still open after the last try (a missing file counts as not open: the plan's manifest reports it).</returns>
    public async Task<IReadOnlyList<string>> FindOpenAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var busy = new List<string>(files);
        for (var attempt = 1; attempt <= Tries && busy.Count > 0; attempt++)
        {
            if (attempt > 1)
            {
                await _delay(Pause, cancellationToken).ConfigureAwait(false);
            }

            busy = busy.FindAll(path => _isOpen(path));
        }

        return busy;
    }

    /// <summary>
    /// Gets whether something has a file open now. On Windows the file is opened with <c>CreateFileW</c> and
    /// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so a symbolic link is opened itself and never followed to its target (the
    /// planner blocks links, and the preflight skips them).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>True when it can't be opened exclusively; false when it isn't there.</returns>
    public static bool IsOpen(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsWindows())
        {
            return IsOpenPortable(path);
        }

        using var handle = NativeMethods.CreateFile(NativeMethods.Long(path), NativeMethods.GenericRead, 0, IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return false;
        }

        // Missing: not open (the manifest reports it). Anything else (a sharing violation, a lock, access denied): open.
        return Marshal.GetLastPInvokeError() is not (NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound);
    }

    private static bool IsOpenPortable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
