using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The base of every plugin <see cref="BackgroundService"/>: it waits on the
/// <see cref="StartupGate"/>, then runs <see cref="RunAsync"/>, and no exception ever leaves it.
/// </summary>
/// <remarks>
/// <para>Jellyfin 10.11.5 sets no <c>BackgroundServiceExceptionBehavior</c>, so .NET's default since 6.0, <c>StopHost</c>,
/// applies: an exception escaping a plugin's <c>ExecuteAsync</c> stops the whole server. Here
/// every exception is caught except the cancellation of shutdown. A failed <see cref="RunAsync"/> is logged as an Error
/// and, unless <see cref="RestartAfterFailure"/> is false, runs again after <see cref="FirstRestartDelay"/> (30 s),
/// then after twice as long each time it fails again, at most
/// <see cref="MaxRestartDelay"/>; a run that lasted longer than that starts the delays again at 30 s. A run that returns
/// ends the service.</para>
/// <para><c>ExecuteAsync</c> yields before anything else, so a synchronous throw can't reach
/// <see cref="BackgroundService.StartAsync"/> (which would fail the host's start), and nothing runs before the gate: the
/// startup tasks have located ffmpeg and registered the scheduled tasks by then. The service is registered only through
/// the plugin's <c>RegisterServices</c>, after the ABI guard, so an inert build never starts it.
/// Repo.Tests' <c>KitAdoptionTests</c> checks that every <see cref="BackgroundService"/> of a plugin derives from it.</para>
/// </remarks>
internal abstract class KitBackgroundService : BackgroundService
{
    /// <summary>The wait before a failed <see cref="RunAsync"/> runs again the first time.</summary>
    public static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait before a failed <see cref="RunAsync"/> runs again.</summary>
    public static readonly TimeSpan MaxRestartDelay = TimeSpan.FromMinutes(30);

    private readonly IServerApplicationHost _applicationHost;
    private readonly CancellationTokenSource _stopping = new();
    private int _failures;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitBackgroundService"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="logger">Where failures are logged.</param>
    /// <param name="timeProvider">The clock of the gate and the restart delays; the system clock by default.</param>
    protected KitBackgroundService(IServerApplicationHost applicationHost, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(logger);
        _applicationHost = applicationHost;
        Logger = logger;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets how many times <see cref="RunAsync"/> failed in this process.
    /// </summary>
    public int Failures => Volatile.Read(ref _failures);

    /// <summary>
    /// Gets the logger failures go to.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the clock of the gate and the restart delays.
    /// </summary>
    protected TimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the name the log lines use: the class name by default.
    /// </summary>
    protected virtual string ServiceName => GetType().Name;

    /// <summary>
    /// Gets a value indicating whether a failed <see cref="RunAsync"/> runs again after a delay (the default); when false,
    /// the service ends after logging the failure.
    /// </summary>
    protected virtual bool RestartAfterFailure => true;

    /// <summary>
    /// Gets a token cancelled when the host stops the service, also for work that doesn't run inside
    /// <see cref="RunAsync"/> (a call from a post-scan task, say).
    /// </summary>
    protected CancellationToken Stopping => _stopping.Token;

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_disposed)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _stopping.Dispose();
        }

        base.Dispose();
    }

    /// <summary>
    /// The service's work, once the startup gate is open. It may run again after it failed (above).
    /// </summary>
    /// <param name="stoppingToken">Cancelled when the host stops.</param>
    /// <returns>A task that completes when the work is done or the host stops.</returns>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Never hold up the host's start, and never throw into it.
            await Task.Yield();
            await StartupGate.WaitForCoreStartupAsync(_applicationHost, TimeProvider, stoppingToken).ConfigureAwait(false);

            var delay = FirstRestartDelay;
            while (true)
            {
                var started = TimeProvider.GetUtcNow();
                try
                {
                    await RunAsync(stoppingToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failures);
                    if (TimeProvider.GetUtcNow() - started > MaxRestartDelay)
                    {
                        delay = FirstRestartDelay;
                    }

                    if (!RestartAfterFailure)
                    {
                        Logger.LogError(ex, "{Service} failed and stopped; the server keeps running", ServiceName);
                        return;
                    }

                    Logger.LogError(ex, "{Service} failed; it runs again in {Seconds} s, and the server keeps running", ServiceName, delay.TotalSeconds);
                }

                await Task.Delay(delay, TimeProvider, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRestartDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // The gate, a delay or a log line failed. Nothing may leave ExecuteAsync.
            try
            {
                Logger.LogError(ex, "{Service} stopped after an unexpected error; the server keeps running", ServiceName);
            }
            catch (Exception)
            {
                // Nothing left to tell.
            }
        }
    }
}
