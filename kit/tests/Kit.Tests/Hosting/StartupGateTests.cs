using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// The startup gate: it opens when <c>CoreStartupHasCompleted</c> turns true, read once a second on the
/// clock it is given.
/// </summary>
public sealed class StartupGateTests
{
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly ManualTimerProvider _clock = new();
    private bool _started;

    public StartupGateTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(() => _started);
    }

    [Fact]
    public async Task AnOpenGate_DoesntWait()
    {
        _started = true;

        await StartupGate.WaitForCoreStartupAsync(_host.Object, _clock, TestContext.Current.CancellationToken);

        Assert.True(StartupGate.IsOpen(_host.Object));
        Assert.Equal(0, _clock.ArmedTimers);
    }

    [Fact]
    public async Task TheGate_OpensOnTheFirstPollAfterStartupCompleted()
    {
        var wait = StartupGate.WaitForCoreStartupAsync(_host.Object, _clock, TestContext.Current.CancellationToken);
        await _clock.WaitForArmedTimersAsync();

        _clock.Advance(TimeSpan.FromSeconds(1));
        await _clock.WaitForArmedTimersAsync();
        Assert.False(wait.IsCompleted);
        Assert.False(StartupGate.IsOpen(_host.Object));

        _started = true;
        Assert.False(wait.IsCompleted);
        _clock.Advance(StartupGate.PollInterval - TimeSpan.FromMilliseconds(1));
        Assert.False(wait.IsCompleted);
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(1), StartupGate.PollInterval);
    }

    [Fact]
    public async Task Shutdown_CancelsTheWait()
    {
        using var cts = new CancellationTokenSource();
        var wait = StartupGate.WaitForCoreStartupAsync(_host.Object, _clock, cts.Token);
        await _clock.WaitForArmedTimersAsync();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task ItChecksItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => StartupGate.IsOpen(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => StartupGate.WaitForCoreStartupAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => StartupGate.WaitForCoreStartupAsync(_host.Object, null!, CancellationToken.None));
    }
}
