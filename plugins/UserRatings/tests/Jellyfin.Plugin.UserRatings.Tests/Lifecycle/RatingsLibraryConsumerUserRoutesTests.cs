using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The user routes: <i>Remap by name</i> and <i>Apply deleted-user policy</i> through the admin
/// controller, over the kit's reconciler with the ratings store as its owner: admins and API keys only, a dry run by
/// default, the snapshot before each change, and read-only data refused with 409.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    private const string RemapPath = "/UserRatings/Admin/Users/Remap";
    private const string ApplyPolicyPath = "/UserRatings/Admin/Users/ApplyPolicy";

    [Fact]
    public async Task TheUserRoutes_RemapByName_AndApplyThePolicy_ForAnAdminOrAnApiKeyOnly_EachAfterASnapshot()
    {
        var heat = AddMovie("Heat (1995)", _movies, "949");
        var ronin = AddMovie("Ronin (1998)", _movies, "8195");

        // Restored from another server: alice's ratings under her old id; carol was deleted while the plugin was off.
        var oldAlice = ServicesFixture.NewUser("alice");
        var carol = ServicesFixture.NewUser("carol");
        var alices = await RateAsync(heat, oldAlice, 9, "A great heist.");
        var carols = await RateAsync(ronin, carol, 3, "Not for me.");
        await _fx.Store.WriteAsync(
            w =>
            {
                RatingsUserDataOwner.RecordName(w, oldAlice.Id, "alice");
                RatingsUserDataOwner.RecordName(w, carol.Id, "carol");
                return 0;
            },
            Ct);
        var owner = new RatingsUserDataOwner(_fx.Store, _logger, _fx.Clock, () => _consumer);
        var reconciler = Reconciler(owner);
        await using var host = await StartAdminHostAsync(new UserRatingsAdminApi(_consumer, () => reconciler, () => owner, hasAllLibraries: _ => true));

        // Only an admin or an API key; nothing changes before.
        foreach (var (path, json) in new[] { (RemapPath + "?dryRun=false", (string?)null), (ApplyPolicyPath, UserIds(carol.Id)) })
        {
            Assert.Equal(401, await AdminStatusAsync(host, path, null, json));
            Assert.Equal(403, await AdminStatusAsync(host, path, "user", json));
        }

        Assert.Equal(oldAlice.Id, _fx.Store.Get(alices.Id)!.UserId);
        Assert.NotNull(_fx.Store.Get(carols.Id));

        // Remap by name: only by name; a dry run by default, which only reports.
        using (var byEmail = await PostAdminAsync(host, RemapPath + "?by=Email", "admin", null))
        {
            Assert.Equal(400, (int)byEmail.StatusCode);
            Assert.Contains(UserRatingsAdminApi.ByMessage, await byEmail.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        var dry = await AdminJsonAsync(host, RemapPath, "apikey", null);
        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        var mapped = Assert.Single(dry.GetProperty("Mapped").EnumerateArray());
        Assert.Equal((KnownUsers.FormatId(oldAlice.Id), KnownUsers.FormatId(_alice.Id), "alice"), (mapped.GetProperty("OldId").GetString(), mapped.GetProperty("NewId").GetString(), mapped.GetProperty("Name").GetString()));
        var unmatched = Assert.Single(dry.GetProperty("Unmatched").EnumerateArray());
        Assert.Equal((KnownUsers.FormatId(carol.Id), UserRemapUnmatched.NoUserWithThisName), (unmatched.GetProperty("OldId").GetString(), unmatched.GetProperty("Reason").GetString()));
        Assert.Equal((0, 0, 0), (dry.GetProperty("MovedRecords").GetInt32(), dry.GetProperty("Ambiguous").GetInt32(), dry.GetProperty("ArchivedRecords").GetInt32()));
        var plan = dry.GetProperty("PlanToken").GetString()!;
        Assert.Equal(oldAlice.Id, _fx.Store.Get(alices.Id)!.UserId);
        Assert.False(Directory.Exists(Path.Combine(_fx.Snapshots, "pre-op")));

        // The real run is bound to the dry run: without its token a 400, with another a 409;
        // nothing is written either way.
        using (var noPlan = await PostAdminAsync(host, RemapPath + "?by=Name&dryRun=false", "admin", null))
        {
            Assert.Equal(400, (int)noPlan.StatusCode);
            Assert.Contains(UserReconciler.PlanRequiredMessage, await noPlan.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var otherPlan = await PostAdminAsync(host, RemapPath + "?by=Name&dryRun=false&ifPlan=p1-00000000000000000000", "admin", null))
        {
            Assert.Equal(409, (int)otherPlan.StatusCode);
            Assert.Contains(UserReconciler.PlanChangedMessage, await otherPlan.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal(oldAlice.Id, _fx.Store.Get(alices.Id)!.UserId);
        Assert.False(Directory.Exists(Path.Combine(_fx.Snapshots, "pre-op")));

        // For real: alice gets her rating back, after a snapshot, with a UserRemapped row (ids and counts only).
        var remap = await AdminJsonAsync(host, RemapPath + "?by=Name&dryRun=false&ifPlan=" + plan, "admin", null);
        Assert.Equal((1, 0, plan), (remap.GetProperty("MovedRecords").GetInt32(), remap.GetProperty("Ambiguous").GetInt32(), remap.GetProperty("PlanToken").GetString()));
        var remapped = Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.UserRemapped);
        Assert.Equal(((Guid?)oldAlice.Id, $"{{\"NewUserId\":\"{_alice.Id:N}\",\"Ratings\":1,\"Archived\":0,\"Ambiguous\":0}}"), (remapped.TargetUserId, remapped.Details));
        Assert.Equal((_alice.Id, true), (_fx.Store.Get(alices.Id)!.UserId, _fx.Store.Get(alices.Id)!.IsAttached));
        var remapSnapshot = Assert.Single(remap.GetProperty("SafetySnapshots").EnumerateArray()).GetString()!;
        Assert.StartsWith("userratings-" + SnapshotOperations.Remap + "-", remapSnapshot, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fx.Snapshots, "pre-op", remapSnapshot)));

        // Apply deleted-user policy: the list is required, 1 to 1000 ids, none of them a user of this server.
        Assert.Equal(415, await AdminStatusAsync(host, ApplyPolicyPath, "admin", null));
        Assert.Equal(400, await AdminStatusAsync(host, ApplyPolicyPath, "admin", "null"));
        using (var noList = await PostAdminAsync(host, ApplyPolicyPath, "admin", "{}"))
        {
            Assert.Equal(400, (int)noList.StatusCode);
            Assert.Contains(UserRatingsAdminApi.UserIdsMessage, await noList.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal(400, await AdminStatusAsync(host, ApplyPolicyPath, "admin", UserIds()));
        Assert.Equal(400, await AdminStatusAsync(host, ApplyPolicyPath, "admin", UserIds(Guid.Empty)));
        Assert.Equal(400, await AdminStatusAsync(host, ApplyPolicyPath, "admin", UserIds(carol.Id, _bob.Id)));
        Assert.Equal(400, await AdminStatusAsync(host, ApplyPolicyPath, "admin", UserIds([.. Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid())])));
        Assert.NotNull(_fx.Store.Get(carols.Id));

        // carol's ratings get the policy (Delete): tombstones, the moderation log, a snapshot first.
        var applied = await AdminJsonAsync(host, ApplyPolicyPath, "admin", UserIds(carol.Id));
        Assert.Equal((nameof(DeletedUserPolicy.Delete), 1, 0), (applied.GetProperty("Policy").GetString(), applied.GetProperty("Affected").GetInt32(), applied.GetProperty("Archived").GetInt32()));
        Assert.Null(_fx.Store.Get(carols.Id));
        Assert.Equal((carol.Id, RatingValues.DeletedBy.UserDeleted), (_fx.Store.GetTombstone(carols.Id)!.UserId, _fx.Store.GetTombstone(carols.Id)!.DeletedBy));
        Assert.Equal((Guid?)carol.Id, Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.UserDeleted).TargetUserId);
        var policySnapshot = Assert.Single(applied.GetProperty("SafetySnapshots").EnumerateArray()).GetString()!;
        Assert.StartsWith("userratings-" + SnapshotOperations.ApplyPolicy + "-", policySnapshot, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fx.Snapshots, "pre-op", policySnapshot)));
        Assert.Equal((_alice.Id, 9), (_fx.Store.Get(alices.Id)!.UserId, _fx.Store.Get(alices.Id)!.Stars2));
    }

    [Fact]
    public async Task TheUnknownUsersTools_AreForAnAdminWithEveryLibrary_OrAnApiKey_AndDiagnosticsHideTheListFromOthers()
    {
        // Before 0.3.19, as an admin without every library, the Remap dry run answered 200 with a
        // PlanToken, ApplyPolicy reached its body check, and Diagnostics listed the unknown users and their last names.
        var carol = ServicesFixture.NewUser("carol");
        var carols = await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), carol, 3, "Not for me.");
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, carol.Id, "carol"); return 0; }, Ct);
        var owner = new RatingsUserDataOwner(_fx.Store, _logger, _fx.Clock, () => _consumer);
        var reconciler = Reconciler(owner);
        Guid restricted = Guid.NewGuid(), everyLibrary = Guid.NewGuid();
        var diagnostics = new Mock<IKitDiagnostics>();
        diagnostics.Setup(d => d.Describe()).Returns(() => new Dictionary<string, object?>(StringComparer.Ordinal) { ["PluginVersion"] = "0.3.19.0", [KitUsers.UsersProperty] = reconciler.Describe() });
        await using var host = await StartAdminHostAsync(new UserRatingsAdminApi(_consumer, () => reconciler, () => owner, hasAllLibraries: id => id == everyLibrary), diagnostics.Object);
        var generation = _fx.Store.Generation;

        // 403 AllLibrariesNeeded before anything else: no plan, no body check, nothing written.
        foreach (var (path, json) in new[] { (RemapPath, (string?)null), (RemapPath + "?by=Email", null), (RemapPath + "?dryRun=false&ifPlan=p1-00000000000000000000", null), (ApplyPolicyPath, UserIds(carol.Id)), (ApplyPolicyPath, UserIds()) })
        {
            using var refused = await PostAdminAsync(host, path, "admin", json, restricted);
            var body = await refused.Content.ReadAsStringAsync(Ct);
            Assert.Equal(403, (int)refused.StatusCode);
            Assert.Contains(RatingsData.AllLibrariesCode, body, StringComparison.Ordinal);
            Assert.DoesNotContain("PlanToken", body, StringComparison.Ordinal);

            // 1.0.1: the detail says how to get every library (the stock Access tab's box, or the Access Roles role).
            using var problem = JsonDocument.Parse(body);
            var detail = problem.RootElement.GetProperty("detail").GetString();
            Assert.EndsWith(RatingsData.AllLibrariesFix, detail, StringComparison.Ordinal);
            Assert.Contains("tick \"Enable access to all libraries\", then Save", detail, StringComparison.Ordinal);
        }

        Assert.Equal(generation, _fx.Store.Generation);
        Assert.NotNull(_fx.Store.Get(carols.Id));
        Assert.False(Directory.Exists(Path.Combine(_fx.Snapshots, "pre-op")));

        // Diagnostics: the list empty for them (the counts stay); whole for an admin with every library and an API key.
        var hidden = await DiagnosticsAsync(host, "admin", restricted);
        Assert.Equal(0, hidden.GetProperty("Users").GetProperty("OrphanUsers").GetArrayLength());
        Assert.Equal(RatingsData.AllLibrariesCode, hidden.GetProperty("Users").GetProperty("OrphanUsersHidden").GetString());
        Assert.Equal(1, hidden.GetProperty("Users").GetProperty("OrphanRecords").GetInt32());
        Assert.DoesNotContain("carol", hidden.GetRawText(), StringComparison.Ordinal);
        foreach (var (caller, id) in new[] { ("admin", everyLibrary), ("apikey", Guid.Empty) })
        {
            var users = (await DiagnosticsAsync(host, caller, id)).GetProperty("Users");
            Assert.Equal("carol", Assert.Single(users.GetProperty("OrphanUsers").EnumerateArray()).GetProperty("LastKnownName").GetString());
            Assert.False(users.TryGetProperty("OrphanUsersHidden", out _));
        }

        // They may use the tools.
        using (var allowed = await PostAdminAsync(host, RemapPath, "admin", null, everyLibrary))
        {
            Assert.Equal(200, (int)allowed.StatusCode);
        }

        var applied = await AdminJsonAsync(host, ApplyPolicyPath, "apikey", UserIds(carol.Id));
        Assert.Equal(1, applied.GetProperty("Affected").GetInt32());
        Assert.Null(_fx.Store.Get(carols.Id));
    }

    [Fact]
    public async Task TheUserRoutes_OnReadOnlyData_Are409_ExceptARemapDryRun()
    {
        var gone = Guid.NewGuid();
        // Strict: a write or a snapshot would fail the request with 500, not 409.
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(true);
        store.SetupGet(s => s.Generation).Returns(1);
        store.Setup(s => s.CountByUser()).Returns(new Dictionary<Guid, int> { [gone] = 2 });
        store.Setup(s => s.GetMeta(RatingsUserDataOwner.KnownUsersKey)).Returns((string?)null);
        var owner = new RatingsUserDataOwner(store.Object, _logger, _fx.Clock);
        var reconciler = Reconciler(owner);
        await using var host = await StartAdminHostAsync(new UserRatingsAdminApi(_consumer, () => reconciler, () => owner, hasAllLibraries: _ => true));

        Assert.Equal(200, await AdminStatusAsync(host, RemapPath, "admin", null));
        Assert.Equal(409, await AdminStatusAsync(host, RemapPath + "?dryRun=false&ifPlan=p1-00000000000000000000", "admin", null));
        Assert.Equal(409, await AdminStatusAsync(host, ApplyPolicyPath, "admin", UserIds(gone)));
    }

    // With the server's user list empty (or failing) ApplyPolicy can't tell a current user from a deleted one: 503,
    // nothing written, no snapshot.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPolicy_WithTheServersUserListUnavailable_Is503_AndWritesNothing(bool throws)
    {
        var carol = ServicesFixture.NewUser("carol");
        var carols = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), carol, 7);
        var owner = new RatingsUserDataOwner(_fx.Store, _logger, _fx.Clock, () => _consumer);
        var reconciler = new UserReconciler(
            () => throws ? throw new InvalidOperationException("The user store is unavailable.") : new Dictionary<Guid, string>(),
            [owner],
            new KitUserMarks(_fx.Directory.Combine("userratings", "kit-users.json"), _logger, _fx.Clock),
            () => DeletedUserPolicy.Delete,
            _logger,
            _fx.Clock);
        _disposables.Add(reconciler);
        await using var host = await StartAdminHostAsync(new UserRatingsAdminApi(_consumer, () => reconciler, () => owner, hasAllLibraries: _ => true));
        var generation = _fx.Store.Generation;

        using var response = await PostAdminAsync(host, ApplyPolicyPath, "admin", UserIds(carol.Id));

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Contains(UserReconciler.UserListUnavailableMessage, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(generation, _fx.Store.Generation);
        Assert.NotNull(_fx.Store.Get(carols.Id));
        Assert.False(Directory.Exists(Path.Combine(_fx.Snapshots, "pre-op")));
    }

    private static string UserIds(params Guid[] ids) => JsonSerializer.Serialize(new { UserIds = ids });

    private static Task<HttpResponseMessage> PostAdminAsync(WebHostHarness host, string path, string? caller, string? json, Guid? userId = null)
        => host.SendAsync(HttpMethod.Post, path, request =>
        {
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
                request.Headers.Add(JellyfinAuth.UserIdHeader, (userId ?? (caller == "apikey" ? Guid.Empty : Guid.NewGuid())).ToString("N"));
            }

            if (json is not null)
            {
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }
        });

    private static async Task<JsonElement> DiagnosticsAsync(WebHostHarness host, string caller, Guid userId)
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/UserRatings/Admin/Diagnostics", request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, caller);
            request.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString("N"));
        });
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<int> AdminStatusAsync(WebHostHarness host, string path, string? caller, string? json)
    {
        using var response = await PostAdminAsync(host, path, caller, json);
        return (int)response.StatusCode;
    }

    private static async Task<JsonElement> AdminJsonAsync(WebHostHarness host, string path, string caller, string? json)
    {
        using var response = await PostAdminAsync(host, path, caller, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private UserReconciler Reconciler(RatingsUserDataOwner owner)
    {
        var reconciler = new UserReconciler(
            () => new Dictionary<Guid, string> { [_alice.Id] = _alice.Username, [_bob.Id] = _bob.Username },
            [owner],
            new KitUserMarks(_fx.Directory.Combine("userratings", "kit-users.json"), _logger, _fx.Clock),
            () => DeletedUserPolicy.Delete,
            _logger,
            _fx.Clock);
        _disposables.Add(reconciler);
        return reconciler;
    }
}
