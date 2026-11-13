using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The worker on a fake clock: runs asked for by library changes wait for the cooldown and join; a pass takes a waiting run
/// with it; passes after a scan wait for the scan to end, the daily one only so long, and runs asked for by hand don't
/// wait; <c>Queued</c> and <c>Running</c> show while that happens; a failing definition doesn't stop the pass; and the
/// definitions of a pass share the items they read.
/// </summary>
public sealed class AutoCollectionWorkerTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly ManualTimerProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly AutoCollectionHost _host;

    public AutoCollectionWorkerTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid };
        users.SetupUsers(all);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.FirstOrDefault(u => u.Id.Equals(id)));
        _host = new AutoCollectionHost(_world, _harness, _folder.Combine("data"), _persistence, users.Object, time: _clock);
    }

    private User Admin => _world.Admin;

    public void Dispose()
    {
        _host.Dispose();
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task AnIdleWorker_Sleeps_UntilWoken()
    {
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.InRange(_host.Worker.Waits, 1, 3);
    }

    [Fact]
    public async Task ARunForLibraryChanges_WaitsForTheCooldown_AndASecondRequestJoinsIt()
    {
        Movie("A");
        var id = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", Query()))).Id;
        Movie("B");

        var first = Events(id);
        var second = Events(id);
        Assert.Same(first, second);
        Assert.Equal(SmartSearchAutoCollectionStatus.Queued, _host.Worker.ActivityOf(id)?.Status);
        Assert.Equal("Queued", (await _host.Service.GetAsync(Admin, true, id, TestContext.Current.CancellationToken)).State.Status);

        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(TimeSpan.FromSeconds(119));
        await NotYet(first);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var state = await Within(first);

        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, state.Status.Value);
        Assert.Equal(SmartSearchAutoCollectionRunReason.Events, state.LastReason!.Value);
        Assert.Equal(1, state.LastAdded);
        Assert.Null(_host.Worker.ActivityOf(id)?.Status);
        Assert.Equal(2, _persistence.Saves);
    }

    [Fact]
    public async Task ARunByHand_JoinsAWaitingRunForLibraryChanges_AndStartsAtOnce()
    {
        Movie("A");
        var id = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", Query()))).Id;

        var waiting = Events(id);
        var manual = _host.Worker.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Manual));
        Assert.Same(waiting, manual);

        var state = await Within(manual);
        Assert.Equal(SmartSearchAutoCollectionRunReason.Manual, state.LastReason!.Value);
    }

    [Fact]
    public async Task APass_TakesAWaitingRunWithIt()
    {
        Movie("A");
        var a = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("First", Query()))).Id;
        var b = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Second", Query()))).Id;
        Movie("B");
        var saves = _persistence.Saves;

        var waiting = Events(a);
        var pass = _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Manual);
        await Within(pass);
        var state = await Within(waiting);

        Assert.Equal(SmartSearchAutoCollectionRunReason.Manual, state.LastReason!.Value);
        Assert.Equal(SmartSearchAutoCollectionRunReason.Manual, _host.State.Find(b)!.LastReason!.Value);
        Assert.Equal(saves + 2, _persistence.Saves);
        Assert.Equal(1, _host.Worker.PassesRun);
    }

    [Fact]
    public async Task APassAfterAScan_WaitsForTheScanToEnd_WhileARunByHandDoesnt()
    {
        Movie("A");
        var id = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", Query()))).Id;
        _host.ScanRunning = true;

        var pass = _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.PostScan);
        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await NotYet(pass);
        Assert.Equal("Queued", (await _host.Service.GetAsync(Admin, true, id, TestContext.Current.CancellationToken)).State.Status);

        // A refresh by hand runs now, and knows a scan was running; the pass still waits.
        var manual = await Within(_host.Worker.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Manual)));
        Assert.Equal(SmartSearchAutoCollectionRunReason.Manual, manual.LastReason!.Value);
        Assert.False(pass.IsCompleted);

        _host.ScanRunning = false;
        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(AutoCollectionWorker.ScanPollInterval);
        await Within(pass);
        Assert.Equal(SmartSearchAutoCollectionRunReason.PostScan, _host.State.Find(id)!.LastReason!.Value);
        Assert.Equal("Ok", (await _host.Service.GetAsync(Admin, true, id, TestContext.Current.CancellationToken)).State.Status);
    }

    [Fact]
    public async Task TheDailyPass_WaitsForAScanOnlySoLong()
    {
        Movie("A");
        var id = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", Query()))).Id;
        _host.Settings = new AutoCollectionSettings { DailyPassScanWait = TimeSpan.FromSeconds(5) };
        _host.ScanRunning = true;
        var progress = new List<double>();

        var pass = _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Daily, new Progress(progress));
        for (var i = 0; i < 3 && !pass.IsCompleted; i++)
        {
            await _clock.WaitForArmedTimersAsync();
            _clock.Advance(AutoCollectionWorker.ScanPollInterval);
        }

        await Within(pass);
        Assert.Equal(SmartSearchAutoCollectionRunReason.Daily, _host.State.Find(id)!.LastReason!.Value);
        Assert.Equal([100.0], progress);
    }

    [Fact]
    public async Task AFailingDefinition_ShowsError_AndThePassGoesOn()
    {
        Movie("A");
        var a = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Broken", Query()))).Id;
        var b = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Fine", Query()))).Id;
        Movie("B");
        _persistence.SaveFails = box => box.Name == "Broken";

        await Within(_host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Manual));

        var broken = _host.State.Find(a)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.Error, broken.Status.Value);
        Assert.Equal(AutoCollectionReasons.RunFailed, broken.Reason);
        Assert.Equal("the disk is full", broken.LastErrorDetail);
        Assert.Equal(AutoCollectionReasons.SafeText(AutoCollectionReasons.RunFailed), broken.LastError);
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, _host.State.Find(b)!.Status.Value);
        Assert.Equal(2, _host.State.Find(b)!.LastMemberCount);

        // The next run tries again.
        _persistence.SaveFails = _ => false;
        var again = await Within(_host.Worker.RequestAsync(new AutoCollectionRequest(a, SmartSearchAutoCollectionRunReason.Manual)));
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, again.Status.Value);
    }

    [Fact]
    public async Task ThePassesDefinitions_ShareTheItemsTheyRead_UpToTheLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            Movie("M" + i);
        }

        await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("First", Query()));
        await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Second", Query()));

        await Within(_host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Manual));
        var shared = _host.Worker.LastPassCache!;
        Assert.Equal(5, shared.Loads);
        Assert.Equal(5, shared.Hits);

        _host.Settings = new AutoCollectionSettings { PassCacheMaxItems = 0 };
        await Within(_host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Manual));
        Assert.Equal(0, _host.Worker.LastPassCache!.Hits);
        Assert.Equal(0, _host.Worker.LastPassCache!.Count);
    }

    [Fact]
    public async Task TheRunsOfOneBatchOfChanges_ShareTheItemsTheyRead_AndTheNextBatchReadsAfresh()
    {
        for (var i = 0; i < 5; i++)
        {
            Movie("M" + i);
        }

        var a = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("First", Query()))).Id;
        var b = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Second", Query()))).Id;

        var first = Events(a);
        var second = Events(b);
        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Within(first);
        await Within(second);
        var shared = _host.Worker.LastPassCache!;
        Assert.Equal(5, shared.Loads);
        Assert.Equal(5, shared.Hits);

        var next = Events(a);
        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Within(next);
        Assert.NotSame(shared, _host.Worker.LastPassCache);
        Assert.Equal(5, _host.Worker.LastPassCache!.Loads);
    }

    [Fact]
    public async Task APass_RunsNothing_WhenAutoCollectionsAreOff()
    {
        Movie("A");
        var id = (await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", Query()))).Id;
        var before = _host.State.Find(id)!.LastRunUtc;
        _host.Settings = new AutoCollectionSettings { Enabled = false };
        _clock.Advance(TimeSpan.FromMinutes(1));

        await Within(_host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Daily));
        Assert.Equal(before, _host.State.Find(id)!.LastRunUtc);
        Assert.Null(_host.Worker.ActivityOf(id)?.Status);
    }

    private static string Query() => QueryPlannerTests.Q(Keep, "\"Movie\"");

    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(Patience)));
        return await task;
    }

    private static async Task Within(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(Patience)));
        await task;
    }

    private static async Task NotYet(Task task)
    {
        await Task.Delay(100);
        Assert.False(task.IsCompleted);
    }

    private Task<AutoCollectionState> Events(Guid id)
        => _host.Worker.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Events));

    private void Movie(string name)
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });

    private sealed class Progress(List<double> values) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }
}
