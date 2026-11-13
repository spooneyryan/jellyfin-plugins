using Jellyfin.Plugin.Kit.TestSupport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Policy;

/// <summary>
/// The user policy hooks (<c>UserPolicyPreMerge</c>, <c>PerUserLock</c>, <c>SignInHook</c>)
/// over the library sync's real parts and the 10.11.5 user manager's double: an admin's Dashboard save
/// of a managed user pre-merged under the user's lock (the rules' result, the admin's other fields kept, audited), the lock
/// shared with the writer (the open window closed), <c>ReportOnly</c>'s safe direction, the pass-throughs and
/// the failures (fail open), and the sign-in hook's correction and refreshed response (fail closed for a non-admin whose
/// correction failed, never for an administrator).
/// </summary>
public sealed class UserPolicyFilterTests : IDisposable
{
    private readonly SyncHarness _h = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly LibrarySyncService _service;
    private readonly Guid _adminId = Guid.NewGuid();

    public UserPolicyFilterTests()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        var configuration = new FakeConfigurationProvider(() => _h.Configuration);
        _service = new LibrarySyncService(host.Object, _h.Sync, _h.Store, _h.Writer, null, configuration, _h.Status, NullLogger.Instance, _h.Clock);
    }

    [Fact]
    public void TheFilter_WatchesTheManifestsUserPolicyActions()
    {
        var actions = Filter().WatchedActions;

        Assert.Equal(
            [
                ("AuthenticateUser", RuleKind.SignInHook), ("AuthenticateUserByName", RuleKind.SignInHook), ("AuthenticateWithQuickConnect", RuleKind.SignInHook),
                ("DeleteUser", RuleKind.PerUserLock), ("UpdateUser", RuleKind.PerUserLock), ("UpdateUserLegacy", RuleKind.PerUserLock), ("UpdateUserPolicy", RuleKind.UserPolicyPreMerge)
            ],
            actions.Where(a => a.Key.Controller == "Jellyfin.Api.Controllers.UserController").Select(a => (a.Key.Action, a.Value)).OrderBy(a => a.Action, StringComparer.Ordinal));
        Assert.All(actions.Keys, k => Assert.Equal("Jellyfin.Api.Controllers.UserController", k.Controller));
    }

    [Fact]
    public async Task AnAdminsLibraryEdit_IsPreMerged_WithTheRolesResult_InOneWrite_TheAdminsOtherFieldsKept_AuditedManualEditOverridden()
    {
        var kid = await ManagedKidAsync();
        var page = _h.Server.Load(kid.Id);
        page.EnabledFolders = [Movies.Id, Shows.Id];
        page.IsDisabled = true;
        page.MaxActiveSessions = 4;
        var writes = _h.Server.Writes.Count;

        await SaveAsync(kid.Id, page);

        // One write: the admin's, carrying the rules' result.
        Assert.Equal(writes + 1, _h.Server.Writes.Count);
        var stored = _h.Server.Stored(kid.Id);
        Assert.Equal([Kids.Id], stored.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.True(stored.HasPermission(PermissionKind.IsDisabled));
        Assert.Equal(4, stored.MaxActiveSessions);
        Assert.False(stored.HasPermission(PermissionKind.IsAdministrator));
        var line = Assert.Single(_h.Lines(AuditActions.ManualEditOverridden));
        Assert.Equal((kid.Id, AuditActor.AdminKind, _adminId, "admin"), (line.TargetUserId!.Value, line.Actor.Kind, line.Actor.UserId!.Value, line.Actor.Name));
        var overridden = SyncHarness.Detail(line, "Overridden")!;
        Assert.Contains(Movies.Id.ToString("N"), overridden, StringComparison.Ordinal);
        Assert.Contains(Shows.Id.ToString("N"), overridden, StringComparison.Ordinal);
        Assert.Contains("\"Added\":[", overridden, StringComparison.Ordinal);
        Assert.Equal(SyncTrigger.PolicySave.ToString(), SyncHarness.Detail(line, "Trigger"));

        // Recorded as Access Roles' write: the next run finds nothing to do, and a post-write check is scheduled.
        Assert.Equal([Kids.Id], _h.Store.Current.Ledger[kid.Id].LastWrite!.EnabledFolders);
        Assert.Empty((await _h.RunAsync()).Users);
        Assert.Equal(1, Hooks()[LibrarySyncStatus.ManualEditsOverridden]);
    }

    [Fact]
    public async Task ASaveThatOnlyCarriesAPendingWrite_IsPreMerged_AuditedPolicySynced_NotAsAnEdit()
    {
        var kid = await ManagedKidAsync();
        await _h.SaveAsync(d => d with { Roles = [d.Roles[0] with { Libraries = d.Roles[0].Libraries with { Refs = [.. d.Roles[0].Libraries.Refs, new LibraryReference { Id = Shows.Id, NameAtSelection = "Shows" }] } }] });
        var page = _h.Server.Load(kid.Id);
        page.MaxActiveSessions = 2;

        await SaveAsync(kid.Id, page);

        Assert.Equal(new[] { Kids.Id, Shows.Id }.Order(), _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
        Assert.Empty(_h.Lines(AuditActions.ManualEditOverridden));
        var line = _h.Lines(AuditActions.PolicySynced).Last();
        Assert.Equal((SyncReasons.RulesChanged, "PolicySave", "admin"), (SyncHarness.Detail(line, "Reason"), SyncHarness.Detail(line, "Trigger"), line.Actor.Name));
    }

    [Fact]
    public async Task ReportOnly_KeepsTheAdminsAdd_StripsADeletedLibrarysId_AndReportsTheDriftOnce()
    {
        var kid = await ManagedKidAsync();
        _h.Configuration.DriftPolicy = "ReportOnly";
        var gone = Library(9, "Gone");
        var page = _h.Server.Load(kid.Id);
        page.EnabledFolders = [Kids.Id, Movies.Id, gone.Id];

        await SaveAsync(kid.Id, page);

        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
        var line = Assert.Single(_h.Lines(AuditActions.DriftDetected));
        Assert.Contains(Movies.Id.ToString("N"), SyncHarness.Detail(line, "Kept"), StringComparison.Ordinal);
        Assert.Contains(gone.Id.ToString("N"), SyncHarness.Detail(line, "Applied"), StringComparison.Ordinal);
        Assert.Empty(_h.Lines(AuditActions.ManualEditOverridden));

        // The run that follows sees the same drift and says nothing again.
        var next = Assert.Single((await _h.RunAsync()).Users);
        Assert.Equal(SyncActionKind.Drift, next.Kind);
        Assert.Single(_h.Lines(AuditActions.DriftDetected));
    }

    [Fact]
    public async Task AUserNotManaged_APausedSync_AnInvalidBody_AndAMissingArgument_PassThroughUnchanged()
    {
        var plain = _h.Server.Add("plain", allFolders: true);
        var kid = await ManagedKidAsync();
        var lines = _h.Lines().Count;

        var page = _h.Server.Load(plain.Id);
        page.EnabledFolders = [Movies.Id];
        page.EnableAllFolders = false;
        await SaveAsync(plain.Id, page);
        Assert.Equal([Movies.Id], _h.Server.Stored(plain.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        _h.Configuration.LibrarySyncEnabled = false;
        var paused = _h.Server.Load(kid.Id);
        paused.EnabledFolders = [Movies.Id];
        await SaveAsync(kid.Id, paused);
        Assert.Equal([Movies.Id], _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        _h.Configuration.LibrarySyncEnabled = true;

        var invalid = _h.Server.Load(kid.Id);
        invalid.EnabledFolders = [Shows.Id];
        var calls = 0;
        var context = Context("UpdateUserPolicy", new() { ["userId"] = kid.Id, ["newPolicy"] = invalid });
        context.ModelState.AddModelError("newPolicy.AuthenticationProviderId", "The AuthenticationProviderId field is required.");
        await Filter().OnActionExecutionAsync(context, () => { calls++; return Executed(new BadRequestResult()); });
        await Filter().OnActionExecutionAsync(Context("UpdateUserPolicy", new() { ["userId"] = kid.Id }), () => { calls++; return Executed(new BadRequestResult()); });

        Assert.Equal(2, calls);
        Assert.Equal([Shows.Id], invalid.EnabledFolders);
        Assert.Equal(lines, _h.Lines().Count);
    }

    [Fact]
    public async Task TheWritersLock_HoldsAnAdminsSave_AndTheSaveHoldsTheWriter_SoTheyNeverInterleave()
    {
        // The open window: a commit between the writer's re-read and the core's own load inside
        // UpdatePolicyAsync was written over. With the filter, an admin's save takes the lock the writer holds from its first
        // read to UpdatePolicyAsync.
        var kid = await ManagedKidAsync();
        var held = await _h.Locks.TryAcquireAsync(kid.Id, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var page = _h.Server.Load(kid.Id);
        page.IsDisabled = true;
        var ran = new TaskCompletionSource();
        var save = Filter(lockTimeout: TimeSpan.FromSeconds(10)).OnActionExecutionAsync(Context("UpdateUserPolicy", new() { ["userId"] = kid.Id, ["newPolicy"] = page }), async () =>
        {
            ran.TrySetResult();
            await _h.Server.Manager.Object.UpdatePolicyAsync(kid.Id, page);
            return await Executed(new NoContentResult());
        });
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(ran.Task.IsCompleted, "The admin's save ran while the writer held the user's lock.");
        held!.Dispose();
        await save;
        Assert.True(_h.Server.Stored(kid.Id).HasPermission(PermissionKind.IsDisabled));

        // The other way round: while an admin's save runs, the writer waits for it.
        var release = new TaskCompletionSource();
        var inAction = new TaskCompletionSource();
        var second = _h.Server.Load(kid.Id);
        second.MaxActiveSessions = 7;
        var adminSave = Filter().OnActionExecutionAsync(Context("UpdateUserPolicy", new() { ["userId"] = kid.Id, ["newPolicy"] = second }), async () =>
        {
            inAction.TrySetResult();
            await release.Task;
            await _h.Server.Manager.Object.UpdatePolicyAsync(kid.Id, second);
            return await Executed(new NoContentResult());
        });
        await inAction.Task;
        var writer = new PolicyWriter(_h.Server.Manager.Object, _h.Locks, NullLogger.Instance, _h.Clock, TimeSpan.FromSeconds(10), TimeSpan.Zero, _h.Server.Committed, TimeSpan.Zero);
        var writes = _h.Server.Writes.Count;
        var write = writer.WriteAsync(kid.Id, LibraryFields.Of(false, [Shows.Id]), TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(writes, _h.Server.Writes.Count);
        release.TrySetResult();
        await adminSave;
        var result = await write;

        // The writer built its write from the admin's committed save: its fields survive, IsDisabled and the sessions too.
        Assert.Equal(PolicyWriteOutcome.Written, result.Outcome);
        var stored = _h.Server.Stored(kid.Id);
        Assert.Equal((true, 7, false), (stored.HasPermission(PermissionKind.IsDisabled), stored.MaxActiveSessions, stored.HasPermission(PermissionKind.IsAdministrator)));
        Assert.Equal([Shows.Id], stored.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ALockThatStaysBusy_LetsTheSaveThroughWithoutIt_StillPreMerged_FailOpen()
    {
        var kid = await ManagedKidAsync();
        using var held = await _h.Locks.TryAcquireAsync(kid.Id, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var page = _h.Server.Load(kid.Id);
        page.EnabledFolders = [Movies.Id];

        await SaveAsync(kid.Id, page, Filter(lockTimeout: TimeSpan.FromMilliseconds(50)));

        Assert.Equal([Kids.Id], _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal(1, Hooks()[LibrarySyncStatus.LockTimeouts]);
    }

    [Fact]
    public async Task APreMergeThatThrows_LetsTheSaveThroughUnchanged_AuditedDriftDetected_HookFailed()
    {
        var kid = await ManagedKidAsync();
        var page = _h.Server.Load(kid.Id);
        page.EnabledFolders = [Movies.Id];
        // The catalog throws on its read (a broken library manager).
        using var failing = new FailingCatalogHarness(_h);
        await SaveAsync(kid.Id, page, failing.Filter);

        Assert.Equal([Movies.Id], _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var line = _h.Lines(AuditActions.DriftDetected).Last();
        Assert.Equal(SyncReasons.HookFailed, SyncHarness.Detail(line, "Reason"));
        Assert.Equal(1, Hooks()[LibrarySyncStatus.FilterErrors]);
    }

    [Fact]
    public async Task TheLockoutGuard_LeavesAnAdminsSaveAlone_WhenTheMergeWouldNarrowTheOnlyFullAdministrator()
    {
        var admin = _h.Server.Add("root", admin: true, allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with { Roles = [role], Assignments = [Assign(SyncHarness.Read(admin), [role])] });
        var page = _h.Server.Load(admin.Id);
        page.MaxActiveSessions = 3;

        await SaveAsync(admin.Id, page);

        Assert.True(_h.Server.Stored(admin.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(3, _h.Server.Stored(admin.Id).MaxActiveSessions);
        Assert.Empty(_h.Lines(AuditActions.ManualEditOverridden));
        Assert.Empty(_h.Lines(AuditActions.PolicySynced));
    }

    [Fact]
    public async Task AnAdminsUserEdit_AndDelete_TakeTheLock_ANonAdminsOwnEditDoesnt()
    {
        var kid = await ManagedKidAsync();
        using (await _h.Locks.TryAcquireAsync(kid.Id, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken))
        {
            var edits = 0;
            // The admin's edit waits a second (1.0.1: 50 ms, which the user's own edit sometimes outlasted under a loaded machine,
            // about 1 full run in 20, so both had run at the first count).
            var asAdmin = Filter(lockTimeout: TimeSpan.FromSeconds(1)).OnActionExecutionAsync(Context("UpdateUser", new() { ["userId"] = kid.Id }), () => { edits++; return Executed(new NoContentResult()); });
            var own = Filter(lockTimeout: TimeSpan.FromMilliseconds(50)).OnActionExecutionAsync(Context("UpdateUser", new() { ["userId"] = (Guid?)null }, caller: Caller(kid.Id, admin: false)), () => { edits++; return Executed(new NoContentResult()); });
            await own;
            Assert.Equal(1, edits);
            await asAdmin;
            Assert.Equal(2, edits);
        }

        // The admin's edit waited for the lock and went on without it (fail open); the user's own didn't wait.
        Assert.Equal(1, Hooks()[LibrarySyncStatus.LockTimeouts]);
        var deleted = false;
        await Filter().OnActionExecutionAsync(Context("DeleteUser", new() { ["userId"] = kid.Id }), () => { deleted = true; return Executed(new NoContentResult()); });
        Assert.True(deleted);
    }

    [Fact]
    public async Task ASignIn_OfAManagedUserWhoseAccessDrifted_IsCorrectedBeforeTheResponse_ItsUserRefreshed_AndTheBackstopAskedFor()
    {
        // An IHasNewUserPolicy provider (LDAP, SSO) writes the policy inside AuthenticateUser: drifted to every library.
        var kid = await ManagedKidAsync();
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);
        var result = SignInResult(kid);
        Assert.True(result.User.Policy.EnableAllFolders);

        var executed = await SignInAsync("AuthenticateUserByName", result);

        Assert.Same(result, ((ObjectResult)executed.Result!).Value);
        Assert.False(result.User.Policy.EnableAllFolders);
        Assert.Equal([Kids.Id], result.User.Policy.EnabledFolders);
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        var line = _h.Lines(AuditActions.PolicySynced).Last();
        Assert.Equal((SyncReasons.Drift, "SignIn", AuditActor.SystemKind), (SyncHarness.Detail(line, "Reason"), SyncHarness.Detail(line, "Trigger"), line.Actor.Kind));
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)],
            _service.PendingRequests.Where(r => r.Trigger == SyncTrigger.SignIn).Select(r => r.Due - _h.Now));
        Assert.Equal(1, Hooks()[LibrarySyncStatus.SignInsCorrected]);
        _sessions.Verify(s => s.Logout(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ASignInOfTheOnlyFullAdministrator_WithAnUnconfirmedNarrowingPlan_WritesNothing_AndIsntRefused()
    {
        // A mutation test's survivor: PlanUser's lockout guard on the sign-in path. The only full administrator has a library role
        // that narrows them, never confirmed: the sign-in writes nothing (Skip NoFullAdministrator), and an administrator is
        // never refused.
        var admin = _h.Server.Add("root", admin: true, allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with { Roles = [role], Assignments = [Assign(SyncHarness.Read(admin), [role])] });
        var result = SignInResult(admin, token: "root-token");

        var executed = await SignInAsync("AuthenticateUserByName", result);

        Assert.Same(result, ((ObjectResult)executed.Result!).Value);
        Assert.True(_h.Server.Cached(admin.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.True(_h.Server.Stored(admin.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Empty(_h.Lines(AuditActions.PolicySynced));
        var skipped = Assert.Single(_h.Lines(AuditActions.SyncSkipped));
        Assert.Equal(SyncReasons.NoFullAdministrator, SyncHarness.Detail(skipped, "Reason"));
        _sessions.Verify(s => s.Logout(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ASignIn_OfAUserNotManaged_OrInSync_ChangesNothing_AndAsksForNoRun()
    {
        var plain = _h.Server.Add("plain", allFolders: true);
        var kid = await ManagedKidAsync();
        var writes = _h.Server.Writes.Count;

        await SignInAsync("AuthenticateUserByName", SignInResult(plain));
        Assert.Empty(_service.PendingRequests);
        var inSync = SignInResult(kid);
        var dto = inSync.User;
        await SignInAsync("AuthenticateWithQuickConnect", inSync);

        // Nothing written, the response's User the core's; only the managed user's sign-in asks for the backstop's two runs.
        Assert.Equal(writes, _h.Server.Writes.Count);
        Assert.Same(dto, inSync.User);
        Assert.Equal(2, _service.PendingRequests.Count(r => r.Trigger == SyncTrigger.SignIn));
    }

    [Fact]
    public async Task ASignInWhoseCorrectionFails_IsRefused_ItsTokenRevoked_ForANonAdmin_NeverForAnAdministrator()
    {
        var kid = await ManagedKidAsync();
        var admin = _h.Server.Add("root", admin: true, allFolders: true);
        _ = _h.Server.Add("root2", admin: true, allFolders: true);
        await _h.SaveAsync(d => d with { Assignments = [.. d.Assignments, Assign(SyncHarness.Read(admin), [d.Roles[0]]) with { ConfirmedNoFullAdminUtc = _h.Now }] });
        Assert.True((await _h.RunAsync()).Users.Single(u => u.User.Id.Equals(admin.Id)).Wrote);
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);
        _h.Server.AdminPost(admin.Id, p => p.EnableAllFolders = true);

        // The users' policy locks stay busy past the writer's wait: the correction fails.
        using var kidLock = await _h.Locks.TryAcquireAsync(kid.Id, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        using var adminLock = await _h.Locks.TryAcquireAsync(admin.Id, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var refused = await SignInAsync("AuthenticateUserByName", SignInResult(kid, token: "kid-token"));
        var allowed = await SignInAsync("AuthenticateUserByName", SignInResult(admin, token: "admin-token"));

        var problem = Assert.IsType<KitProblemResult>(refused.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal("5", refused.HttpContext.Response.Headers.RetryAfter.ToString());
        _sessions.Verify(s => s.Logout("kid-token"), Times.Once);
        _sessions.Verify(s => s.Logout("admin-token"), Times.Never);
        Assert.IsType<ObjectResult>(allowed.Result);
        var line = Assert.Single(_h.Lines(AuditActions.SignInRefused));
        Assert.Equal((kid.Id, SyncReasons.LockTimeout, "true"), (line.TargetUserId!.Value, SyncHarness.Detail(line, "Reason"), SyncHarness.Detail(line, "TokenRevoked")));
        Assert.Equal(1, Hooks()[LibrarySyncStatus.SignInsRefused]);
    }

    [Fact]
    public async Task ASignInWhileALongRunIsUnderWay_IsCorrectedUnderTheUsersLockOnly_NotRefused()
    {
        // The correction no longer waits for the run lock. A run held up writing another user (hundreds of
        // users, a busy database) doesn't refuse a sign-in whose rules just narrowed; the run then finds the user written.
        var kid = await ManagedKidAsync();
        // The run writes users by name: "busy" first, held up, then "other".
        var slow = _h.Server.Add("busy", allFolders: true);
        var other = _h.Server.Add("other", allFolders: true);
        await _h.SaveAsync(d => d with
        {
            Assignments = [.. d.Assignments, Assign(SyncHarness.Read(slow), [d.Roles[0]]), Assign(SyncHarness.Read(other), [d.Roles[0]])],
            Ledger = new Dictionary<Guid, LedgerEntry>(d.Ledger) { [slow.Id] = Ledger(true), [other.Id] = Ledger(true) }
        });
        using var gate = new ManualResetEventSlim(false);
        var held = 0;
        _h.Server.BeforeGetUserById = (id, _) =>
        {
            if (id.Equals(slow.Id) && Interlocked.Exchange(ref held, 1) == 0)
            {
                gate.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var run = Task.Run(() => _h.RunAsync());
        while (Volatile.Read(ref held) == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var inSync = await SignInAsync("AuthenticateUserByName", SignInResult(kid, token: "kid-token"));
        var narrowed = SignInResult(other, token: "other-token");
        var corrected = await SignInAsync("AuthenticateUserByName", narrowed);
        gate.Set();
        var report = await run;
        _h.Server.BeforeGetUserById = null;

        Assert.IsType<ObjectResult>(inSync.Result);
        Assert.IsType<ObjectResult>(corrected.Result);
        Assert.Equal([Kids.Id], narrowed.User.Policy.EnabledFolders);
        Assert.False(_h.Server.Cached(other.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Empty(_h.Lines(AuditActions.SignInRefused));
        _sessions.Verify(s => s.Logout(It.IsAny<string>()), Times.Never);
        var ran = report.Users.Single(u => u.User.Id.Equals(other.Id));
        Assert.Equal(PolicyWriteOutcome.Unchanged, ran.Outcome);
        Assert.Single(_h.Server.Writes, w => w.UserId.Equals(other.Id));
    }

    [Fact]
    public async Task TheResponsesUser_IsRefreshed_WheneverTheServerEnforcesOtherLibraries_EvenWhenTheHookWroteNothing()
    {
        // A run corrected the user while the sign-in was under way, so the hook had nothing to
        // write, and the response's User still showed the access from before.
        var kid = await ManagedKidAsync();
        await PausedAsync(() => _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true));
        var result = SignInResult(kid);
        Assert.True(result.User.Policy.EnableAllFolders);
        await _h.RunAsync();
        var writes = _h.Server.Writes.Count;

        await SignInAsync("AuthenticateUserByName", result);

        Assert.Equal(writes, _h.Server.Writes.Count);
        Assert.False(result.User.Policy.EnableAllFolders);
        Assert.Equal([Kids.Id], result.User.Policy.EnabledFolders);
    }

    [Fact]
    public async Task WhileTheStoreHoldsTheSync_ASignInBroaderThanTheLastWriteIsRefused_AnAdminsPauseRefusesNobody()
    {
        // The store's pauses (here the restore hold) refuse a non-admin whose access is more than
        // Access Roles last wrote, with Retry-After; an admin's pause (the sync switched off) leaves library access untouched
        // as it is; an administrator is never refused; nothing is written either way.
        var kid = await ManagedKidAsync();
        var admin = _h.Server.Add("root", admin: true, allFolders: true);
        _ = _h.Server.Add("root2", admin: true, allFolders: true);
        var narrow = _h.Server.Add("narrow", allFolders: true);
        await _h.SaveAsync(d => d with
        {
            Assignments = [.. d.Assignments, Assign(SyncHarness.Read(admin), [d.Roles[0]]) with { ConfirmedNoFullAdminUtc = _h.Now }, Assign(SyncHarness.Read(narrow), [d.Roles[0]])],
            Ledger = new Dictionary<Guid, LedgerEntry>(d.Ledger) { [narrow.Id] = Ledger(true) }
        });
        Assert.Equal(2, (await _h.RunAsync()).Written);
        await System.IO.File.WriteAllTextAsync(_h.Paths.RestoreHoldFile, "{}", TestContext.Current.CancellationToken);
        Assert.Equal(SyncReasons.StoreRestored, _h.Sync.PauseReason());
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);
        _h.Server.AdminPost(admin.Id, p => p.EnableAllFolders = true);
        _h.Server.AdminPost(narrow.Id, p => p.EnabledFolders = []);
        var writes = _h.Server.Writes.Count;

        var refused = await SignInAsync("AuthenticateUserByName", SignInResult(kid, token: "kid-token"));
        var adminIn = await SignInAsync("AuthenticateUserByName", SignInResult(admin, token: "admin-token"));
        var narrowIn = await SignInAsync("AuthenticateUserByName", SignInResult(narrow, token: "narrow-token"));

        var problem = Assert.IsType<KitProblemResult>(refused.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal("60", refused.HttpContext.Response.Headers.RetryAfter.ToString());
        Assert.IsType<ObjectResult>(adminIn.Result);
        Assert.IsType<ObjectResult>(narrowIn.Result);
        _sessions.Verify(s => s.Logout("kid-token"), Times.Once);
        _sessions.Verify(s => s.Logout(It.Is<string>(t => t != "kid-token")), Times.Never);
        var line = Assert.Single(_h.Lines(AuditActions.SignInRefused));
        Assert.Equal((kid.Id, SyncReasons.StoreRestored, "true", "true"), (line.TargetUserId!.Value, SyncHarness.Detail(line, "Reason"), SyncHarness.Detail(line, "Held"), SyncHarness.Detail(line, "TokenRevoked")));
        Assert.Contains(Kids.Id.ToString("N"), SyncHarness.Detail(line, "Intended"), StringComparison.Ordinal);

        // An admin's pause: the same drifted user signs in, nothing refused, nothing written.
        _h.Configuration.LibrarySyncEnabled = false;
        var paused = await SignInAsync("AuthenticateUserByName", SignInResult(kid, token: "kid-token-2"));
        Assert.IsType<ObjectResult>(paused.Result);
        Assert.Single(_h.Lines(AuditActions.SignInRefused));
        Assert.Equal(writes, _h.Server.Writes.Count);
        Assert.True(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
    }

    [Theory]
    [InlineData(SyncReasons.StoreRestored, true)]
    [InlineData(SyncReasons.StoreReadOnly, true)]
    [InlineData(SyncReasons.StoreDegraded, true)]
    [InlineData(SyncReasons.SafeMode, false)]
    [InlineData(SyncReasons.EnforcementDisabled, false)]
    [InlineData(SyncReasons.LibrarySyncDisabled, false)]
    [InlineData(null, false)]
    public void TheStoresPauses_AreTheOnesThatRefuse(string? pause, bool storeCaused)
        => Assert.Equal(storeCaused, SyncReasons.IsStoreCaused(pause));

    [Fact]
    public async Task ARefusalWhoseTokenCouldntBeRevoked_SaysSo()
    {
        // TokenRevoked is what happened, not always true.
        var kid = await ManagedKidAsync();
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);
        _sessions.Setup(s => s.Logout(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("The session store is gone."));
        using var kidLock = await _h.Locks.TryAcquireAsync(kid.Id, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        var refused = await SignInAsync("AuthenticateUserByName", SignInResult(kid, token: "kid-token"));

        Assert.IsType<KitProblemResult>(refused.Result);
        Assert.Equal("false", SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.SignInRefused)), "TokenRevoked"));
    }

    [Fact]
    public async Task APluginsOwnSignIn_IsHooked_AJellyfinActionOutsideTheTableIsnt()
    {
        var kid = await ManagedKidAsync();
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);

        var plugin = SignInResult(kid);
        await Filter().OnActionExecutionAsync(Context("SignIn", [], typeof(Jellyfin.Plugin.SsoFixture.SsoController)), () => Executed(new ObjectResult(plugin)));

        Assert.False(plugin.User.Policy.EnableAllFolders);
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
    }

    public void Dispose()
    {
        _service.Dispose();
        _h.Dispose();
    }

    private static Task<ActionExecutedContext> Executed(IActionResult? result, Exception? exception = null)
        => Task.FromResult(new ActionExecutedContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [], new object()) { Result = result, Exception = exception });

    private static ClaimsPrincipal Caller(Guid userId, bool admin)
        => new(new ClaimsIdentity(
            admin
                ? [new Claim("Jellyfin-UserId", userId.ToString("N")), new Claim(ClaimTypes.Role, UserPolicyFilter.AdministratorRole)]
                : [new Claim("Jellyfin-UserId", userId.ToString("N"))],
            "Test"));

    private ActionExecutingContext Context(string action, Dictionary<string, object?> arguments, Type? controller = null, ClaimsPrincipal? caller = null)
    {
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = (controller ?? typeof(Jellyfin.Api.Controllers.UserController)).GetTypeInfo(),
            ActionName = action
        };
        var http = new DefaultHttpContext { User = caller ?? Caller(_adminId, admin: true) };
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], arguments, new object());
    }

    private UserPolicyFilter Filter(TimeSpan? lockTimeout = null, ILibraryCatalogSource? catalog = null)
    {
        var users = new FakeServerUsers(id => id.Equals(_adminId) ? new ServerUser(_adminId, "admin", true, false, true, NativeAccess.Default) : null);
        return new UserPolicyFilter(ActionManifest.ForBuild(), _h.Locks, _h.Sync, _service, users, _h.Server.Manager.Object, _sessions.Object, _h.Status, NullLogger.Instance, lockTimeout, TimeSpan.FromSeconds(1));
    }

    // An admin's POST /Users/{id}/Policy through the filter: the "action" is the core's UpdatePolicyAsync of the (pre-merged) body.
    private async Task SaveAsync(Guid userId, UserPolicy policy, UserPolicyFilter? filter = null)
        => await (filter ?? Filter()).OnActionExecutionAsync(Context("UpdateUserPolicy", new() { ["userId"] = userId, ["newPolicy"] = policy }), async () =>
        {
            await _h.Server.Manager.Object.UpdatePolicyAsync(userId, policy);
            return await Executed(new NoContentResult());
        });

    private async Task<ActionExecutedContext> SignInAsync(string action, AuthenticationResult result)
    {
        ActionExecutedContext? executed = null;
        var context = Context(action, [], caller: new ClaimsPrincipal(new ClaimsIdentity()));
        await Filter().OnActionExecutionAsync(context, () =>
        {
            executed = new ActionExecutedContext(context, [], new object()) { Result = new ObjectResult(result) };
            return Task.FromResult(executed);
        });
        return executed!;
    }

    private AuthenticationResult SignInResult(User user, string token = "token")
        => new() { User = new UserDto { Id = user.Id, Name = user.Username, Policy = FakeJellyfinUsers.PolicyOf(_h.Server.Cached(user.Id)) }, AccessToken = token };

    private async Task<User> ManagedKidAsync()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with
        {
            Roles = [role],
            Assignments = [Assign(SyncHarness.Read(kid), [role])],
            Ledger = new Dictionary<Guid, LedgerEntry> { [kid.Id] = new LedgerEntry { OriginalEnableAllFolders = true, OriginalEnableUserPreferenceAccess = true, CapturedUtc = _h.Now } }
        });
        Assert.True(Assert.Single((await _h.RunAsync()).Users).Wrote);
        return kid;
    }

    private IDictionary<string, object?> Hooks() => (IDictionary<string, object?>)_h.Status.Describe(null, "Reapply", 5, 0, 0, 0)["Hooks"]!;

    // A native edit while the sync is switched off (an admin's pause), so a run's drift rules see it afterwards.
    private async Task PausedAsync(Action edit)
    {
        _h.Configuration.LibrarySyncEnabled = false;
        edit();
        _h.Configuration.LibrarySyncEnabled = true;
        await Task.CompletedTask;
    }

    // A second sync over the harness's store and users whose catalog throws, with its own filter.
    private sealed class FailingCatalogHarness : IDisposable
    {
        private readonly LibrarySync _sync;
        private readonly LibrarySyncService _service;

        public FailingCatalogHarness(SyncHarness h)
        {
            var catalog = new FakeCatalogSource(() => throw new InvalidOperationException("The library manager is gone."));
            var configuration = new FakeConfigurationProvider(() => h.Configuration);
            _sync = new LibrarySync(h.Store, catalog, new JellyfinServerUsers(h.Server.Manager.Object), h.Writer, h.Audit, configuration, h.Paths, h.Status, NullLogger.Instance, h.Clock);
            var host = new Mock<IServerApplicationHost>();
            _service = new LibrarySyncService(host.Object, _sync, h.Store, h.Writer, null, configuration, h.Status, NullLogger.Instance, h.Clock);
            Filter = new UserPolicyFilter(ActionManifest.ForBuild(), h.Locks, _sync, _service, new JellyfinServerUsers(h.Server.Manager.Object), h.Server.Manager.Object, null, h.Status, NullLogger.Instance);
        }

        public UserPolicyFilter Filter { get; }

        public void Dispose()
        {
            _service.Dispose();
            _sync.Dispose();
        }
    }
}
