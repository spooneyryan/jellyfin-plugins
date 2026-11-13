using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The triggers on a fake clock: the full pass inside a scan only queues a pass that starts after the scan; a batch of
/// library changes asks for the definitions over its kinds; the daily task runs a pass and reports it; the startup pass
/// comes after its delay; a changed or removed saved query asks for the definitions that follow it.
/// </summary>
public sealed class AutoCollectionTriggerTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly ManualTimerProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly AutoCollectionHost _host;

    public AutoCollectionTriggerTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid };
        users.SetupUsers(all);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.FirstOrDefault(u => u.Id.Equals(id)));
        _host = new AutoCollectionHost(_world, _harness, _folder.Combine("data"), _persistence, users.Object, time: _clock);
        Movie("A");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _host.Dispose();
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task TheFullPassInsideAScan_OnlyQueuesAPass_ThatStartsWhenTheScanEnds()
    {
        var id = await CreateAsync("Movies", "\"Movie\"");
        Movie("B");
        _host.ScanRunning = true;
        var consumer = Consumer();

        var handled = consumer.HandleAsync(Batch(fullPass: true), Ct);
        Assert.True(handled.IsCompletedSuccessfully);
        Assert.Equal([SmartSearchAutoCollectionRunReason.PostScan], _host.Queue.Passes);
        Assert.Equal(SmartSearchAutoCollectionStatus.Queued, _host.Worker.ActivityOf(id)?.Status);

        await _clock.WaitForArmedTimersAsync();
        _host.ScanRunning = false;
        _clock.Advance(AutoCollectionWorker.ScanPollInterval);
        await Within(_host.Queue.WhenIdleAsync());
        var state = _host.State.Find(id)!;
        Assert.Equal(SmartSearchAutoCollectionRunReason.PostScan, state.LastReason!.Value);
        Assert.Equal(2, state.LastMemberCount);
    }

    [Fact]
    public async Task ABatchOfChanges_AsksForTheDefinitionsOverItsKinds()
    {
        var movies = await CreateAsync("Movies", "\"Movie\"");
        var episodes = await CreateAsync("Episodes", "\"Episode\"");
        var consumer = Consumer();

        await consumer.HandleAsync(Batch(added: [Change(BaseItemKind.Movie)]), Ct);
        Assert.Equal([movies], Asked());

        await consumer.HandleAsync(Batch(updated: [Change(BaseItemKind.Series)]), Ct);
        Assert.Equal([episodes], Asked());

        await consumer.HandleAsync(Batch(removedFolders: [Change(BaseItemKind.Folder)]), Ct);
        Assert.Equal(new[] { movies, episodes }.Order(), Asked().Order());

        _host.Settings = new AutoCollectionSettings { Enabled = false };
        await consumer.HandleAsync(Batch(added: [Change(BaseItemKind.Movie)]), Ct);
        await consumer.HandleAsync(Batch(fullPass: true), Ct);
        Assert.Empty(Asked());
        Assert.Empty(_host.Queue.Passes);
    }

    [Fact]
    public async Task TheDailyTask_RunsAPass_ScheduledOrByHand_AndSkipsWhenItsOff()
    {
        var id = await CreateAsync("Movies", "\"Movie\"");
        var trigger = new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = DateTime.Now.AddMinutes(-1).TimeOfDay.Ticks };
        var scheduled = new Mock<IScheduledTask>();
        scheduled.SetupGet(t => t.Key).Returns("SmartSearchRefreshAutoCollections");
        var worker = new Mock<IScheduledTaskWorker>();
        worker.SetupGet(w => w.ScheduledTask).Returns(scheduled.Object);
        worker.SetupGet(w => w.Triggers).Returns(() => [trigger]);
        var taskManager = new Mock<ITaskManager>();
        taskManager.SetupGet(m => m.ScheduledTasks).Returns([worker.Object]);
        var services = new ServiceCollection()
            .AddSingleton(new KitAbiStatus("Smart Search", "10.11", "10.11.5", true, "Active."))
            .AddSingleton<IAutoCollectionQueue>(_host.Queue)
            .AddSingleton(_host.Context)
            .AddSingleton(taskManager.Object)
            .BuildServiceProvider();
        var task = new RefreshAutoCollectionsTask(services);

        // At its trigger's time: the scheduled pass.
        await Within(task.ExecuteAsync(new Progress<double>(), Ct));
        Assert.Equal(SmartSearchAutoCollectionRunReason.Daily, _host.State.Find(id)!.LastReason!.Value);
        Assert.Equal("Smart Search: Refresh auto-collections", task.Name);
        Assert.Equal(new TimeSpan(4, 0, 0).Ticks, Assert.Single(task.GetDefaultTriggers()).TimeOfDayTicks);

        // At any other time: the dashboard's Run, a pass by hand.
        trigger = new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = DateTime.Now.AddMinutes(-30).TimeOfDay.Ticks };
        await Within(task.ExecuteAsync(new Progress<double>(), Ct));
        Assert.Equal(SmartSearchAutoCollectionRunReason.Manual, _host.State.Find(id)!.LastReason!.Value);

        _host.Settings = new AutoCollectionSettings { Enabled = false };
        await Within(task.ExecuteAsync(new Progress<double>(), Ct));
        Assert.Equal([SmartSearchAutoCollectionRunReason.Daily, SmartSearchAutoCollectionRunReason.Manual], _host.Queue.Passes);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(-90, false)]
    public void ARunWithinTenMinutesAfterADailyTriggersTime_IsTheScheduledPass(int minutesAfter, bool scheduled)
    {
        var now = new DateTime(2026, 11, 10, 4, 0, 0, DateTimeKind.Local).AddMinutes(minutesAfter);
        var daily = new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = new TimeSpan(4, 0, 0).Ticks };
        Assert.Equal(scheduled, RefreshAutoCollectionsTask.IsScheduled([daily], now));
        var late = new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = new TimeSpan(23, 55, 0).Ticks };
        Assert.True(RefreshAutoCollectionsTask.IsScheduled([late], new DateTime(2026, 11, 11, 0, 2, 0, DateTimeKind.Local)));
        Assert.False(RefreshAutoCollectionsTask.IsScheduled([new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }], now));
    }

    [Fact]
    public async Task TheStartupPass_ComesAfterItsDelay()
    {
        var id = await CreateAsync("Movies", "\"Movie\"");
        _host.Settings = new AutoCollectionSettings { StartupPassDelay = TimeSpan.FromMinutes(5) };
        using var triggers = Triggers();
        await triggers.StartAsync(Ct);

        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(_host.Queue.Passes);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => _host.Queue.Passes.Count == 1);
        await Within(_host.Queue.WhenIdleAsync());

        Assert.Equal([SmartSearchAutoCollectionRunReason.Startup], _host.Queue.Passes);
        Assert.Equal(SmartSearchAutoCollectionRunReason.Startup, _host.State.Find(id)!.LastReason!.Value);
        await triggers.StopAsync(Ct);
    }

    [Fact]
    public async Task AChangedOrRemovedSavedQuery_AsksForTheDefinitionsThatFollowIt()
    {
        var query = await AddQueryAsync();
        var other = await AddQueryAsync();
        var following = (await _host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Following", savedQueryId: query))).Id;
        await _host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Other", savedQueryId: other));
        await _host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Copy", QueryPlannerTests.Q(Keep, "\"Movie\"")));
        using var triggers = Triggers();
        await triggers.StartAsync(Ct);
        await WaitUntil(() => triggers.IsListening);
        Asked();

        // An edit (any write that replaces the record, a forced or bulk one too).
        await _host.Queries.UpdateAsync(d => SavedQueryStore.WithQueries(d, d.Queries.Select(q => q.Id.Equals(query) ? q with { Name = "Renamed" } : q)), Ct);
        Assert.Equal([following], Asked());

        // Removed.
        await _host.Queries.UpdateAsync(d => SavedQueryStore.WithQueries(d, d.Queries.Where(q => !q.Id.Equals(query))), Ct);
        Assert.Equal([following], Asked());

        // A write that replaces no query asks for nothing.
        await _host.Queries.UpdateAsync(d => d, Ct);
        Assert.Empty(Asked());
        await triggers.StopAsync(Ct);
        await Within(_host.Queue.WhenIdleAsync());
    }

    private static LibraryChangeBatch Batch(
        bool fullPass = false,
        IReadOnlyList<ItemChange>? added = null,
        IReadOnlyList<ItemChange>? updated = null,
        IReadOnlyList<ItemChange>? removedFolders = null)
        => new(added ?? [], updated ?? [], [], removedFolders ?? [], [], fullPass, duringPostScan: fullPass);

    private static ItemChange Change(BaseItemKind kind)
        => new() { Kind = LibraryChangeKind.Added, ItemId = Guid.NewGuid(), ItemKind = kind, Utc = DateTimeOffset.UtcNow };

    private static async Task Within(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(Patience, Ct)));
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.True(condition());
    }

    // The definitions asked for since the last look, in order.
    private List<Guid> Asked()
    {
        lock (_host.Queue.Requests)
        {
            var asked = _host.Queue.Requests.Where(r => r.Kind == AutoCollectionRequestKind.Run).Select(r => r.DefinitionId).ToList();
            _host.Queue.Requests.Clear();
            return asked;
        }
    }

    private AutoCollectionsChangeConsumer Consumer()
        => new(_host.Store, _host.Checks, _host.Context, _host.Queue, NullLogger.Instance);

    private AutoCollectionTriggers Triggers()
        => new(_host.Server, _host.Queue, _host.Store, _host.Queries, () => _host.Settings, _clock, NullLogger.Instance);

    private async Task<Guid> CreateAsync(string name, string kinds)
    {
        var id = (await _host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def(name, QueryPlannerTests.Q(Keep, kinds)))).Id;
        Asked();
        return id;
    }

    private async Task<Guid> AddQueryAsync()
    {
        var id = Guid.NewGuid();
        using var query = JsonDocument.Parse(QueryPlannerTests.Q(Keep, "\"Movie\""));
        var record = new SavedQueryRecord { Id = id, OwnerUserId = _world.Admin.Id, Name = "Query " + id.ToString("N")[..6], Shared = false, Query = query.RootElement.Clone(), CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        await _host.Queries.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, record]), Ct);
        return id;
    }

    private void Movie(string name)
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
}
