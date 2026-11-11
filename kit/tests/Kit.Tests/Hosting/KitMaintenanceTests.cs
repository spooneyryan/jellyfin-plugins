using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// The daily maintenance: <see cref="KitMaintenance"/> runs its steps in order, one failure costing only
/// that step, one run at a time; <see cref="KitMaintenanceTaskBase"/> names the task after the plugin, triggers it daily at
/// the plugin's time, and does nothing for an inert build or without a maintenance.
/// </summary>
public sealed class KitMaintenanceTests
{
    private readonly ListLogger _logger = new();

    [Fact]
    public async Task TheSteps_RunInOrder_AndAFailingOneCostsOnlyItself()
    {
        var ran = new List<string>();
        using var maintenance = new KitMaintenance(
            () =>
            [
                Step("VerifySweep", KitMaintenanceOrder.VerifySweep, ran),
                Step("Users", KitMaintenanceOrder.Users, ran, fail: true),
                Step("Snapshots", KitMaintenanceOrder.Snapshots, ran),
                Step("Archive", KitMaintenanceOrder.Archive, ran),
            ],
            _logger);
        var progress = new List<double>();

        var run = await maintenance.RunAsync(new SyncProgress(progress), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Snapshots", "Archive", "Users", "VerifySweep" }, ran);
        var steps = (List<IDictionary<string, object?>>)run["Steps"]!;
        Assert.Equal(new[] { "Done", "Done", "Failed", "Done" }, steps.Select(s => s["Outcome"]));
        Assert.Equal("broken", steps[2]["Error"]);
        Assert.Equal(1, run["Failed"]);
        Assert.Equal(new[] { 25.0, 50.0, 75.0, 100.0, 100.0 }, progress);
        Assert.Single(_logger.At(LogLevel.Error));
        Assert.Equal(1L, maintenance.Describe()["Runs"]);
        Assert.Same(run, maintenance.Describe()["LastRun"]);
    }

    [Fact]
    public async Task ACancelledRun_StopsBetweenSteps()
    {
        using var cancel = new CancellationTokenSource();
        var ran = new List<string>();
        using var maintenance = new KitMaintenance(
            () => [new KitMaintenanceStep("First", 1, async _ => { ran.Add("First"); await cancel.CancelAsync(); return null; }), Step("Second", 2, ran)],
            _logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenance.RunAsync(null, cancel.Token));

        Assert.Equal(new[] { "First" }, ran);
    }

    [Fact]
    public async Task Runs_DontOverlap()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        using var maintenance = new KitMaintenance(
            () => [new KitMaintenanceStep("Slow", 1, async _ =>
            {
                most = Math.Max(most, Interlocked.Increment(ref running));
                await gate.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref running);
                return null;
            })],
            _logger);

        var first = maintenance.RunAsync(null, TestContext.Current.CancellationToken);
        var second = maintenance.RunAsync(null, TestContext.Current.CancellationToken);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, most);
        Assert.Equal(2L, maintenance.Describe()["Runs"]);
    }

    [Fact]
    public async Task AGatesHold_IsHeldThroughTheSteps_AndReleasedWhenTheRunEnds_AFailedStepToo()
    {
        // User Ratings' restore and data jobs write the database, then the archive; the Archive step mustn't run
        // between the two.
        var ran = new List<string>();
        var held = 0;
        var heldDuringSteps = new List<int>();
        using var maintenance = new KitMaintenance(
            () =>
            [
                new KitMaintenanceStep("First", 1, _ => { heldDuringSteps.Add(held); return Task.FromResult<IDictionary<string, object?>?>(null); }),
                Step("Broken", 2, ran, fail: true),
                new KitMaintenanceStep("Last", 3, _ => { heldDuringSteps.Add(held); return Task.FromResult<IDictionary<string, object?>?>(null); }),
            ],
            _logger,
            gates:
            [
                ("Jobs", _ => { held++; return Task.FromResult<IDisposable?>(new Released(() => held--)); }),
                ("Other", _ => { held++; return Task.FromResult<IDisposable?>(new Released(() => held--)); }),
            ]);

        var run = await maintenance.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 2, 2 }, heldDuringSteps);
        Assert.Equal(0, held);
        Assert.Equal(1, run["Failed"]);
        Assert.False(run.ContainsKey("Skipped"));
        var described = maintenance.Describe();
        Assert.Equal(new[] { "Jobs", "Other" }, (List<string>)described["Gates"]!);
        Assert.Equal((1L, 0L), ((long)described["Runs"]!, (long)described["Skipped"]!));
    }

    [Fact]
    public async Task AGateThatGivesNoHold_SkipsTheRun_NamesIt_AndReleasesTheHoldsTaken()
    {
        var ran = new List<string>();
        var held = 0;
        using var maintenance = new KitMaintenance(
            () => [Step("Archive", KitMaintenanceOrder.Archive, ran)],
            _logger,
            gates:
            [
                ("First", _ => { held++; return Task.FromResult<IDisposable?>(new Released(() => held--)); }),
                ("Jobs", _ => Task.FromResult<IDisposable?>(null)),
                ("Never", _ => throw new InvalidOperationException("not asked")),
            ]);
        var progress = new List<double>();

        var run = await maintenance.RunAsync(new SyncProgress(progress), TestContext.Current.CancellationToken);

        Assert.Empty(ran);
        Assert.Equal(0, held);
        Assert.Equal("Jobs", run["Skipped"]);
        Assert.Empty((List<IDictionary<string, object?>>)run["Steps"]!);
        Assert.Equal(new[] { 100.0 }, progress);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("Maintenance skipped: Jobs", StringComparison.Ordinal));
        Assert.Empty(_logger.At(LogLevel.Error));
        var described = maintenance.Describe();
        Assert.Same(run, described["LastRun"]);
        Assert.Equal((0L, 1L), ((long)described["Runs"]!, (long)described["Skipped"]!));
    }

    [Fact]
    public async Task AGateThatThrows_IsLogged_AndTheRunGoesAheadWithoutIt()
    {
        var ran = new List<string>();
        using var maintenance = new KitMaintenance(
            () => [Step("Archive", KitMaintenanceOrder.Archive, ran)],
            _logger,
            gates: [("Jobs", _ => throw new InvalidOperationException("the gate broke"))]);

        var run = await maintenance.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Archive" }, ran);
        Assert.False(run.ContainsKey("Skipped"));
        var error = Assert.Single(_logger.At(LogLevel.Error));
        Assert.Contains("gate Jobs failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRun_StopsAtAGateThatWaits_RunningNoStep()
    {
        using var cancel = new CancellationTokenSource();
        var ran = new List<string>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var maintenance = new KitMaintenance(
            () => [Step("Archive", KitMaintenanceOrder.Archive, ran)],
            _logger,
            gates: [("Jobs", async ct => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; })]);

        var run = maintenance.RunAsync(null, cancel.Token);
        await waiting.Task;
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(ran);
        Assert.Equal(0L, maintenance.Describe()["Skipped"]);
    }

    [Fact]
    public async Task AddKitMaintenanceGate_GivesTheGateTheProvider_AndTheTaskPassesIt()
    {
        var ran = new List<string>();
        IServiceProvider? given = null;
        var released = false;
        using var provider = Provider(services => services
            .AddKitMaintenance()
            .AddKitMaintenanceStep("Only", 1, (_, _) => Ran(ran, "Only"))
            .AddKitMaintenanceGate("Jobs", (p, _) => { given = p; return Task.FromResult<IDisposable?>(new Released(() => released = true)); }));

        await new FxMaintenanceTask(provider).ExecuteAsync(new SyncProgress([]), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Only" }, ran);
        Assert.NotNull(given);
        Assert.True(released);
        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>());
        Assert.Equal(new[] { "Jobs" }, (List<string>)((IDictionary<string, object?>)source.Describe()!)["Gates"]!);
    }

    [Fact]
    public void WithoutAGate_TheDiagnosticsAreAsBefore()
    {
        using var maintenance = new KitMaintenance(() => [], _logger);

        Assert.Equal(new[] { "LastRun", "Runs", "Steps" }, maintenance.Describe().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTask_IsNamedAfterThePlugin_AndRunsDailyAtItsTime()
    {
        var task = new FxMaintenanceTask(new ServiceCollection().BuildServiceProvider());

        Assert.Equal("Kit Test: Maintenance", task.Name);
        Assert.Equal("KitTestMaintenance", task.Key);
        Assert.Equal("Kit Test", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(new TimeSpan(3, 10, 0).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public async Task TheTask_RunsThePluginsMaintenance()
    {
        var ran = new List<string>();
        using var provider = Provider(services => services.AddKitMaintenance().AddKitMaintenance().AddSingleton(Step("Only", 1, ran)).AddKitMaintenanceStep<KitMaintenanceStep>());
        var progress = new List<double>();

        await new FxMaintenanceTask(provider).ExecuteAsync(new SyncProgress(progress), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Only" }, ran);
        Assert.Equal(100.0, progress[^1]);
        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>());
        Assert.Equal(KitMaintenanceServiceCollectionExtensions.DiagnosticsKey, source.Key);
        Assert.Equal(1L, ((IDictionary<string, object?>)source.Describe()!)["Runs"]);
    }

    [Fact]
    public async Task AStepThatCantBeCreated_IsLeftOut_ShownInDiagnostics_AndTriedAgainAtTheNextRun()
    {
        // The steps used to come from one GetServices call cached in a Lazy, so one plugin step whose creation
        // threw once stopped the whole maintenance until a restart.
        var ran = new List<string>();
        var broken = true;
        using var provider = Provider(services => services
            .AddKitMaintenance()
            .AddKitMaintenanceStep("Last", 3, (_, _) => Ran(ran, "Last"))
            .AddSingleton(_ => broken ? throw new InvalidOperationException("no store") : new PluginStep(ran))
            .AddKitMaintenanceStep<PluginStep>()
            .AddKitMaintenanceStep("First", 1, (_, _) => Ran(ran, "First")));
        var maintenance = provider.GetRequiredService<KitMaintenance>();
        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>());

        var run = await maintenance.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "First", "Last" }, ran);
        Assert.Equal(0, run["Failed"]);
        var described = (IDictionary<string, object?>)source.Describe()!;
        Assert.Equal(new[] { "1 First", "3 Last" }, (IEnumerable<string>)described["Steps"]!);
        var unavailable = Assert.Single((IEnumerable<IDictionary<string, object?>>)described["Unavailable"]!);
        Assert.Equal((typeof(PluginStep).FullName, "no store"), ((string?)unavailable["Name"], (string?)unavailable["Problem"]));

        broken = false;
        ran.Clear();
        await maintenance.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "First", "Plugin", "Last" }, ran);
        Assert.Empty((IEnumerable<IDictionary<string, object?>>)((IDictionary<string, object?>)source.Describe()!)["Unavailable"]!);
    }

    [Fact]
    public async Task TheTask_DoesNothing_WithoutAMaintenance_OrForAnInertBuild()
    {
        var progress = new List<double>();
        await new FxMaintenanceTask(Provider(_ => { })).ExecuteAsync(new SyncProgress(progress), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 100.0 }, progress);

        var ran = new List<string>();
        var inert = new ServiceCollection().AddSingleton(new KitAbiStatus("Kit Test", "10.11", "12.1.0", false, "the server is 12.1")).AddSingleton(new KitMaintenance(() => [Step("Never", 1, ran)], _logger)).BuildServiceProvider();
        await new FxMaintenanceTask(inert).ExecuteAsync(new SyncProgress(progress), TestContext.Current.CancellationToken);
        Assert.Empty(ran);
    }

    [Fact]
    public void TheTask_ChecksItsArguments()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BadTimeTask(services));
    }

    private static KitMaintenanceStep Step(string name, int order, List<string> ran, bool fail = false)
        => new(name, order, _ =>
        {
            ran.Add(name);
            return fail ? throw new InvalidOperationException("broken") : Task.FromResult<IDictionary<string, object?>?>(new Dictionary<string, object?> { ["Name"] = name });
        });

    private static Task<IDictionary<string, object?>?> Ran(List<string> ran, string name)
    {
        ran.Add(name);
        return Task.FromResult<IDictionary<string, object?>?>(null);
    }

    private static ServiceProvider Provider(Action<IServiceCollection> configure)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(Path.GetTempPath());
        var services = new ServiceCollection()
            .AddSingleton(paths.Object)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", true, "active"))
            .AddKit(TestKit.Options);
        configure(services);
        return services.BuildServiceProvider();
    }

    private sealed class Released(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private sealed class PluginStep(List<string> ran) : IKitMaintenanceStep
    {
        public string Name => "Plugin";

        public int Order => 2;

        public Task<IDictionary<string, object?>?> RunAsync(CancellationToken cancellationToken) => Ran(ran, Name);
    }

    private sealed class FxMaintenanceTask(IServiceProvider services) : KitMaintenanceTaskBase(services, "Kit Test", new TimeSpan(3, 10, 0));

    private sealed class BadTimeTask(IServiceProvider services) : KitMaintenanceTaskBase(services, "Kit Test", TimeSpan.FromHours(25));
}
