using Jellyfin.Plugin.Kit.TestSupport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller;
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
/// The user events (default roles for new users; <c>UserCreatedConsumer</c>, <c>UserUpdatedConsumer</c>):
/// a new user gets the default roles, the sync runs 3 s after the creating request is
/// done, then 10 s and 30 s after it (<see cref="UserCreationTests"/> runs it with the service started), the
/// stock <i>Add user</i> page's policy post that follows is pre-merged; a new user whose roles give no library gets
/// nothing (fail closed) where an existing user is left alone; the 12.1+ user-updated event asks for a run unless it is the
/// writer's own write, and does nothing on 10.11.
/// </summary>
public sealed class UserEventsTests : IDisposable
{
    private readonly SyncHarness _h = new();
    private readonly LibrarySyncService _service;
    private readonly AccessRolesAdminService _admin;
    private readonly Mock<IServerApplicationHost> _host = new();

    public UserEventsTests()
    {
        _host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        var configuration = new FakeConfigurationProvider(() => _h.Configuration);
        _service = new LibrarySyncService(_host.Object, _h.Sync, _h.Store, _h.Writer, null, configuration, _h.Status, NullLogger.Instance, _h.Clock);
        _admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);
    }

    [Fact]
    public async Task ANewUser_GetsTheDefaultRoles_TheLedgerMarked_Audited_AndTheSyncRunsSoonAndTwiceMore()
    {
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var missing = Role(2, "Gone");
        await _h.SaveAsync(d => d with { Roles = [kidRole], DefaultRoleIds = [kidRole.Id, missing.Id] });
        var user = _h.Server.Add("newkid", allFolders: true);

        await Consumer().OnEvent(new UserCreatedEventArgs(user));

        var assignment = EffectiveRulesResolver.FindAssignment(_h.Store.Current, user.Id)!;
        Assert.Equal([kidRole.Id], assignment.RoleIds);
        // Created under the default roles: the original access is none, not the server's every library.
        var entry = _h.Store.Current.Ledger[user.Id];
        Assert.False(entry.OriginalEnableAllFolders);
        Assert.Empty(entry.OriginalEnabledFolders);
        Assert.Equal(_h.Now, entry.DefaultRolesUtc);
        var line = Assert.Single(_h.Lines(AuditActions.UserCreatedDefaulted));
        Assert.Equal((user.Id, AuditActor.SystemKind), (line.TargetUserId!.Value, line.Actor.Kind));
        Assert.Contains(missing.Id.ToString("N"), SyncHarness.Detail(line, "MissingRoleIds"), StringComparison.Ordinal);
        Assert.Contains(Kids.Id.ToString("N"), SyncHarness.Detail(line, "EnabledFolders"), StringComparison.Ordinal);
        Assert.Single(_h.Lines(AuditActions.AssignmentChanged));
        // Without a request to wait for, 3 s later, then 10 s and 30 s; the save itself raised no store change.
        Assert.Equal(
            [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)],
            _service.PendingRequests.Where(r => r.Trigger == SyncTrigger.UserCreated).Select(r => r.Due - _h.Now));
        Assert.DoesNotContain(_service.PendingRequests, r => r.Trigger == SyncTrigger.StoreChanged);
        Assert.True(_h.Sync.IsBeingCreated(user.Id));
        _h.Clock.Advance(UserCreatedConsumer.FirstRunDelay);
        Assert.False(_h.Sync.IsBeingCreated(user.Id));

        // The run writes the role's result.
        Assert.True(Assert.Single((await _h.RunAsync(SyncTrigger.UserCreated)).Users).Wrote);
        Assert.Equal([Kids.Id], _h.Server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task InsideTheCreatingRequest_TheFirstRunWaitsUntilTheResponseIsDone()
    {
        // CreateUserByName saves the password after the event, through UpdateUserAsync on the entity it created: a policy
        // write of ours in between fails that save (seen in a sandbox run: 500, the user without a password). So the runs are
        // asked for once the response has completed.
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with { Roles = [kidRole], DefaultRoleIds = [kidRole.Id] });
        var user = _h.Server.Add("newkid", allFolders: true);
        var response = new CompletingResponse();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(response);
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(http);

        await new UserCreatedConsumer(_admin, _h.Sync, _service, _h.Status, NullLogger.Instance, accessor.Object).OnEvent(new UserCreatedEventArgs(user));

        // Until the response is done, no run is asked for, and the user is being created.
        Assert.NotNull(EffectiveRulesResolver.FindAssignment(_h.Store.Current, user.Id));
        Assert.Empty(_service.PendingRequests);
        Assert.True(_h.Sync.IsBeingCreated(user.Id));
        await response.CompleteAsync();
        Assert.Equal(
            [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)],
            _service.PendingRequests.Where(r => r.Trigger == SyncTrigger.UserCreated).Select(r => r.Due - _h.Now));
    }

    [Fact]
    public async Task NoDefaultRoles_OrAnExistingAssignment_GiveNothing()
    {
        var user = _h.Server.Add("plain", allFolders: true);
        await Consumer().OnEvent(new UserCreatedEventArgs(user));
        Assert.Empty(_h.Store.Current.Assignments);

        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var other = Role(2, "Other", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var remapped = _h.Server.Add("remapped", allFolders: true);
        await _h.SaveAsync(d => d with { Roles = [kidRole, other], DefaultRoleIds = [kidRole.Id], Assignments = [Assign(SyncHarness.Read(remapped), [other])] });
        await Consumer().OnEvent(new UserCreatedEventArgs(remapped));
        Assert.Equal([other.Id], EffectiveRulesResolver.FindAssignment(_h.Store.Current, remapped.Id)!.RoleIds);
        Assert.Empty(_h.Lines(AuditActions.UserCreatedDefaulted));
        Assert.Empty(_service.PendingRequests);
    }

    [Fact]
    public async Task ANewUserWhoseDefaultRolesGiveNoLibrary_GetsNoLibrary_FailClosed_WhereAnExistingUserIsLeftAlone()
    {
        // The default role's only library was deleted: an existing member keeps their native access (no empty result by
        // accident), a new user has nothing an admin gave them and gets nothing.
        var gone = Library(9, "Gone");
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [gone]);
        var existing = _h.Server.Add("existing", allFolders: false, folders: [Movies.Id]);
        await _h.SaveAsync(d => d with
        {
            Roles = [kidRole with { Libraries = kidRole.Libraries with { Refs = [kidRole.Libraries.Refs[0] with { Orphaned = true }] } }],
            DefaultRoleIds = [kidRole.Id],
            Assignments = [Assign(SyncHarness.Read(existing), [kidRole])],
            Ledger = new Dictionary<Guid, LedgerEntry> { [existing.Id] = Ledger(false, Movies) }
        });
        var user = _h.Server.Add("newkid", allFolders: true);
        await Consumer().OnEvent(new UserCreatedEventArgs(user));
        _h.Clock.Advance(UserCreatedConsumer.FirstRunDelay);

        var report = await _h.RunAsync(SyncTrigger.UserCreated);

        var created = report.Users.Single(u => u.User.Id.Equals(user.Id));
        Assert.Equal((SyncActionKind.Write, SyncReasons.NewUserNoLibraries, true), (created.Kind, created.Reason, created.Wrote));
        Assert.False(_h.Server.Cached(user.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Empty(_h.Server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var kept = report.Users.Single(u => u.User.Id.Equals(existing.Id));
        Assert.Equal((SyncActionKind.Skip, SyncReasons.NoLibraries), (kept.Kind, kept.Reason));
        Assert.Equal([Movies.Id], _h.Server.Cached(existing.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Single(_h.Lines(AuditActions.UsersWithoutLibraries));
    }

    [Fact]
    public async Task TheAddUserPagesPolicyPost_AfterTheDefaultRoles_IsPreMerged_AndItsTicksAreAudited()
    {
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with { Roles = [kidRole], DefaultRoleIds = [kidRole.Id] });
        var user = _h.Server.Add("newkid", allFolders: true);
        await Consumer().OnEvent(new UserCreatedEventArgs(user));

        // The stock page posts its own policy right after the creation: the libraries ticked on it.
        var page = _h.Server.Load(user.Id);
        page.EnableAllFolders = false;
        page.EnabledFolders = [Movies.Id, Shows.Id];
        var admin = Guid.NewGuid();
        await Filter().OnActionExecutionAsync(Context("UpdateUserPolicy", new() { ["userId"] = user.Id, ["newPolicy"] = page }, admin), async () =>
        {
            await _h.Server.Manager.Object.UpdatePolicyAsync(user.Id, page);
            return new ActionExecutedContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [], new object()) { Result = new NoContentResult() };
        });

        Assert.Equal([Kids.Id], _h.Server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var line = Assert.Single(_h.Lines(AuditActions.ManualEditOverridden));
        Assert.Contains(Movies.Id.ToString("N"), SyncHarness.Detail(line, "Posted"), StringComparison.Ordinal);
        Assert.Contains(Shows.Id.ToString("N"), SyncHarness.Detail(line, "Overridden"), StringComparison.Ordinal);
        _h.Clock.Advance(UserCreatedConsumer.FirstRunDelay);
        var after = await _h.RunAsync(SyncTrigger.UserCreated);
        Assert.Empty(after.Users);
        Assert.Empty(after.BeingCreated);
    }

    [Fact]
    public async Task AUserCreatedUnderTheDefaultRoles_RestoresToNoLibraries_ThePreviewsSayWhy_AndTheSyncWritesNone()
    {
        // Restore never widens a user born under default roles (their original access was the
        // server's default for a new user, every library), so it is "no libraries", and the Unmanage and role-delete
        // previews say so.
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with { Roles = [kidRole], DefaultRoleIds = [kidRole.Id] });
        var user = _h.Server.Add("newkid", allFolders: true);
        await Consumer().OnEvent(new UserCreatedEventArgs(user));
        _h.Clock.Advance(UserCreatedConsumer.FirstRunDelay);
        Assert.True(Assert.Single((await _h.RunAsync(SyncTrigger.UserCreated)).Users).Wrote);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test"));
        var token = TestContext.Current.CancellationToken;

        var unmanage = await _admin.UnmanageAsync(user.Id, new AccessRolesUnmanageRequest { Choice = AccessRolesUnmanageChoice.Restore }, new AccessRolesWriteOptions { DryRun = true }, admin, token);
        var leaving = unmanage.Preview.Users.Single(u => u.UserId.Equals(user.Id)).LeavesManagement!;
        Assert.Equal((true, AccessRolesMapper.RestoresToNoLibraries), (leaving.CreatedUnderDefaultRoles, leaving.Note));
        Assert.Equal((false, 0), (leaving.After!.EnableAllFolders, leaving.After.EnabledFolders.Count));

        await _h.SaveAsync(d => d with { DefaultRoleIds = [] });
        var delete = await _admin.DeleteRoleAsync(kidRole.Id, null, null, null, new AccessRolesWriteOptions { DryRun = true, OnUnmanage = AccessRolesUnmanageChoice.Restore }, admin, token);
        Assert.Equal(AccessRolesMapper.RestoresToNoLibraries, delete.Preview.Users.Single(u => u.UserId.Equals(user.Id)).LeavesManagement!.Note);

        // Saved, then written by the sync: no library, never every library.
        await _admin.UnmanageAsync(user.Id, new AccessRolesUnmanageRequest { Choice = AccessRolesUnmanageChoice.Restore }, new AccessRolesWriteOptions(), admin, token);
        var release = Assert.Single((await _h.RunAsync()).Users);
        Assert.Equal((SyncActionKind.Release, true), (release.Kind, release.Wrote));
        Assert.False(_h.Server.Cached(user.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Empty(_h.Server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var line = _h.Lines(AuditActions.AssignmentChanged).Last(l => l.TargetUserId == user.Id);
        Assert.Contains("\"CreatedUnderDefaultRoles\":true", SyncHarness.Detail(line, "LeavesManagement"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALedgerEntryA0940BuildWroteWithEveryLibrary_ForAUserCreatedUnderTheDefaultRoles_ReadsAsNoLibraries()
    {
        // 0.9.4.0 captured the server's default (every library) for a user given the default roles; read now, it is none,
        // and it is saved that way.
        var json = "{\"OriginalEnableAllFolders\":true,\"OriginalEnabledFolders\":[\"" + Kids.Id.ToString("D") + "\"],\"OriginalEnableUserPreferenceAccess\":true,\"CapturedUtc\":\"2026-10-20T10:00:00Z\",\"DefaultRolesUtc\":\"2026-10-20T10:00:00Z\"}";
        var entry = System.Text.Json.JsonSerializer.Deserialize<LedgerEntry>(json, Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options)!;

        Assert.False(entry.OriginalEnableAllFolders);
        Assert.Empty(entry.OriginalEnabledFolders);
        Assert.True(entry.OriginalEnableUserPreferenceAccess);
        var saved = System.Text.Json.JsonSerializer.Serialize(entry, Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options);
        Assert.Matches(@"""OriginalEnableAllFolders"":\s*false", saved);
        Assert.Matches(@"""OriginalEnabledFolders"":\s*\[\s*\]", saved);

        // Without the mark (an existing user's entry), every library stays the original access.
        var existing = System.Text.Json.JsonSerializer.Deserialize<LedgerEntry>(json.Replace(",\"DefaultRolesUtc\":\"2026-10-20T10:00:00Z\"", string.Empty, StringComparison.Ordinal), Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options)!;
        Assert.True(existing.OriginalEnableAllFolders);
    }

    [Theory]
    [InlineData("10.11.5", false)]
    [InlineData("12.1.0", true)]
    public async Task TheUserUpdatedEvent_AsksForARun_On121AndLater_ButNotForTheWritersOwnWrite(string version, bool asks)
    {
        _host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version));
        var kidRole = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var kid = _h.Server.Add("kid", allFolders: true);
        var plain = _h.Server.Add("plain", allFolders: true);
        await _h.SaveAsync(d => d with { Roles = [kidRole], Assignments = [Assign(SyncHarness.Read(kid), [kidRole])], Ledger = new Dictionary<Guid, LedgerEntry> { [kid.Id] = Ledger(true) } });
        Assert.True(Assert.Single((await _h.RunAsync()).Users).Wrote);
        var consumer = new UserUpdatedConsumer(_host.Object, _h.Sync, _h.Writer, _service, NullLogger.Instance);

        // Its own write a moment ago: the loop guard.
        await consumer.OnEvent(new UserUpdatedEventArgs(_h.Server.Cached(kid.Id)));
        Assert.Empty(_service.PendingRequests);

        // A change outside MVC (another plugin), and a user who isn't managed.
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);
        await consumer.OnEvent(new UserUpdatedEventArgs(_h.Server.Cached(kid.Id)));
        await consumer.OnEvent(new UserUpdatedEventArgs(_h.Server.Cached(plain.Id)));

        Assert.Equal(asks ? 1 : 0, _service.PendingRequests.Count(r => r.Trigger == SyncTrigger.UserUpdated));
        Assert.Equal(asks ? 1 : 0, _service.PendingRequests.Count);
    }

    public void Dispose()
    {
        _service.Dispose();
        _h.Dispose();
    }

    private UserCreatedConsumer Consumer() => new(_admin, _h.Sync, _service, _h.Status, NullLogger.Instance);

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

    private UserPolicyFilter Filter()
        => new(ActionManifest.ForBuild(), _h.Locks, _h.Sync, _service, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Server.Manager.Object, null, _h.Status, NullLogger.Instance);

    private static ActionExecutingContext Context(string action, Dictionary<string, object?> arguments, Guid admin)
    {
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = typeof(Jellyfin.Api.Controllers.UserController).GetTypeInfo(), ActionName = action };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", admin.ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test")) };
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], arguments, new object());
    }
}
