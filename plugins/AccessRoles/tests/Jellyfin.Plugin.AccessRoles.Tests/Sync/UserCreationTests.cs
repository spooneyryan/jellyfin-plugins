using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Sync;

/// <summary>
/// A user created under default roles with the library sync's service running: no
/// run of any trigger (a store change, a manual sync, another user's sign-in follow-up, the backstop) and no post-write
/// check writes the user while the request creating them runs, so the core's own save of the password after the event
/// succeeds however long the request takes; the default roles' save raises no store change; once the request is done the
/// runs wait 3 s more while the user's own sign-in is corrected by the hook, then write them. The 10.11.5 user manager's
/// double models the core's save failing after another save moved the row on (its negative control).
/// </summary>
public sealed class UserCreationTests : IDisposable
{
    private readonly SyncHarness _h = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly LibrarySyncService _service;
    private readonly AccessRolesAdminService _admin;

    public UserCreationTests()
    {
        _host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse("10.11.5"));
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var configuration = new FakeConfigurationProvider(() => _h.Configuration);
        _service = new LibrarySyncService(_host.Object, _h.Sync, _h.Store, _h.Writer, null, configuration, _h.Status, NullLogger.Instance, _h.Clock);
        _admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);
    }

    [Fact]
    public async Task WhileTheCreatingRequestRuns_NoRunWritesTheNewUser_AndTheCoresPasswordSaveSucceeds()
    {
        var kidRole = await StartedWithKidAsDefaultRoleAsync();
        var bystander = _h.Server.Add("bystander", allFolders: true);
        var user = _h.Server.Add("newkid", allFolders: true);
        var created = _h.Server.RowVersion(user.Id);
        var response = new CompletingResponse();

        // The event inside POST /Users/New (the database busy: the request goes on for a while).
        await Consumer(Request(response, "create-1")).OnEvent(new UserCreatedEventArgs(user));
        Assert.NotNull(EffectiveRulesResolver.FindAssignment(_h.Store.Current, user.Id));
        Assert.DoesNotContain(_service.PendingRequests, r => r.Trigger == SyncTrigger.StoreChanged);

        // Every trigger the review named, while the request runs: a store change (another admin's save, its run 2 s later),
        // another user's sign-in follow-up, a manual sync, and the backstop's interval.
        var runs = Runs();
        await _h.Store.UpdateAsync(d => d with { Assignments = [.. d.Assignments, Assign(SyncHarness.Read(bystander), [kidRole])] }, TestContext.Current.CancellationToken);
        await WaitForTimerAsync(LibrarySyncService.Debounce);
        _h.Clock.Advance(LibrarySyncService.Debounce);
        await WaitForAsync(() => Runs() == runs + 1);
        Assert.Equal("StoreChanged", LastTrigger());
        Assert.Equal([Kids.Id], _h.Server.Cached(bystander.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        _service.Request(SyncTrigger.SignIn, TimeSpan.Zero);
        await WaitForAsync(() => Runs() == runs + 2);
        var manual = await _h.RunAsync(SyncTrigger.Manual);
        Assert.Equal([user.Id], manual.BeingCreated);
        Assert.DoesNotContain(manual.Users, u => u.User.Id.Equals(user.Id));
        _h.Clock.Advance(TimeSpan.FromSeconds(40));
        _h.Configuration.ReconcileIntervalMinutes = 1;
        _service.Request(SyncTrigger.Interval, TimeSpan.Zero);
        await WaitForAsync(() => Runs() == runs + 4);

        Assert.DoesNotContain(_h.Server.Writes, w => w.UserId.Equals(user.Id));
        _h.Server.SaveCreatedUsersPassword(user.Id, created);
        Assert.True(_h.Server.Cached(user.Id).HasPermission(PermissionKind.EnableAllFolders));

        // The request is done: the runs still wait 3 s (a manual sync meanwhile leaves the user alone), then write them.
        await response.CompleteAsync();
        Assert.Equal([user.Id], (await _h.RunAsync(SyncTrigger.Manual)).BeingCreated);
        runs = Runs();
        await WaitForTimerAsync(UserCreatedConsumer.FirstRunDelay);
        _h.Clock.Advance(UserCreatedConsumer.FirstRunDelay);
        await WaitForAsync(() => Runs() == runs + 1);
        Assert.Equal("UserCreated", LastTrigger());
        Assert.Equal([Kids.Id], _h.Server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.False(_h.Server.Cached(user.Id).HasPermission(PermissionKind.EnableAllFolders));
        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithoutTheMark_AStoreChangesRun_WritesTheNewUserInsideTheRequest_AndTheCoresPasswordSaveFails()
    {
        // The negative control, 0.9.4.0's way: the default roles saved with a store change and no mark. The run 2 s later
        // writes the user while the request still holds the entity it created, and the core's save of the password fails.
        var kidRole = await StartedWithKidAsDefaultRoleAsync();
        var user = _h.Server.Add("newkid", allFolders: true);
        var created = _h.Server.RowVersion(user.Id);
        var runs = Runs();

        await _h.Store.UpdateAsync(d => d with { Assignments = [.. d.Assignments, Assign(SyncHarness.Read(user), [kidRole])] }, TestContext.Current.CancellationToken);
        await WaitForTimerAsync(LibrarySyncService.Debounce);
        _h.Clock.Advance(LibrarySyncService.Debounce);
        await WaitForAsync(() => Runs() == runs + 1);

        Assert.Contains(_h.Server.Writes, w => w.UserId.Equals(user.Id));
        Assert.Throws<InvalidOperationException>(() => _h.Server.SaveCreatedUsersPassword(user.Id, created));
        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASignInRightAfterTheCreation_IsCorrectedByTheHook_WhileTheRunsStillWait()
    {
        // The first run at the response's completion met the new user's sign-in (the 10.11.5
        // race). Now the runs wait 3 s, and the sign-in's own correction writes the user after the core's save.
        _ = await StartedWithKidAsDefaultRoleAsync();
        var user = _h.Server.Add("newkid", allFolders: true);
        var response = new CompletingResponse();
        await Consumer(Request(response, "create-1")).OnEvent(new UserCreatedEventArgs(user));
        await response.CompleteAsync();

        var runs = Runs();
        _service.Request(SyncTrigger.SignIn, TimeSpan.Zero);
        await WaitForAsync(() => Runs() == runs + 1);
        Assert.DoesNotContain(_h.Server.Writes, w => w.UserId.Equals(user.Id));
        var result = SignInResult(user);
        var executed = await SignInAsync(result, "signin-1");

        Assert.IsType<ObjectResult>(executed.Result);
        Assert.False(result.User.Policy.EnableAllFolders);
        Assert.Equal([Kids.Id], result.User.Policy.EnabledFolders);
        Assert.Single(_h.Server.Writes, w => w.UserId.Equals(user.Id));
        var line = _h.Lines(AuditActions.PolicySynced).Last();
        Assert.Equal((user.Id, "SignIn"), (line.TargetUserId!.Value, SyncHarness.Detail(line, "Trigger")));

        // The first run, 3 s after the request, finds the user right.
        runs = Runs();
        await WaitForTimerAsync(UserCreatedConsumer.FirstRunDelay);
        _h.Clock.Advance(UserCreatedConsumer.FirstRunDelay);
        await WaitForAsync(() => Runs() == runs + 1);
        Assert.Single(_h.Server.Writes, w => w.UserId.Equals(user.Id));
        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnotherRequestsSignIn_OfAUserBeingCreated_IsRefusedWhenBroader_TheCreatingRequestsOwnIsCorrected()
    {
        // The creating request's own sign-in (an LDAP or SSO plugin creating the user at their first sign-in) runs after the
        // action that created them is done; any other request's sign-in may not write them (fail closed when broader).
        _ = await StartedWithKidAsDefaultRoleAsync();
        var first = _h.Server.Add("ldap1", allFolders: true);
        var second = _h.Server.Add("ldap2", allFolders: true);
        await Consumer(Request(new CompletingResponse(), "signin-ldap1")).OnEvent(new UserCreatedEventArgs(first));
        await Consumer(Request(new CompletingResponse(), "signin-ldap2")).OnEvent(new UserCreatedEventArgs(second));

        var own = await SignInAsync(SignInResult(first), "signin-ldap1");
        var other = await SignInAsync(SignInResult(second, "second-token"), "another-request");

        Assert.IsType<ObjectResult>(own.Result);
        Assert.False(_h.Server.Cached(first.Id).HasPermission(PermissionKind.EnableAllFolders));
        var problem = Assert.IsType<KitProblemResult>(other.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.True(_h.Server.Cached(second.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.DoesNotContain(_h.Server.Writes, w => w.UserId.Equals(second.Id));
        var refused = Assert.Single(_h.Lines(AuditActions.SignInRefused));
        Assert.Equal((second.Id, SyncReasons.BeingCreated), (refused.TargetUserId!.Value, SyncHarness.Detail(refused, "Reason")));
        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARequestThatNeverSaysItIsDone_LeavesTheUserAloneForAMinute_ThenTheNextRunWritesThem()
    {
        _ = await StartedWithKidAsDefaultRoleAsync();
        var user = _h.Server.Add("newkid", allFolders: true);
        await Consumer(Request(new CompletingResponse(), "create-1")).OnEvent(new UserCreatedEventArgs(user));
        _h.Clock.Advance(LibrarySync.CreationTimeout - TimeSpan.FromSeconds(1));
        Assert.True(_h.Sync.IsBeingCreated(user.Id));
        Assert.Empty((await _h.RunAsync(SyncTrigger.Manual)).Users);

        // The mark lapses; the next run (the backstop's at the latest) writes the user.
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_h.Sync.IsBeingCreated(user.Id));
        Assert.True(Assert.Single((await _h.RunAsync(SyncTrigger.Interval)).Users).Wrote);
        Assert.Equal([Kids.Id], _h.Server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APostWriteCheckOfAUserBeingCreated_WritesNothing()
    {
        // A check after writing that would heal the 10.11.5 sign-in race (the database holds Kids, the cache the stale
        // object): while the user is being created it writes nothing and leaves the user to the runs; afterwards it heals.
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var user = _h.Server.Add("newkid", allFolders: true);
        await _h.Store.UpdateAsync(d => d with { Roles = [kidRole], Assignments = [Assign(SyncHarness.Read(user), [kidRole])] }, TestContext.Current.CancellationToken, notify: false);
        _h.Server.AdminPost(user.Id, p =>
        {
            p.EnableAllFolders = false;
            p.EnabledFolders = [Kids.Id];
        });
        _h.Server.SignInRace(user.Id);
        _h.Writer.RecordAdminSave(user.Id, LibraryFields.Of(false, [Kids.Id]), LibraryFields.All);
        _h.Sync.BeginCreation(user.Id, "create-1");
        _h.Clock.Advance(PolicyWriter.VerifyDelay);

        Assert.True(await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_h.Server.Writes);

        // The control: the same check once the user is created writes the stored row back.
        _h.Sync.EndCreation(user.Id, TimeSpan.Zero);
        _h.Writer.RecordAdminSave(user.Id, LibraryFields.Of(false, [Kids.Id]), LibraryFields.All);
        _h.Clock.Advance(PolicyWriter.VerifyDelay);
        await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken);
        Assert.Single(_h.Server.Writes);
        Assert.False(_h.Server.Cached(user.Id).HasPermission(PermissionKind.EnableAllFolders));
    }

    public void Dispose()
    {
        _service.Dispose();
        _h.Dispose();
    }

    private static IHttpContextAccessor Request(CompletingResponse response, string traceIdentifier)
    {
        var http = new DefaultHttpContext { TraceIdentifier = traceIdentifier };
        http.Features.Set<IHttpResponseFeature>(response);
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(http);
        return accessor.Object;
    }

    // The service started over a store whose default role is Kid (saved without a run), after its startup run.
    private async Task<AccessRole> StartedWithKidAsDefaultRoleAsync()
    {
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.Store.UpdateAsync(d => d with { Roles = [kidRole], DefaultRoleIds = [kidRole.Id] }, TestContext.Current.CancellationToken, notify: false);
        await _service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => LastTrigger() == "Startup");
        return kidRole;
    }

    private UserCreatedConsumer Consumer(IHttpContextAccessor? http) => new(_admin, _h.Sync, _service, _h.Status, NullLogger.Instance, http);

    private async Task<ActionExecutedContext> SignInAsync(AuthenticationResult result, string traceIdentifier)
    {
        var filter = new UserPolicyFilter(ActionManifest.ForBuild(), _h.Locks, _h.Sync, _service, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Server.Manager.Object, new Mock<MediaBrowser.Controller.Session.ISessionManager>().Object, _h.Status, NullLogger.Instance);
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = typeof(Jellyfin.Api.Controllers.UserController).GetTypeInfo(), ActionName = "AuthenticateUserByName" };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()), TraceIdentifier = traceIdentifier };
        ActionExecutedContext? executed = null;
        await filter.OnActionExecutionAsync(new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], new Dictionary<string, object?>(), new object()), () =>
        {
            executed = new ActionExecutedContext(new ActionContext(http, new RouteData(), descriptor), [], new object()) { Result = new ObjectResult(result) };
            return Task.FromResult(executed);
        });
        return executed!;
    }

    private AuthenticationResult SignInResult(User user, string token = "token")
        => new() { User = new UserDto { Id = user.Id, Name = user.Username, Policy = FakeJellyfinUsers.PolicyOf(_h.Server.Cached(user.Id)) }, AccessToken = token };

    private string? LastTrigger() => (_h.Status.Describe(null, "Reapply", 5, 0, 0, 0)["LastRun"] as IDictionary<string, object?>)?["Trigger"] as string;

    private int Runs() => (int)((IDictionary<string, object?>)_h.Status.Describe(null, "Reapply", 5, 0, 0, 0)["Totals"]!)["Runs"]!;

    private async Task WaitForTimerAsync(TimeSpan due)
    {
        // The loop waits on a timer of the manual clock that falls due within this time, before the test moves the clock on.
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
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    // A response whose OnCompleted callbacks run when the test says the response is done.
    private sealed class CompletingResponse : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _completed = [];

        public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Add((callback, state));

        public async Task CompleteAsync()
        {
            foreach (var (callback, state) in _completed)
            {
                await callback(state);
            }
        }
    }
}
