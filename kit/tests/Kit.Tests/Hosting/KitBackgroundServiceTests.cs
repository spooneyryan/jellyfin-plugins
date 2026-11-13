using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// <see cref="KitBackgroundService"/>: nothing runs before the startup gate, and no
/// exception leaves it, so a failing plugin service never stops the server (Jellyfin keeps .NET's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>); a failed run is logged and runs again after 30 s, then after twice
/// as long each time.
/// </summary>
public sealed class KitBackgroundServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly ManualTimerProvider _clock = new();
    private readonly ListLogger _log = new();
    private bool _started = true;

    public KitBackgroundServiceTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(() => _started);
    }

    [Fact]
    public async Task RunAsync_WaitsForTheStartupGate()
    {
        _started = false;
        using var service = new Probe(_host.Object, _log, _clock, (_, ct) => Task.Delay(Timeout.Infinite, ct));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _clock.WaitForArmedTimersAsync();
        Assert.Equal(0, service.Runs);

        _started = true;
        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.WaitForRunAsync(1);

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_log.At(LogLevel.Error));
    }

    [Fact]
    public async Task ASynchronousThrow_NeitherReachesStartAsync_NorEscapes()
    {
        using var service = new Probe(_host.Object, _log, _clock, (_, _) => throw new InvalidOperationException("boom"), restart: false);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(1, service.Failures);
        var error = Assert.Single(_log.At(LogLevel.Error));
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("Probe failed and stopped; the server keeps running", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRun_RunsAgainAfter30Seconds_ThenAfterTwiceAsLong_UntilOneReturns()
    {
        using var service = new Probe(_host.Object, _log, _clock, async (run, _) =>
        {
            await Task.Yield();
            if (run < 3)
            {
                throw new InvalidOperationException("run " + run);
            }
        });

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.WaitForRunAsync(1);
        await _clock.WaitForArmedTimersAsync();
        Assert.Contains("it runs again in 30 s", Assert.Single(_log.At(LogLevel.Error)).Message, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, service.Runs);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.WaitForRunAsync(2);
        await _clock.WaitForArmedTimersAsync();
        Assert.Contains("it runs again in 60 s", _log.At(LogLevel.Error).Last().Message, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(2, service.Runs);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.WaitForRunAsync(3);
        await service.ExecuteTask!.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // The third run returned: the service is done, and nothing is armed any more.
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(2, service.Failures);
        Assert.Equal(0, _clock.ArmedTimers);
    }

    [Fact]
    public async Task TheDelays_StopDoublingAt30Minutes_AndStartAgainAfterALongRun()
    {
        var release = new TaskCompletionSource();
        using var service = new Probe(_host.Object, _log, _clock, async (run, _) =>
        {
            if (run == 9)
            {
                await release.Task;
            }

            throw new InvalidOperationException("run " + run);
        });

        await service.StartAsync(TestContext.Current.CancellationToken);
        var expected = new[] { 30, 60, 120, 240, 480, 960, 1800, 1800 };
        for (var i = 0; i < expected.Length; i++)
        {
            await service.WaitForRunAsync(i + 1);
            await _clock.WaitForArmedTimersAsync();
            Assert.Contains($"it runs again in {expected[i]} s", _log.At(LogLevel.Error).Last().Message, StringComparison.Ordinal);
            _clock.Advance(TimeSpan.FromSeconds(expected[i]));
        }

        // Run 9 lasts longer than the longest delay before it fails: the delays start again at 30 s.
        await service.WaitForRunAsync(9);
        _clock.Advance(TimeSpan.FromMinutes(31));
        release.SetResult();
        await _clock.WaitForArmedTimersAsync();
        Assert.Contains("it runs again in 30 s", _log.At(LogLevel.Error).Last().Message, StringComparison.Ordinal);

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(9, service.Failures);
    }

    [Fact]
    public async Task Stopping_WhileWaitingForTheGate_OrWhileRunning_IsNoFailure()
    {
        _started = false;
        using (var waiting = new Probe(_host.Object, _log, _clock, (_, ct) => Task.Delay(Timeout.Infinite, ct)))
        {
            await waiting.StartAsync(TestContext.Current.CancellationToken);
            await _clock.WaitForArmedTimersAsync();
            await waiting.StopAsync(TestContext.Current.CancellationToken);
            Assert.True(waiting.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal(0, waiting.Runs);
        }

        _started = true;
        using var running = new Probe(_host.Object, _log, _clock, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await running.StartAsync(TestContext.Current.CancellationToken);
        await running.WaitForRunAsync(1);
        Assert.False(running.StoppingToken.IsCancellationRequested);

        await running.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(running.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(running.StoppingToken.IsCancellationRequested);
        Assert.Equal(0, running.Failures);
        Assert.Empty(_log.At(LogLevel.Error));
    }

    [Fact]
    public async Task AFailingKitService_LeavesTheHostRunning_WhereAPlainBackgroundServiceStopsIt()
    {
        // .NET's default BackgroundServiceExceptionBehavior is StopHost, as in Jellyfin 10.11.5.
        var failing = await StartHostAsync(services => services.AddHostedService(_ => new Probe(_host.Object, _log, TimeProvider.System, (_, _) => throw new InvalidOperationException("boom"))));
        try
        {
            await WaitUntilAsync(() => _log.At(LogLevel.Error).Any());
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(failing.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await failing.StopAsync(TestContext.Current.CancellationToken);
            failing.Dispose();
        }

        using var plain = await StartHostAsync(services => services.AddHostedService<ThrowingPlainService>());
        var stopping = plain.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await WaitUntilAsync(() => stopping.IsCancellationRequested);
        await plain.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheBase_IsInternalAndAbstract_AndSealsExecuteAsync()
    {
        var type = typeof(KitBackgroundService);

        Assert.False(type.IsVisible);
        Assert.True(type.IsAbstract);
        Assert.True(typeof(BackgroundService).IsAssignableFrom(type));
        var execute = type.GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True(execute.IsFinal);
        Assert.Equal(TimeSpan.FromSeconds(30), KitBackgroundService.FirstRestartDelay);
        Assert.Equal(TimeSpan.FromMinutes(30), KitBackgroundService.MaxRestartDelay);
    }

    private static async Task<IHost> StartHostAsync(Action<IServiceCollection> configure)
    {
        var host = new HostBuilder().ConfigureServices(configure).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class Probe(IServerApplicationHost host, ILogger logger, TimeProvider clock, Func<int, CancellationToken, Task> run, bool restart = true)
        : KitBackgroundService(host, logger, clock)
    {
        private readonly ConcurrentQueue<int> _runs = new();

        public int Runs => _runs.Count;

        public CancellationToken StoppingToken => Stopping;

        protected override bool RestartAfterFailure => restart;

        protected override string ServiceName => "Probe";

        public async Task WaitForRunAsync(int count)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (Runs < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Run {count} didn't start.");
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        protected override Task RunAsync(CancellationToken stoppingToken)
        {
            _runs.Enqueue(0);
            return run(_runs.Count, stoppingToken);
        }
    }

    private sealed class ThrowingPlainService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
    }
}
