using System;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.TestSupport;

/// <summary>
/// The controller-name test and the route check.
/// </summary>
public sealed class ControllerNameChecksTests
{
    [Fact]
    public void ReservedNames_HoldCoreAndJellyfinEnhancedKeys()
    {
        var names = ControllerNameChecks.ReservedNames;

        // Every name found in JE 12.8's filter tables, plus the whole 10.11.5 controller list.
        foreach (var name in new[] { "Items", "UserLibrary", "TvShows", "Suggestions", "Search", "Library", "Image", "Trickplay", "MediaInfo", "Movies", "Playlists", "HomeScreen" })
        {
            Assert.Contains(name, names);
        }

        Assert.Contains("user", names);
        Assert.Equal(62, names.Count);
    }

    [Fact]
    public void CheckNames_AcceptsPrefixedControllers()
    {
        Assert.Empty(ControllerNameChecks.CheckNames([typeof(FxController), typeof(FxAdminController)], "Fx", ControllerNameChecks.ReservedNames));
    }

    [Fact]
    public void CheckNames_RejectsACoreNameEvenWhenItIsTheRoutePrefix()
    {
        // A plugin whose route were "Items" would still collide with core's ItemsController.
        var problems = ControllerNameChecks.CheckNames([typeof(ItemsController)], "Items", ControllerNameChecks.ReservedNames);

        var problem = Assert.Single(problems);
        Assert.Contains("'Items'", problem, StringComparison.Ordinal);
        Assert.Contains("filter and strip its responses", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckNames_RejectsAControllerOutsideThePrefix_AndCaseVariantsOfReservedNames()
    {
        var problems = ControllerNameChecks.CheckNames([typeof(userlibraryController)], "Fx", ControllerNameChecks.ReservedNames);

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void CheckRoutes_AcceptsRoutesUnderThePrefix()
    {
        Assert.Empty(ControllerNameChecks.CheckRoutes([typeof(FxController), typeof(FxAdminController)], "Fx"));
    }

    [Fact]
    public void CheckRoutes_RejectsAbsoluteRoutesElsewhereAndMissingRoutes()
    {
        var problems = ControllerNameChecks.CheckRoutes([typeof(FxStrayController)], "Fx");

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("'/Items/Shared'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'/FxOther/Thing'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("no attribute route", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_FollowsMvc()
    {
        Assert.True(ControllerDiscovery.IsController(typeof(Fixtures.FxGoodController)));
        Assert.False(ControllerDiscovery.IsController(typeof(FxController)));
        Assert.False(ControllerDiscovery.IsController(typeof(Fixtures.FxItemDto)));
        Assert.Equal("FxGood", ControllerDiscovery.GetControllerName(typeof(Fixtures.FxGoodController)));
        Assert.Equal(["Ping"], ControllerDiscovery.FindActions(typeof(FxController)).Select(m => m.Name));
    }

    [ApiController]
    [Authorize]
    [Route("Fx")]
    public sealed class FxController : ControllerBase
    {
        [HttpGet("Ping")]
        public ActionResult<string> Ping() => "pong";

        [NonAction]
        public string Helper() => "not an action";
    }

    [ApiController]
    [Authorize]
    [Route("Fx/Admin")]
    public sealed class FxAdminController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> Get() => "admin";
    }

    [ApiController]
    [Authorize]
    [Route("Items")]
    public sealed class ItemsController : ControllerBase
    {
        [HttpGet("Mine")]
        public ActionResult<string> Mine() => "mine";
    }

    [ApiController]
    [Authorize]
    [Route("Fx/Library")]
    public sealed class userlibraryController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> Get() => "x";
    }

    [ApiController]
    [Authorize]
    public sealed class FxStrayController : ControllerBase
    {
        [HttpGet("/Items/Shared")]
        public ActionResult<string> Shared() => "shared";

        [HttpGet("~/FxOther/Thing")]
        public ActionResult<string> Other() => "other";

        public ActionResult<string> NoRoute() => "none";
    }
}
