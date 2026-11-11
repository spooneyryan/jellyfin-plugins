using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.UserRatings.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// At the start, deletes the data jobs' files a kill left in <c>tmp/</c>: an export's file,
/// which holds every review, and an upload cut short. No job survives a restart, so none of them is held. A failure is logged
/// and never stops the server; the daily maintenance's <see cref="RatingsData.TempStep"/> step tries again.
/// </summary>
internal sealed class DataTempSweep : IHostedService
{
    private readonly Func<RatingsData> _data;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataTempSweep"/> class.
    /// </summary>
    /// <param name="data">The Data tab's server side, created at the start.</param>
    /// <param name="logger">The logger.</param>
    public DataTempSweep(Func<RatingsData> data, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(logger);
        _data = data;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _data().SweepTemp();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The data jobs' files in tmp/ couldn't be swept at the start; the daily maintenance tries again");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
