using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The watch-state trigger: only the evaluation user's changes run their auto-collections, after the quiet period, not
/// the progress saved while playing, not during a scan; the handler is registered only while some auto-collection needs
/// it and the setting is on.
/// </summary>
public sealed class UserDataTriggerTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(100);

    private readonly TempDirectory _folder = new();
    private readonly AutoCollectionStore _store;
    private readonly RecordingQueue _queue = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Guid _creator = Guid.NewGuid();
    private readonly Guid _evaluator = Guid.NewGuid();
    private AutoCollectionSettings _settings = new() { EventDebounce = Quiet, EventMaxWait = TimeSpan.FromSeconds(5) };
    private bool _scanning;

    public UserDataTriggerTests()
    {
        _store = AutoCollectionTestData.Store(_folder.Path);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _store.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task OnlyTheEvaluationUsersChange_RunsTheirCollection_AfterTheQuietPeriod()
    {
        var watched = await AddAsync(_evaluator);
        await AddAsync(evaluator: null);
        using var trigger = await StartAsync();
        Assert.True(trigger.IsListening);

        trigger.OnSaved(Guid.NewGuid(), UserDataSaveReason.TogglePlayed);
        trigger.OnSaved(_evaluator, UserDataSaveReason.PlaybackProgress);
        trigger.OnSaved(_evaluator, UserDataSaveReason.PlaybackStart);
        await Task.Delay(Quiet * 4, Ct);
        Assert.Empty(_queue.Requests);

        // A burst of changes is one run, after the last one's quiet period.
        trigger.OnSaved(_evaluator, UserDataSaveReason.TogglePlayed);
        trigger.OnSaved(_evaluator, UserDataSaveReason.UpdateUserRating);
        trigger.OnSaved(_evaluator, UserDataSaveReason.PlaybackFinished);
        await WaitForAsync(() => !_queue.Requests.IsEmpty);
        await Task.Delay(Quiet * 3, Ct);
        var request = Assert.Single(_queue.Requests);
        Assert.Equal((watched, SmartSearchAutoCollectionRunReason.WatchState), (request.DefinitionId, request.Reason));
    }

    [Fact]
    public async Task DuringAScan_NothingIsAskedFor()
    {
        await AddAsync(_evaluator);
        using var trigger = await StartAsync();
        _scanning = true;
        trigger.OnSaved(_evaluator, UserDataSaveReason.TogglePlayed);
        await Task.Delay(Quiet * 5, Ct);
        Assert.Empty(_queue.Requests);
    }

    [Fact]
    public async Task TheHandler_IsRegisteredOnlyWhileNeeded()
    {
        using var trigger = await StartAsync();
        Assert.False(trigger.IsListening);

        var id = await AddAsync(_evaluator);
        trigger.Recheck();
        Assert.True(trigger.IsListening);
        _userData.VerifyAdd(u => u.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once());

        // Off in the settings: no handler; a disabled definition doesn't count either.
        _settings = _settings with { UseUserDataTriggers = false };
        trigger.Recheck();
        Assert.False(trigger.IsListening);
        _settings = _settings with { UseUserDataTriggers = true };
        await _store.UpdateDefinitionAsync(id, d => d with { Enabled = false }, Ct);
        trigger.Recheck();
        Assert.False(trigger.IsListening);
        _userData.VerifyRemove(u => u.UserDataSaved -= It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.AtLeastOnce());
    }

    private async Task<Guid> AddAsync(Guid? evaluator)
    {
        var definition = AutoCollectionTestData.Definition(_creator, Guid.NewGuid(), name: "Unwatched " + Guid.NewGuid().ToString("N")[..6], collectionId: Guid.NewGuid()) with { EvaluationUserId = evaluator };
        await _store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [.. d.Definitions, definition]), Ct);
        return definition.Id;
    }

    private async Task<UserDataTrigger> StartAsync()
    {
        var server = new Mock<IServerApplicationHost>();
        server.Setup(s => s.CoreStartupHasCompleted).Returns(true);
        var trigger = new UserDataTrigger(server.Object, _userData.Object, _store, _queue, () => _settings, () => _scanning, TimeProvider.System, NullLogger.Instance);
        await trigger.StartAsync(Ct);
        await WaitForAsync(() => trigger.IsListening || _store.Current.Definitions.All(d => d.EvaluationUserId is null), TimeSpan.FromSeconds(5));
        return trigger;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "Timed out waiting.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class RecordingQueue : IAutoCollectionQueue
    {
        public ConcurrentQueue<AutoCollectionRequest> Requests { get; } = new();

        public Task<AutoCollectionState> RequestAsync(AutoCollectionRequest request)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new AutoCollectionState());
        }

        public Task RequestPassAsync(SmartSearchAutoCollectionRunReason reason, IProgress<double>? progress = null) => Task.CompletedTask;

        public AutoCollectionActivity? ActivityOf(Guid definitionId) => null;
    }
}
