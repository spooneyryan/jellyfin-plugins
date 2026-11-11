using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// <c>GET /SmartSearch/Fields</c>: the registry for users, with <c>Path</c> only for callers that pass
/// <c>RequiresElevation</c> (decided by the policy, never the stored flag), and 403 for API keys.
/// </summary>
public sealed class FieldsEndpointTests
{
    private readonly User _user = new("alice", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider");
    private readonly Mock<IUserManager> _users = new();

    public FieldsEndpointTests()
    {
        _users.Setup(u => u.GetUserById(_user.Id)).Returns(_user);
    }

    [Fact]
    public async Task Users_GetEveryFieldButPath()
    {
        var catalog = (await CreateController(JellyfinAuth.CreatePrincipal(_user.Id, isAdministrator: false)).GetSmartSearchFields()).Value!;

        Assert.Equal(FieldRegistry.Version, catalog.RegistryVersion);
        Assert.DoesNotContain(catalog.Fields, f => f.Id == "Path");
        Assert.Equal(new FieldRegistry().Fields.Count - 1, catalog.Fields.Count);
        Assert.Equal(FieldRegistry.SortKeyList, catalog.SortKeys);
        Assert.Equal(QueryLimits.ItemKindOrder, catalog.ItemKinds);
    }

    [Fact]
    public async Task Administrators_AlsoGetPath()
    {
        var catalog = (await CreateController(JellyfinAuth.CreatePrincipal(_user.Id, isAdministrator: true)).GetSmartSearchFields()).Value!;

        var path = Assert.Single(catalog.Fields, f => f.Id == "Path");
        Assert.True(path.AdminOnly);
        Assert.Equal(["Contains", "NotContains", "StartsWith"], path.Operators); // Matches is not offered yet (QueryOperators.Dormant)
    }

    [Fact]
    public async Task TheCatalogue_SaysWhetherSharingIsAllowed()
    {
        Assert.True((await CreateController(JellyfinAuth.CreatePrincipal(_user.Id, isAdministrator: false)).GetSmartSearchFields()).Value!.AllowQuerySharing);

        var sharing = false;
        var fields = new SmartSearchFields(new FieldRegistry(), allowSharing: () => sharing);
        Assert.False(fields.GetCatalog(isAdmin: false).AllowQuerySharing);
        sharing = true;
        Assert.True(fields.GetCatalog(isAdmin: false).AllowQuerySharing);
    }

    [Fact]
    public async Task AnAdministratorFlag_ThatFailsRequiresElevation_GetsNoPath()
    {
        var catalog = (await CreateController(JellyfinAuth.CreatePrincipal(_user.Id, isAdministrator: true), vetoElevation: true).GetSmartSearchFields()).Value!;

        Assert.DoesNotContain(catalog.Fields, f => f.Id == "Path");
    }

    [Fact]
    public async Task ApiKeys_AreRefused()
    {
        var controller = CreateController(JellyfinAuth.CreatePrincipal(Guid.Empty, isAdministrator: true, isApiKey: true));

        var error = await Assert.ThrowsAsync<PluginException>(controller.GetSmartSearchFields);
        Assert.Equal(PluginErrorKind.Forbidden, error.Kind);
    }

    [Fact]
    public async Task TheDtos_CarryWhatTheRuleBuilderNeeds()
    {
        var catalog = (await CreateController(JellyfinAuth.CreatePrincipal(_user.Id, isAdministrator: false)).GetSmartSearchFields()).Value!;

        var year = catalog.Fields.Single(f => f.Id == "Year");
        Assert.Equal(("Number", "Integer", 1800d, 2100d, true, 200d), (year.Type, year.ElementKind, year.Min, year.Max, year.Integer, year.MaxSpan));
        Assert.Contains("AnyOf", year.Operators);
        var provider = Assert.Single(catalog.Fields.Single(f => f.Id == "HasProviderId").Options);
        Assert.Equal(("Provider", true), (provider.Name, provider.Required));
        Assert.Equal(["Tmdb", "Imdb", "Tvdb"], provider.Choices);
        var played = catalog.Fields.Single(f => f.Id == "Played");
        Assert.Equal(("Bool", "User", true, true), (played.Type, played.Cost, played.PerUser, played.Available));
        Assert.Equal("/SmartSearch/Series", catalog.Fields.Single(f => f.Id == "Series").ValueSource);
        Assert.Equal(["Continuing", "Ended", "Unreleased"], catalog.Fields.Single(f => f.Id == "SeriesStatus").Choices);
    }

    [Fact]
    public void TheController_KeepsTheKitsAttributes()
    {
        var attributes = typeof(SmartSearchController).GetCustomAttributes(inherit: true);
        Assert.Contains(attributes, a => a is KitAbiGateAttribute);
        Assert.Contains(attributes, a => a is PluginExceptionFilterAttribute);
        Assert.Contains(attributes, a => a is AuthorizeAttribute);
    }

    private SmartSearchController CreateController(ClaimsPrincipal principal, bool vetoElevation = false)
    {
        var services = new ServiceCollection().AddLogging().AddJellyfinAuthorization();
        if (vetoElevation)
        {
            services.AddSingleton<IAuthorizationHandler, VetoElevationHandler>();
        }

        return new SmartSearchController(Mock.Of<IEmbeddedScript>(), new SmartSearchFields(new FieldRegistry()), Mock.Of<ISmartSearchService>(), _users.Object)
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
