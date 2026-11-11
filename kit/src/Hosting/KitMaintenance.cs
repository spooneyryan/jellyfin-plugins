using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// A plugin's daily maintenance: runs the registered <see cref="IKitMaintenanceStep"/>s in their
/// <see cref="IKitMaintenanceStep.Order"/>, one after another, each in a try/catch (a failed step is logged, and the next
/// one runs; a step that can't even be created is left out, <see cref="KitComponent{T}"/>), reporting progress; one run at
/// a time. The plugin's public sealed <see cref="KitMaintenanceTaskBase"/> task
/// runs it every day, and Diagnostics' <c>Maintenance</c> entry shows the last run. Before the steps it passes the plugin's
/// gates (<see cref="KitMaintenanceGate"/>), if any, holding each until the run ends.
/// </summary>
internal sealed class KitMaintenance : IDisposable
{
    /// <summary>The logger component, <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Maintenance</c>.</summary>
    public const string LoggerComponent = "Maintenance";

    private readonly Func<IEnumerable<IKitMaintenanceStep>> _steps;
    private readonly IReadOnlyList<(string Name, Func<CancellationToken, Task<IDisposable?>> Hold)> _gates;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _lock = new();
    private IDictionary<string, object?>? _lastRun;
    private long _runs;
    private long _skipped;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitMaintenance"/> class.
    /// </summary>
    /// <param name="steps">The steps, asked at every run: <c>AddKitMaintenance</c> passes those of <c>AddKitMaintenanceStep</c>,
    /// each created on its own, one that can't be created left out and tried again at the next run
    /// (<see cref="KitComponent{T}"/>).</param>
    /// <param name="logger">The logger (<c>Jellyfin.Plugin.&lt;X&gt;.Kit.Maintenance</c>).</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="gates">The plugin's gates, passed in order before the steps (<c>AddKitMaintenanceGate</c>); none by
    /// default.</param>
    public KitMaintenance(
        Func<IEnumerable<IKitMaintenanceStep>> steps,
        ILogger logger,
        TimeProvider? timeProvider = null,
        IEnumerable<(string Name, Func<CancellationToken, Task<IDisposable?>> Hold)>? gates = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(logger);
        _steps = steps;
        _gates = gates?.ToList() ?? [];
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the steps that could be created, in the order they run.
    /// </summary>
    public IReadOnlyList<IKitMaintenanceStep> Steps => _steps().Where(s => s is not null).OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Runs every step, after a run in progress has finished and the plugin's gates let it (a gate that gives no hold skips
    /// the run: <c>Skipped</c> names it).
    /// </summary>
    /// <param name="progress">The task's progress, 0 to 100.</param>
    /// <param name="cancellationToken">Cancels the run at a gate, between and inside steps.</param>
    /// <returns>The run's description (as Diagnostics shows it).</returns>
    public async Task<IDictionary<string, object?>> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        var holds = new List<IDisposable>();
        try
        {
            var started = _timeProvider.GetUtcNow().UtcDateTime;
            var clock = Stopwatch.StartNew();
            var skippedBy = await PassGatesAsync(holds, cancellationToken).ConfigureAwait(false);
            if (skippedBy is not null)
            {
                var skipped = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["StartedUtc"] = started,
                    ["Milliseconds"] = clock.ElapsedMilliseconds,
                    ["Skipped"] = skippedBy,
                    ["Failed"] = 0,
                    ["Steps"] = new List<IDictionary<string, object?>>()
                };
                lock (_lock)
                {
                    _lastRun = skipped;
                    _skipped++;
                }

                _logger.LogWarning("Maintenance skipped: {Gate} didn't let it run (the plugin's own work held it); the next run tries again", skippedBy);
                progress?.Report(100);
                return skipped;
            }

            var steps = Steps;
            var results = new List<IDictionary<string, object?>>();
            for (var i = 0; i < steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[i];
                var stepClock = Stopwatch.StartNew();
                var entry = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Name"] = step.Name, ["Order"] = step.Order };
                try
                {
                    entry["Result"] = await step.RunAsync(cancellationToken).ConfigureAwait(false);
                    entry["Outcome"] = "Done";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The maintenance step {Step} failed; the next step runs", step.Name);
                    entry["Outcome"] = "Failed";
                    entry["Error"] = ex.Message;
                }

                entry["Milliseconds"] = stepClock.ElapsedMilliseconds;
                results.Add(entry);
                progress?.Report(100.0 * (i + 1) / steps.Count);
            }

            var run = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["StartedUtc"] = started,
                ["Milliseconds"] = clock.ElapsedMilliseconds,
                ["Failed"] = results.Count(r => Equals(r["Outcome"], "Failed")),
                ["Steps"] = results
            };
            lock (_lock)
            {
                _lastRun = run;
                _runs++;
            }

            _logger.LogInformation(
                "Maintenance finished: {Steps} steps in {Milliseconds} ms, {Failed} failed",
                results.Count,
                clock.ElapsedMilliseconds,
                run["Failed"]);
            progress?.Report(100);
            return run;
        }
        finally
        {
            for (var i = holds.Count - 1; i >= 0; i--)
            {
                try
                {
                    holds[i].Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A maintenance gate's hold couldn't be released");
                }
            }

            _running.Release();
        }
    }

    /// <summary>
    /// Describes the maintenance for Diagnostics: its steps (those that can be created), its gates and the last run.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        var steps = Steps.Select(s => $"{s.Order} {s.Name}").ToList();
        lock (_lock)
        {
            var described = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Steps"] = steps,
                ["Runs"] = _runs,
                ["LastRun"] = _lastRun
            };
            if (_gates.Count > 0)
            {
                described["Gates"] = _gates.Select(g => g.Name).ToList();
                described["Skipped"] = _skipped;
            }

            return described;
        }
    }

    // Takes each gate's hold in order; the name of the first that gives none (the run is skipped, the holds taken
    // released by the caller). A gate that throws is logged and passed: the maintenance goes ahead without it.
    private async Task<string?> PassGatesAsync(List<IDisposable> holds, CancellationToken cancellationToken)
    {
        foreach (var (name, hold) in _gates)
        {
            IDisposable? held;
            try
            {
                held = await hold(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The maintenance gate {Gate} failed; the run goes ahead without it", name);
                continue;
            }

            if (held is null)
            {
                return name;
            }

            holds.Add(held);
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose() => _running.Dispose();
}
