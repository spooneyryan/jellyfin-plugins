using System;
using System.Threading;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Cancellation and progress for one clip job while a worker owns it.
/// </summary>
internal sealed class RunningClip : IProgress<double>, IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private double _progress;
    private int _stopReason;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunningClip"/> class.
    /// </summary>
    /// <param name="stoppingToken">The service's shutdown token, linked into the job's token.</param>
    public RunningClip(CancellationToken stoppingToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    }

    /// <summary>
    /// Gets the token that cancels the job.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Gets the latest progress, from 0 to 1.
    /// </summary>
    public double Progress => Volatile.Read(ref _progress);

    /// <summary>
    /// Gets why the job was stopped: the first reason given, or <see cref="ClipStopReason.None"/>.
    /// </summary>
    public ClipStopReason StopReason => (ClipStopReason)Volatile.Read(ref _stopReason);

    /// <inheritdoc />
    public void Report(double value) => Volatile.Write(ref _progress, value);

    /// <summary>
    /// Requests cancellation of the job (someone cancelled it, or deleted its bookmark or clip).
    /// </summary>
    public void Cancel() => Stop(ClipStopReason.Cancelled);

    /// <summary>
    /// Stops the job for a reason, which <see cref="StopReason"/> keeps when it is the first one: the free-space watchdog
    /// stops it with <see cref="ClipStopReason.LowDiskSpace"/>, and the job then fails instead of being cancelled.
    /// </summary>
    /// <param name="reason">Why.</param>
    public void Stop(ClipStopReason reason)
    {
        Interlocked.CompareExchange(ref _stopReason, (int)reason, (int)ClipStopReason.None);
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job finished while we were cancelling it.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cancellation.Dispose();
}
