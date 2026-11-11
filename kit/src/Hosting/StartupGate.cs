using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The startup gate: background work waits until Jellyfin's core startup has completed.
/// </summary>
/// <remarks>
/// <para>Hosted services start before Jellyfin runs its startup tasks, which register the scheduled tasks and locate ffmpeg;
/// <c>IServerApplicationHost.CoreStartupHasCompleted</c> turns true after them (<c>v10.11.5:
/// Emby.Server.Implementations/ApplicationHost.cs:426</c>). Nothing heavy runs before the gate opens, and event handlers
/// that only enqueue are subscribed after it.</para>
/// <para>It polls once a second on the given clock; the flag has no event. <see cref="KitBackgroundService"/> waits on it
/// before its <c>RunAsync</c>.</para>
/// </remarks>
internal static class StartupGate
{
    /// <summary>How often the flag is read while startup runs.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets a value indicating whether Jellyfin's core startup has completed.
    /// </summary>
    /// <param name="applicationHost">The server.</param>
    /// <returns><c>true</c> once the startup tasks have run.</returns>
    public static bool IsOpen(IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(applicationHost);
        return applicationHost.CoreStartupHasCompleted;
    }

    /// <summary>
    /// Waits until Jellyfin's core startup has completed, reading the flag every second.
    /// </summary>
    /// <param name="applicationHost">The server.</param>
    /// <param name="cancellationToken">Cancelled when the server stops.</param>
    /// <returns>A task that completes when the gate is open.</returns>
    public static Task WaitForCoreStartupAsync(IServerApplicationHost applicationHost, CancellationToken cancellationToken)
        => WaitForCoreStartupAsync(applicationHost, TimeProvider.System, cancellationToken);

    /// <summary>
    /// Waits until Jellyfin's core startup has completed, reading the flag every <see cref="PollInterval"/> of the given
    /// clock.
    /// </summary>
    /// <param name="applicationHost">The server.</param>
    /// <param name="timeProvider">The clock whose timers pace the polling (a fake one in tests).</param>
    /// <param name="cancellationToken">Cancelled when the server stops.</param>
    /// <returns>A task that completes when the gate is open; cancelled with the token.</returns>
    public static async Task WaitForCoreStartupAsync(IServerApplicationHost applicationHost, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(timeProvider);
        while (!applicationHost.CoreStartupHasCompleted)
        {
            await Task.Delay(PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
