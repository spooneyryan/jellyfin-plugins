using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// Writes the <see cref="BlockCounter"/>'s closed windows to the audit log (the counters are flushed every
/// 10 minutes), looking every <see cref="CheckInterval"/>, and every window, the open ones too, when the server stops, so
/// nothing counted is lost on a clean shutdown.
/// </summary>
internal sealed class BlockCounterService : KitBackgroundService
{
    /// <summary>How often the closed windows are looked for.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly BlockCounter _counter;
    private readonly AccessRolesAuditLog _audit;
    private readonly IServerUsers _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockCounterService"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="counter">The counter.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="users">The server's users, for the lines' names.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public BlockCounterService(IServerApplicationHost applicationHost, BlockCounter counter, AccessRolesAuditLog audit, IServerUsers users, ILogger logger, TimeProvider? timeProvider = null)
        : base(applicationHost, logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(users);
        _counter = counter;
        _audit = audit;
        _users = users;
    }

    /// <summary>
    /// Writes the windows that have closed (or all of them).
    /// </summary>
    /// <param name="all">Whether to write the open windows too.</param>
    /// <param name="cancellationToken">Cancels waiting for the audit log.</param>
    /// <returns>How many lines were written.</returns>
    public async Task<int> FlushAsync(bool all, CancellationToken cancellationToken)
    {
        var lines = _counter.Take(all, id => _users.Find(id)?.Name);
        if (lines.Count == 0)
        {
            return 0;
        }

        var written = await _audit.AppendAsync(lines, cancellationToken).ConfigureAwait(false);
        return written.Count;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await FlushAsync(all: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Access Roles couldn't write its blocked-request counts at shutdown");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, TimeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await FlushAsync(all: false, stoppingToken).ConfigureAwait(false);
        }
    }
}
