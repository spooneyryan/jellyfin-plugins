using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// <c>GET /AccessRoles/Me</c> over HTTP: the caller's own locks from the filter's snapshot, none for
/// an administrator, every Settings section locked noted, a managed user with no library, 403 for an API key, 401
/// anonymously, never cached by the browser.
/// </summary>
public sealed class MeTests
{
    [Fact]
    public async Task ALockedUser_GetsTheirOwnLocks_AnUnmanagedOneNone_AnAdministratorNone()
    {
        await using var api = await AccessRolesApi.StartAsync();
        var kid = api.AddUser("kid", allFolders: false, folders: [Movies]);
        var open = api.AddUser("open");
        var admin = api.AddUser("boss", admin: true);
        var (_, role) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Kid", Locks = new[] { "Profile", "Home", "UserDirectory" } });
        var roleId = role!["Role"]!["Id"]!.GetValue<string>();
        Assert.Equal(200, (await api.JsonAsync(HttpMethod.Put, $"/AccessRoles/Admin/Users/{kid.Id:N}", body: new { RoleIds = new[] { roleId } })).Status);
        Assert.Equal(200, (await api.JsonAsync(HttpMethod.Put, $"/AccessRoles/Admin/Users/{admin.Id:N}", body: new { RoleIds = new[] { roleId } })).Status);

        using var response = await api.SendAsync(HttpMethod.Get, "/AccessRoles/Me", caller: "user", userId: kid.Id);
        var me = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal((kid.Id, true, false, false), (Guid.Parse(me["UserId"]!.GetValue<string>()), me["Managed"]!.GetValue<bool>(), me["AllSettingsLocked"]!.GetValue<bool>(), me["NoLibraries"]!.GetValue<bool>()));
        Assert.Equal(["Profile", "Home", "UserDirectory"], me["Locked"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Equal(16, me["RulesVersion"]!.GetValue<string>().Length);
        Assert.Null(me["Message"]);

        // The locks the build doesn't enforce yet, the same for every caller; none since 0.9.7.
        Assert.Empty(me["NotEnforced"]!.AsArray());

        var (openStatus, openMe) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Me", caller: "user", userId: open.Id);
        Assert.Equal((200, false), (openStatus, openMe!["Managed"]!.GetValue<bool>()));
        Assert.Empty(openMe["Locked"]!.AsArray());
        Assert.NotEqual(me["RulesVersion"]!.GetValue<string>(), openMe["RulesVersion"]!.GetValue<string>());

        var (adminStatus, adminMe) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Me", caller: "admin", userId: admin.Id);
        Assert.Equal((200, true), (adminStatus, adminMe!["Managed"]!.GetValue<bool>()));
        Assert.Empty(adminMe["Locked"]!.AsArray());
        Assert.Empty(adminMe["NotEnforced"]!.AsArray());

        Assert.Equal(403, (await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Me", caller: "apikey")).Status);
        Assert.Equal(401, (await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Me", caller: null)).Status);
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task EverySettingsSectionLocked_IsNoted_JellyfinEnhancedsOnlyWhenItIsInstalled()
    {
        await using var api = await AccessRolesApi.StartAsync();
        var guest = api.AddUser("guest");
        var (_, role) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Guest", Locks = new[] { "Profile", "DisplayAccount", "DisplayDevice", "Home", "Playback", "Subtitles", "Controls", "QuickConnect" } });
        await api.JsonAsync(HttpMethod.Put, $"/AccessRoles/Admin/Users/{guest.Id:N}", body: new { RoleIds = new[] { role!["Role"]!["Id"]!.GetValue<string>() } });

        // The harness routes no Jellyfin Enhanced controller, so its section doesn't count.
        var (_, me) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Me", caller: "user", userId: guest.Id);

        Assert.True(me!["AllSettingsLocked"]!.GetValue<bool>());
        Assert.Equal(AccessRolesMe.AllSettingsLockedMessage, me["Message"]!.GetValue<string>());
    }

    [Fact]
    public async Task AManagedUserWhoCanOpenNoLibrary_IsToldSo()
    {
        await using var api = await AccessRolesApi.StartAsync();
        var gone = Library(9, "Gone");
        var kid = api.AddUser("kid", allFolders: false, folders: [gone]);
        var (_, role) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Lock", Locks = new[] { "Profile" } });
        await api.JsonAsync(HttpMethod.Put, $"/AccessRoles/Admin/Users/{kid.Id:N}", body: new { RoleIds = new[] { role!["Role"]!["Id"]!.GetValue<string>() } });

        var (_, me) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Me", caller: "user", userId: kid.Id);

        Assert.True(me!["NoLibraries"]!.GetValue<bool>());
        Assert.Equal(AccessRolesMe.NoLibrariesMessage, me["Message"]!.GetValue<string>());
    }
}
