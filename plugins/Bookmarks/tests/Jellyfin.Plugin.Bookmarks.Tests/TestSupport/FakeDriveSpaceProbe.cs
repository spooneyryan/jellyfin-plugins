using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Bookmarks.Clips;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// A free-space probe for tests: by default 1 TiB available on <c>C:\</c>; a test sets another
/// answer, or one per call, and reads which folders were probed.
/// </summary>
internal sealed class FakeDriveSpaceProbe : IDriveSpaceProbe
{
    /// <summary>What the probe answers by default: 1 TiB available.</summary>
    public const long Plenty = 1L << 40;

    private readonly ConcurrentQueue<string> _probed = new();
    private Func<string, DriveSpace> _answer;
    private int _calls;

    public FakeDriveSpaceProbe(long available = Plenty)
    {
        _answer = folder => Ok(folder, available);
    }

    /// <summary>Gets how many times the probe was asked.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Gets the folders asked about, in order.</summary>
    public IReadOnlyList<string> Probed => _probed.ToList();

    /// <summary>An <see cref="DriveSpaceStatus.Ok"/> answer.</summary>
    public static DriveSpace Ok(string folder, long available, long total = 4 * Plenty, string volume = @"C:\")
        => new() { Status = DriveSpaceStatus.Ok, Folder = folder, ProbedPath = folder, Volume = volume, AvailableBytes = available, TotalBytes = total };

    /// <summary>Answers with this much available from now on.</summary>
    public void Available(long bytes, long total = 4 * Plenty, string volume = @"C:\") => Answer(folder => Ok(folder, bytes, total, volume));

    /// <summary>Answers <see cref="DriveSpaceStatus.Unavailable"/> from now on (no existing folder, or a "gone" error).</summary>
    public void Unavailable(int? error = null)
        => Answer(folder => new DriveSpace { Status = DriveSpaceStatus.Unavailable, Folder = folder, Error = error });

    /// <summary>Answers <see cref="DriveSpaceStatus.Unknown"/> from now on (a failing call on an existing folder).</summary>
    public void Unknown(int error = 5)
        => Answer(folder => new DriveSpace { Status = DriveSpaceStatus.Unknown, Folder = folder, ProbedPath = folder, Error = error });

    /// <summary>Answers with a function of the folder from now on.</summary>
    public void Answer(Func<string, DriveSpace> answer) => Volatile.Write(ref _answer, answer);

    public DriveSpace Get(string folder)
    {
        Interlocked.Increment(ref _calls);
        _probed.Enqueue(folder);
        return Volatile.Read(ref _answer)(folder);
    }
}
