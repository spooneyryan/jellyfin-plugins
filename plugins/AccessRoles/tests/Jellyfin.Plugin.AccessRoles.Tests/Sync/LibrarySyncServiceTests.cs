using Jellyfin.Plugin.Kit.TestSupport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Sync;

/// <summary>
/// What starts the library sync: the run at startup after the gate, a store change
/// debounced 2 s, a library structure call the filter saw (at once and twice more), a new library's <c>ItemAdded</c>, the
/// post-write check when due; and the library structure filter itself, which only watches the manifest's seven actions,
/// records a removal or a rename before the action runs and tells the sync after a 2xx result.
/// </summary>
public sealed class LibrarySyncServiceTests : IDisposable
{
    private readonly SyncHarness _h = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly FakeConfigurationProvider _configuration;
    private bool _gateOpen;

    public LibrarySyncServiceTests()
    {
        _configuration = new FakeConfigurationProvider(() => _h.Configuration);
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(() => _gateOpen);
    }

    [Fact]
    public async Task TheFirstRun_WaitsForTheStartupGate()
    {
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await _h.Clock.WaitForArmedTimersAsync();
        Assert.Null(LastTrigger());

        _gateOpen = true;
        _h.Clock.Advance(TimeSpan.FromSeconds(1));

        await WaitForAsync(() => LastTrigger() == "Startup");
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AStoreChange_RunsTheSyncTwoSecondsLater_AndTheUsersAccessIsWritten()
    {
        _gateOpen = true;
        var kid = _h.Server.Add("kid", allFolders: true);
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");

        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.Store.UpdateAsync(d => d with { Roles = [role], Assignments = [Assign(SyncHarness.Read(kid), [role])] }, TestContext.Current.CancellationToken);
        await WaitForTimerAsync(LibrarySyncService.Debounce);
        Assert.Empty(_h.Server.Writes);
        _h.Clock.Advance(LibrarySyncService.Debounce);

        await WaitForAsync(() => LastTrigger() == "StoreChanged");
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ALibraryStructureCall_RunsTheSyncAtOnce_AndAgainShortlyAfter()
    {
        _gateOpen = true;
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");
        var runs = Runs();

        await service.LibraryStructureChangedAsync(new LibraryStructureCall(LibraryStructureCallKind.Add, "Anime", null, null), TestContext.Current.CancellationToken);
        await WaitForAsync(() => Runs() == runs + 1);
        Assert.Equal("LibraryChanged", LastTrigger());
        await WaitForTimerAsync(LibrarySyncService.FollowUps[0]);
        _h.Clock.Advance(LibrarySyncService.FollowUps[0]);
        await WaitForAsync(() => Runs() == runs + 2);
        await WaitForTimerAsync(LibrarySyncService.FollowUps[1]);
        _h.Clock.Advance(LibrarySyncService.FollowUps[1] - LibrarySyncService.FollowUps[0]);
        await WaitForAsync(() => Runs() == runs + 3);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ANewLibrarysItemAdded_RunsTheSync_AndOtherItemsDont()
    {
        _gateOpen = true;
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");

        _libraryManager.Raise(m => m.ItemAdded += null, _libraryManager.Object, new ItemChangeEventArgs { Item = new Folder() });
        _libraryManager.Raise(m => m.ItemAdded += null, _libraryManager.Object, new ItemChangeEventArgs { Item = new CollectionFolder() });
        await WaitForTimerAsync(LibrarySyncService.Debounce);
        _h.Clock.Advance(LibrarySyncService.Debounce);

        await WaitForAsync(() => LastTrigger() == "LibraryChanged");
        Assert.Equal(2, Runs());
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APostWriteCheck_RunsWhenDue_AndHealsTheSignInRacesRevert()
    {
        _gateOpen = true;
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.Store.UpdateAsync(d => d with { Roles = [role], Assignments = [Assign(SyncHarness.Read(kid), [role])] }, TestContext.Current.CancellationToken, notify: false);
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => _h.Server.Writes.Count == 1);
        _h.Server.SignInRace(kid.Id);

        await WaitForTimerAsync(PolicyWriter.VerifyDelay);
        _h.Clock.Advance(PolicyWriter.VerifyDelay);

        await WaitForAsync(() => _h.Lines(Jellyfin.Plugin.AccessRoles.Audit.AuditActions.PolicySynced).Any(l => SyncHarness.Detail(l, "Reason") == SyncReasons.RevertedAfterWrite));
        Assert.Equal(2, _h.Server.Writes.Count);
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheFilter_WatchesTheManifestsSevenLibraryStructureActions()
    {
        using var service = Service();
        var filter = Filter(service);

        Assert.Equal(
            ["AddMediaPath", "AddVirtualFolder", "RemoveMediaPath", "RemoveVirtualFolder", "RenameVirtualFolder", "UpdateLibraryOptions", "UpdateMediaPath"],
            filter.WatchedActions.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(LibraryStructureCallKind.Rename, filter.WatchedActions["RenameVirtualFolder"]);
        Assert.Equal(LibraryStructureCallKind.Update, filter.WatchedActions["UpdateLibraryOptions"]);
    }

    [Fact]
    public async Task TheFilter_TellsTheSyncWhichLibraryWasRemoved_ByTheIdOfItsNameBeforeTheCall_SoTheBackstopDoesntWaitForARename()
    {
        using var service = Service();
        var filter = Filter(service);
        await _h.RunAsync();

        // A new library with the removed one's folders, in the add window: without the seen removal the backstop would
        // wait for it as a possible rename.
        await filter.OnActionExecutionAsync(Context("RemoveVirtualFolder", new() { ["name"] = "Kids" }), () =>
        {
            _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
            _h.Catalog.Libraries.Add(Library(7, "Kids 2") with { Locations = Kids.Locations, HasItem = false });
            return Executed(new NoContentResult());
        });
        var report = await _h.RunAsync(SyncTrigger.LibraryChanged);

        var removed = Assert.Single(report.Libraries);
        Assert.Equal((Jellyfin.Plugin.AccessRoles.Access.LifecycleEventKind.Removed, Kids.Id, true), (removed.Kind, removed.Id, removed.Exact));
    }

    [Fact]
    public async Task TheFilter_RecordsARename_WithTheOldLibrarysId()
    {
        using var service = Service();
        var filter = Filter(service);

        await filter.OnActionExecutionAsync(Context("RenameVirtualFolder", new() { ["name"] = "Kids", ["newName"] = "kids", ["refreshLibrary"] = true }), () => Executed(new NoContentResult()));

        var rename = Assert.Single(_h.Store.Current.PendingRenames);
        Assert.Equal((Kids.Id, "Kids", "kids"), (rename.OldId, rename.OldName, rename.NewName));
    }

    [Fact]
    public async Task TheFilter_IgnoresAFailedCall_AnotherController_AndActionsItDoesntWatch()
    {
        using var service = Service();
        var filter = Filter(service);
        var calls = 0;

        await filter.OnActionExecutionAsync(Context("RenameVirtualFolder", new() { ["name"] = "Kids", ["newName"] = "Taken" }), () => { calls++; return Executed(new ConflictObjectResult("taken")); });
        await filter.OnActionExecutionAsync(Context("RenameVirtualFolder", new() { ["name"] = "Kids", ["newName"] = "Thrown" }), () => { calls++; return Executed(null, new InvalidOperationException("core")); });
        await filter.OnActionExecutionAsync(Context("GetVirtualFolders", []), () => { calls++; return Executed(new OkResult()); });
        await filter.OnActionExecutionAsync(Context("RenameVirtualFolder", new() { ["name"] = "Kids", ["newName"] = "Other" }, typeof(SyncHarness)), () => { calls++; return Executed(new NoContentResult()); });

        Assert.Equal(4, calls);
        Assert.Empty(_h.Store.Current.PendingRenames);
    }

    [Fact]
    public async Task ASettingsSave_WakesTheLoop_SoAShorterIntervalAppliesAtOnce()
    {
        // The Settings tab says a change applies at once, but the loop read the interval
        // only after a run, so a save waited out the old interval.
        _gateOpen = true;
        _h.Configuration.ReconcileIntervalMinutes = 30;
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");
        var runs = Runs();

        _h.Configuration.ReconcileIntervalMinutes = 1;
        _configuration.RaiseChanged();
        await WaitForTimerAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(service.PendingRequests);
        _h.Clock.Advance(TimeSpan.FromMinutes(1));

        await WaitForAsync(() => Runs() == runs + 1);
        Assert.Equal("Interval", LastTrigger());
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("DriftPolicy")]
    [InlineData("LibrarySyncEnabled")]
    [InlineData("EnforcementEnabled")]
    public async Task ASettingsSave_ThatTurnsOnWhatLetsTheSyncWrite_RunsItTwoSecondsLater_AndOneThatTurnsItOffDoesnt(string setting)
    {
        _gateOpen = true;
        var kid = _h.Server.Add("kid", allFolders: true);
        Set(setting, on: false);
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.Store.UpdateAsync(d => d with { Roles = [role], Assignments = [Assign(SyncHarness.Read(kid), [role])] }, TestContext.Current.CancellationToken);
        await WaitForTimerAsync(LibrarySyncService.Debounce);
        _h.Clock.Advance(LibrarySyncService.Debounce);
        await WaitForAsync(() => LastTrigger() == "StoreChanged");
        if (setting == "DriftPolicy")
        {
            // Written under ReportOnly (a first write isn't drift), then changed natively: the drift ReportOnly keeps.
            Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
            _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);
        }

        Assert.True(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        var runs = Runs();

        Set(setting, on: true);
        _configuration.RaiseChanged();
        Assert.Equal([SyncTrigger.SettingsChanged], service.PendingRequests.Select(r => r.Trigger));
        await WaitForTimerAsync(LibrarySyncService.Debounce);
        _h.Clock.Advance(LibrarySyncService.Debounce);

        await WaitForAsync(() => Runs() == runs + 1);
        Assert.Equal("SettingsChanged", LastTrigger());
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        // Turned off again, or a save of something else: no run asked for.
        Set(setting, on: false);
        _configuration.RaiseChanged();
        _h.Configuration.AuditRetentionMonths = 6;
        _configuration.RaiseChanged();
        Assert.Empty(service.PendingRequests);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    // A run for everyone ends only the settings saves asked for before
    // it started; one saved while it ran, which it may not have read, keeps its run.
    [Fact]
    public async Task ASettingsSaveWhileARunForEveryoneRuns_KeepsItsOwnRun()
    {
        _gateOpen = true;
        var kid = _h.Server.Add("kid", allFolders: true);
        Set("DriftPolicy", on: false);
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.Store.UpdateAsync(d => d with { Roles = [role], Assignments = [Assign(SyncHarness.Read(kid), [role])] }, TestContext.Current.CancellationToken, notify: false);

        // The run's write reads the kid: a second later an admin saves DriftPolicy Reapply.
        var saved = false;
        _h.Server.BeforeGetUserById = (id, _) =>
        {
            if (id.Equals(kid.Id) && !saved)
            {
                saved = true;
                _h.Clock.Advance(TimeSpan.FromSeconds(1));
                Set("DriftPolicy", on: true);
                _configuration.RaiseChanged();
            }
        };
        await _h.RunAsync(SyncTrigger.Manual);

        Assert.True(saved);
        Assert.Equal([SyncTrigger.SettingsChanged], service.PendingRequests.Select(r => r.Trigger));
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    // The mirror's setting saved on the Settings tab applies at once, either way: on turns the flag
    // off two seconds later, off puts it back; a save of something else asks for nothing.
    [Fact]
    public async Task SavingTheMirrorSetting_RunsTheSyncTwoSecondsLater_EitherWay()
    {
        _gateOpen = true;
        var kid = _h.Server.Add("kid");
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");
        var role = Role(1, "Five", [AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.Home, AccessRolesLock.Playback, AccessRolesLock.Subtitles]);
        await _h.Store.UpdateAsync(d => d with { Roles = [role], Assignments = [Assign(SyncHarness.Read(kid), [role])] }, TestContext.Current.CancellationToken);
        await WaitForTimerAsync(LibrarySyncService.Debounce);
        _h.Clock.Advance(LibrarySyncService.Debounce);
        await WaitForAsync(() => LastTrigger() == "StoreChanged");
        Assert.True(_h.Server.Stored(kid.Id).EnableUserPreferenceAccess);

        foreach (var on in new[] { true, false })
        {
            var runs = Runs();
            _h.Configuration.MirrorPreferenceLockToPolicy = on;
            _configuration.RaiseChanged();
            Assert.Equal([SyncTrigger.SettingsChanged], service.PendingRequests.Select(r => r.Trigger));
            await WaitForTimerAsync(LibrarySyncService.Debounce);
            _h.Clock.Advance(LibrarySyncService.Debounce);
            await WaitForAsync(() => Runs() == runs + 1);
            Assert.Equal(!on, _h.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        }

        _h.Configuration.AuditRetentionMonths = 6;
        _configuration.RaiseChanged();
        Assert.Empty(service.PendingRequests);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    // An admin's end of Degraded mode asks for a run (the mirror puts back the flags of the users it
    // no longer locks; a store recovered empty resumes the sync), debounced as a store change.
    [Fact]
    public async Task AnAdminsEndOfDegradedMode_RunsTheSyncTwoSecondsLater()
    {
        _gateOpen = true;
        Directory.CreateDirectory(Path.GetDirectoryName(_h.Store.Degraded.MarkerPath)!);
        await File.WriteAllTextAsync(_h.Store.Degraded.MarkerPath, "{\"Because\":\"ReadOnlyDataUnreadable\",\"Users\":[\"" + Guid.NewGuid().ToString("N") + "\"]}", TestContext.Current.CancellationToken);
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");
        Assert.True(_h.Store.Degraded.CanResolve);
        Assert.Empty(service.PendingRequests);

        _h.Store.Degraded.Resolve();

        Assert.Equal([SyncTrigger.StoreChanged], service.PendingRequests.Select(r => r.Trigger));
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASettingsSavesRun_IsDoneByARunForEveryoneThatStartsAfterIt_NotByADryRunOrARunForOneUser()
    {
        // Turning the sync back on asks for a run 2 s later; an admin's Sync right after it read the saved settings, so it
        // is that run: one run, not two (the sync probe's sign-in race then is healed by the post-write check, as before).
        _gateOpen = true;
        var kid = _h.Server.Add("kid", allFolders: true);
        Set("LibrarySyncEnabled", on: false);
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");

        Set("LibrarySyncEnabled", on: true);
        _configuration.RaiseChanged();
        Assert.Equal([SyncTrigger.SettingsChanged], service.PendingRequests.Select(r => r.Trigger));

        // A dry run and a run for one user aren't the run the save asked for.
        await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true });
        await _h.RunAsync(SyncTrigger.Manual, r => r with { UserId = kid.Id });
        Assert.Equal([SyncTrigger.SettingsChanged], service.PendingRequests.Select(r => r.Trigger));

        // A run for everyone is.
        await _h.RunAsync(SyncTrigger.Manual);
        Assert.Empty(service.PendingRequests);

        // A save after it asks again.
        Set("LibrarySyncEnabled", on: false);
        _configuration.RaiseChanged();
        Set("LibrarySyncEnabled", on: true);
        _configuration.RaiseChanged();
        Assert.Equal([SyncTrigger.SettingsChanged], service.PendingRequests.Select(r => r.Trigger));
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheConfigurationProvider_RaisesChanged_WhenThePluginsConfigurationIsUpdated_AsBothSettingsEndpointsDo()
    {
        // The Settings tab saves through IPluginConfigurationProvider.Save, Jellyfin's POST /Plugins/{id}/Configuration
        // through the plugin instance's UpdateConfiguration: both raise its ConfigurationChanged.
        var scratch = Directory.CreateTempSubdirectory("accessroles-config-");
        try
        {
            var plugin = (Plugin)PluginIdentityChecks.CreatePlugin(typeof(Plugin), scratch.FullName);
            var provider = new PluginConfigurationProvider(() => plugin);
            var changed = 0;
            provider.Changed += (_, _) => changed++;

            provider.Save(new PluginConfiguration { ReconcileIntervalMinutes = 7 });
            plugin.UpdateConfiguration(new PluginConfiguration { ReconcileIntervalMinutes = 9 });

            Assert.Equal((2, 9), (changed, provider.GetConfiguration().ReconcileIntervalMinutes));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheRegistrator_AddsTheFilter_AtTheGlobalFiltersOrder()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        using var provider = services.BuildServiceProvider();

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters.OfType<ServiceFilterAttribute>().ToList();

        // Since 0.9.5 one global filter, which hands the library structure actions to this filter and every other
        // action to the user policy hooks (0.9.4); the two are no filters of their own any more.
        Assert.Equal(
            [(typeof(Jellyfin.Plugin.AccessRoles.Enforcement.AccessRulesActionFilter), -10000)],
            filters.Select(f => (f.ServiceType, f.Order)));
    }

    public void Dispose() => _h.Dispose();

    private static Task<ActionExecutedContext> Executed(IActionResult? result, Exception? exception = null)
        => Task.FromResult(new ActionExecutedContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [], new object()) { Result = result, Exception = exception });

    private static ActionExecutingContext Context(string action, Dictionary<string, object?> arguments, Type? controller = null)
    {
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = (controller ?? typeof(Jellyfin.Api.Controllers.LibraryStructureController)).GetTypeInfo(),
            ActionName = action
        };
        return new ActionExecutingContext(new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor), [], arguments, new object());
    }

    private LibrarySyncService Service()
    {
        return new LibrarySyncService(_host.Object, _h.Sync, _h.Store, _h.Writer, _libraryManager.Object, _configuration, _h.Status, NullLogger.Instance, _h.Clock);
    }

    private void Set(string setting, bool on)
    {
        switch (setting)
        {
            case "DriftPolicy":
                _h.Configuration.DriftPolicy = on ? LibrarySync.ReapplyPolicy : LibrarySync.ReportOnlyPolicy;
                break;
            case "LibrarySyncEnabled":
                _h.Configuration.LibrarySyncEnabled = on;
                break;
            default:
                _h.Configuration.EnforcementEnabled = on;
                break;
        }
    }

    private LibraryStructureFilter Filter(LibrarySyncService service) => new(ActionManifest.ForBuild(), service, _h.Catalog, NullLogger.Instance);

    private string? LastTrigger() => (_h.Status.Describe(null, "Reapply", 5, 0, 0, 0)["LastRun"] as IDictionary<string, object?>)?["Trigger"] as string;

    private int Runs() => (int)((IDictionary<string, object?>)_h.Status.Describe(null, "Reapply", 5, 0, 0, 0)["Totals"]!)["Runs"]!;

    private async Task WaitForTimerAsync(TimeSpan due)
    {
        // The loop waits on a timer of the manual clock that falls due within this time (the next run it shows, or a
        // post-write check), before the test moves the clock on.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var until = _h.Now + due;
        while (!(_h.Clock.ArmedTimers > 0
            && ((_h.Status.Describe(null, "Reapply", 5, 0, 0, 0)["NextRunUtc"] is DateTime next && next <= until) || _h.Writer.NextVerificationDue <= until)))
        {
            Assert.True(DateTime.UtcNow < deadline, "The loop waits for nothing due within " + due);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
