using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Tests.Fixtures;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Api;

/// <summary>
/// CurrentUser: claim parsing, API keys refused, literals pinned, and IsElevatedAsync
/// following RequiresElevation exactly, including a vetoing handler, while IsAdmin still reads the stored flag.
/// </summary>
public sealed class CurrentUserTests
{
    private static readonly Guid AliceId = new("0f8e0a52-43c4-4b8f-9b7e-6a3f2c1d0e9a");

    [Fact]
    public void ClaimLiterals_AreJellyfins()
    {
        // v10.11.5 Jellyfin.Api/Constants/InternalClaimTypes.cs:11 and :41; plugins can't reference Jellyfin.Api.
        Assert.Equal("Jellyfin-UserId", CurrentUser.UserIdClaimType);
        Assert.Equal("Jellyfin-IsApiKey", CurrentUser.IsApiKeyClaimType);
    }

    [Fact]
    public void GetUserId_ReadsTheClaim()
    {
        Assert.Equal(AliceId, JellyfinAuth.CreatePrincipal(AliceId, isAdministrator: false).GetUserId());
        Assert.Equal(Guid.Empty, JellyfinAuth.CreatePrincipal(AliceId, isAdministrator: false, isApiKey: true).GetUserId());
        Assert.Equal(Guid.Empty, new ClaimsPrincipal(new ClaimsIdentity()).GetUserId());
        Assert.Equal(Guid.Empty, Principal(new Claim(CurrentUser.UserIdClaimType, "not-a-guid")).GetUserId());

        // Jellyfin matches claim types case-insensitively.
        Assert.Equal(AliceId, Principal(new Claim("JELLYFIN-USERID", AliceId.ToString("N"))).GetUserId());
    }

    [Fact]
    public void IsApiKey_ReadsTheClaim()
    {
        Assert.True(JellyfinAuth.CreatePrincipal(Guid.Empty, isAdministrator: false, isApiKey: true).IsApiKey());
        Assert.False(JellyfinAuth.CreatePrincipal(AliceId, isAdministrator: true).IsApiKey());
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity()).IsApiKey());
        Assert.False(Principal(new Claim(CurrentUser.IsApiKeyClaimType, "yes")).IsApiKey());
    }

    [Fact]
    public void RequireUser_ReturnsTheSignedInUser()
    {
        var alice = CreateUser("alice");
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(AliceId)).Returns(alice);

        Assert.Same(alice, JellyfinAuth.CreatePrincipal(AliceId, isAdministrator: false).RequireUser(users.Object));
    }

    [Fact]
    public void RequireUser_RefusesApiKeysAndUnknownUsersWith403()
    {
        var users = new Mock<IUserManager>();

        var apiKey = Assert.Throws<PluginException>(
            () => JellyfinAuth.CreatePrincipal(Guid.Empty, isAdministrator: false, isApiKey: true).RequireUser(users.Object));
        var anonymous = Assert.Throws<PluginException>(() => new ClaimsPrincipal(new ClaimsIdentity()).RequireUser(users.Object));
        var unknown = Assert.Throws<PluginException>(() => JellyfinAuth.CreatePrincipal(AliceId, isAdministrator: false).RequireUser(users.Object));
        var custom = Assert.Throws<PluginException>(
            () => JellyfinAuth.CreatePrincipal(Guid.Empty, isAdministrator: true, isApiKey: true).RequireUser(users.Object, "Bookmarks need a user."));

        Assert.All([apiKey, anonymous, unknown, custom], ex => Assert.Equal(PluginErrorKind.Forbidden, ex.Kind));
        Assert.Equal(CurrentUser.NoUserMessage, apiKey.Message);
        Assert.Equal(CurrentUser.NoUserMessage, anonymous.Message);
        Assert.Equal(CurrentUser.UnknownUserMessage, unknown.Message);
        Assert.Equal("Bookmarks need a user.", custom.Message);
        users.Verify(m => m.GetUserById(Guid.Empty), Times.Never);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public async Task IsElevatedAsync_FollowsTheRoleClaim(bool isAdministrator, bool isApiKey, bool expected)
    {
        var context = CreateContext(JellyfinAuth.CreatePrincipal(AliceId, isAdministrator, isApiKey));

        Assert.Equal(expected, await CurrentUser.IsElevatedAsync(context));
    }

    [Fact]
    public async Task IsElevatedAsync_IsFalseForAnonymousRequests()
    {
        Assert.False(await CurrentUser.IsElevatedAsync(CreateContext(new ClaimsPrincipal(new ClaimsIdentity()))));
    }

    [Fact]
    public async Task IsElevatedAsync_HonoursAVetoingHandler_WhileIsAdminStillReadsTrue()
    {
        // No planned plugin registers such a handler; this pins the kit's rule that the claim, not the stored flag,
        // decides: the stored flag would still say admin.
        var admin = CreateUser("root");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        var context = CreateContext(JellyfinAuth.CreatePrincipal(AliceId, isAdministrator: true), veto: true);
        context.Request.Headers[FxVetoHandler.Header] = "1";

        Assert.False(await CurrentUser.IsElevatedAsync(context));
        Assert.True(CurrentUser.IsAdmin(admin));
    }

    [Fact]
    public async Task IsElevatedAsync_IsFalseForADemotedAdmin_WhoseStoredFlagIsStale()
    {
        // Core recomputes the role claim from the live user on every request (CustomAuthenticationHandler.cs:53-58), so a
        // user demoted since their entity was loaded arrives with the User role.
        var stale = CreateUser("demoted");
        stale.SetPermission(PermissionKind.IsAdministrator, true);

        Assert.False(await CurrentUser.IsElevatedAsync(CreateContext(JellyfinAuth.CreatePrincipal(AliceId, isAdministrator: false))));
        Assert.True(CurrentUser.IsAdmin(stale));
    }

    [Theory]
    [InlineData("admin", false)]
    [InlineData("user", false)]
    [InlineData("apikey", false)]
    [InlineData("admin", true)]
    public async Task IsElevatedAsync_AgreesWithARequiresElevationEndpoint(string caller, bool veto)
    {
        await using var host = await MvcTestHost.StartAsync(
            [typeof(FxElevationController)],
            services => services.AddSingleton<IAuthorizationHandler, FxVetoHandler>());

        using var check = host.Request(HttpMethod.Get, "/Fx/Elevation/Check", caller);
        using var admin = host.Request(HttpMethod.Get, "/Fx/Elevation/Admin", caller);
        if (veto)
        {
            check.Headers.Add(FxVetoHandler.Header, "1");
            admin.Headers.Add(FxVetoHandler.Header, "1");
        }

        using var checkResponse = await host.Client.SendAsync(check, TestContext.Current.CancellationToken);
        using var adminResponse = await host.Client.SendAsync(admin, TestContext.Current.CancellationToken);
        var elevated = bool.Parse(await checkResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, checkResponse.StatusCode);
        Assert.Equal(elevated ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, adminResponse.StatusCode);
        Assert.Equal(caller is "admin" or "apikey" && !veto, elevated);
    }

    [Fact]
    public void IsAdmin_ReadsTheStoredFlag()
    {
        var user = CreateUser("bob");
        Assert.False(CurrentUser.IsAdmin(user));

        user.SetPermission(PermissionKind.IsAdministrator, true);
        Assert.True(CurrentUser.IsAdmin(user));
    }

    [Fact]
    public void HasAny_IsTrueWhenOneKindIsSet()
    {
        var user = CreateUser("carol");
        user.SetPermission(PermissionKind.EnableContentDownloading, false);
        user.SetPermission(PermissionKind.EnableCollectionManagement, true);

        Assert.True(CurrentUser.HasAny(user, PermissionKind.EnableContentDownloading, PermissionKind.EnableCollectionManagement));
        Assert.False(CurrentUser.HasAny(user, PermissionKind.EnableContentDownloading));
        Assert.False(CurrentUser.HasAny(user));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, JellyfinAuth.Scheme));

    private static User CreateUser(string name)
    {
        var user = new User(name, "DefaultAuthenticationProvider", "DefaultPasswordResetProvider");
        user.AddDefaultPermissions();
        return user;
    }

    private static DefaultHttpContext CreateContext(ClaimsPrincipal principal, bool veto = false)
    {
        var services = new ServiceCollection().AddLogging().AddJellyfinAuthorization();
        if (veto)
        {
            services.AddSingleton<IAuthorizationHandler, FxVetoHandler>();
        }

        return new DefaultHttpContext { User = principal, RequestServices = services.BuildServiceProvider() };
    }
}
