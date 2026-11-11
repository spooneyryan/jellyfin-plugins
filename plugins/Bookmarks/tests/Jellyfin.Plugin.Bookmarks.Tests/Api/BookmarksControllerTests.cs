using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The controller's use of the kit's CurrentUser: Capabilities.IsAdmin comes from IsElevatedAsync, and API
/// keys keep Bookmarks 1.0's 403 message.
/// </summary>
public sealed class BookmarksControllerTests
{
    private readonly User _user = TestData.CreateUser("alice");
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IBookmarkManager> _manager = new();

    public BookmarksControllerTests()
    {
        _users.Setup(m => m.GetUserById(_user.Id)).Returns(_user);
        _manager.Setup(m => m.GetCapabilities(_user, It.IsAny<bool>()))
            .Returns((User _, bool isAdmin) => new BookmarkCapabilitiesDto { ClipsEnabled = true, IsAdmin = isAdmin });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Capabilities_IsAdmin_FollowsRequiresElevation(bool administratorRole, bool expected)
    {
        var controller = CreateController(JellyfinAuth.CreatePrincipal(_user.Id, administratorRole));

        var capabilities = (await controller.GetCapabilities()).Value;

        Assert.NotNull(capabilities);
        Assert.Equal(expected, capabilities.IsAdmin);
    }

    [Fact]
    public async Task Capabilities_IsAdmin_IgnoresTheStoredFlagWhenThePolicyIsVetoed()
    {
        _user.SetPermission(PermissionKind.IsAdministrator, true);
        var controller = CreateController(JellyfinAuth.CreatePrincipal(_user.Id, isAdministrator: true), vetoElevation: true);

        var capabilities = (await controller.GetCapabilities()).Value;

        Assert.False(capabilities!.IsAdmin);
        Assert.True(CurrentUser.IsAdmin(_user));
    }

    [Fact]
    public async Task ApiKeys_Get403WithTheBookmarks10Message()
    {
        var controller = CreateController(JellyfinAuth.CreatePrincipal(Guid.Empty, isAdministrator: false, isApiKey: true));

        var error = await Assert.ThrowsAsync<PluginException>(controller.GetCapabilities);

        Assert.Equal(PluginErrorKind.Forbidden, error.Kind);
        Assert.Equal("Bookmarks require a signed-in user; API keys can't be used.", error.Message);
        Assert.Equal(BookmarksController.NoUserMessage, error.Message);
        _manager.Verify(m => m.GetCapabilities(It.IsAny<User>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void UnknownUsers_Get403WithTheBookmarks10Message()
    {
        var controller = CreateController(JellyfinAuth.CreatePrincipal(Guid.NewGuid(), isAdministrator: false));

        var error = Assert.Throws<PluginException>(() => controller.GetBookmarks(null));

        Assert.Equal(PluginErrorKind.Forbidden, error.Kind);
        Assert.Equal("The signed-in user no longer exists.", error.Message);
    }

    [Fact]
    public void Controller_CarriesTheKitFilters()
    {
        var attributes = typeof(BookmarksController).GetCustomAttributes(inherit: true);

        Assert.Contains(attributes, a => a is KitAbiGateAttribute);
        Assert.Contains(attributes, a => a is PluginExceptionFilterAttribute);
    }

    private BookmarksController CreateController(ClaimsPrincipal principal, bool vetoElevation = false)
    {
        var services = new ServiceCollection().AddLogging().AddJellyfinAuthorization();
        if (vetoElevation)
        {
            services.AddSingleton<IAuthorizationHandler, VetoElevationHandler>();
        }

        return new BookmarksController(_manager.Object, Mock.Of<IBookmarkQuery>(), Mock.Of<IBookmarkPreferences>(), _users.Object, Mock.Of<IEmbeddedScript>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal, RequestServices = services.BuildServiceProvider() }
            }
        };
    }

    /// <summary>Fails RequiresElevation (its role requirement) for everyone.</summary>
    private sealed class VetoElevationHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            if (context.Requirements.OfType<ClaimsAuthorizationRequirement>().Any(r => r.ClaimType == ClaimTypes.Role))
            {
                context.Fail();
            }

            return Task.CompletedTask;
        }
    }
}
