using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.Fixtures;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Api;

/// <summary>
/// [KitAbiGate] as an action constraint, in Jellyfin's pipeline (endpoint routing, attribute-routed controllers): two
/// loaded copies of a plugin have controllers with the same routes; the copy that stands down as a duplicate drops out
/// of route selection, so the other copy answers. Without that, the request is ambiguous.
/// </summary>
public sealed class KitAbiGateRoutingTests
{
    [Fact]
    public async Task DuplicateCopy_DropsOutOfRouting_AndTheOtherCopyAnswers()
    {
        var counter = new FxConstructionCounter();
        await using var host = await StartAsync(counter, Status(active: false).AsDuplicate("Stands down."), typeof(FxGateController), typeof(OtherCopyController));

        using var response = await host.SendAsync(HttpMethod.Get, "/Fx/Gate/ClientScript");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("other copy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task DuplicateCopy_Alone_IsNotFound()
    {
        var counter = new FxConstructionCounter();
        await using var host = await StartAsync(counter, Status(active: false).AsDuplicate("Stands down."), typeof(FxGateController));

        using var response = await host.SendAsync(HttpMethod.Get, "/Fx/Gate/ClientScript");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task InertBuild_StillAnswers503()
    {
        // The wrong-line inert mode is unchanged: the constraint accepts, the filter answers.
        await using var host = await StartAsync(new FxConstructionCounter(), Status(active: false), typeof(FxGateController));

        using var response = await host.SendAsync(HttpMethod.Get, "/Fx/Gate/ClientScript");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ActiveCopy_IsPreferredOverACopyWithoutTheConstraint()
    {
        // ASP.NET Core prefers the candidate whose constraints accept over one without constraints.
        var counter = new FxConstructionCounter();
        await using var host = await StartAsync(counter, Status(active: true), typeof(FxGateController), typeof(OtherCopyController));

        using var response = await host.SendAsync(HttpMethod.Get, "/Fx/Gate/ClientScript");

        Assert.Equal("script", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task TwoCopiesWithoutTheConstraint_AreAmbiguous()
    {
        // What two copies built before the constraint do: both endpoints match, and routing fails the request.
        await using var host = await StartAsync(new FxConstructionCounter(), Status(active: true), typeof(OtherCopyController), typeof(ThirdCopyController));

        var outcome = await Record.ExceptionAsync(async () =>
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/Fx/Gate/ClientScript");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        });

        Assert.True(outcome is null || outcome.ToString().Contains("AmbiguousMatchException", StringComparison.Ordinal), outcome?.ToString());
    }

    private static KitAbiStatus Status(bool active) => new("Kit Test", "10.11", "10.11.5", active, active ? "Active." : "Inactive.");

    private static Task<WebHostHarness> StartAsync(FxConstructionCounter counter, KitAbiStatus status, params Type[] controllers)
        => new WebHostHarness()
            .WithServices(services => services.AddSingleton(counter).AddSingleton(status))
            .WithMvc(controllers)
            .StartAsync();

    /// <summary>The other copy's controller: the same route, built without the duplicate guard.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx/Gate")]
    public sealed class OtherCopyController : ControllerBase
    {
        [HttpGet("ClientScript")]
        [AllowAnonymous]
        public ActionResult<string> ClientScript() => "other copy";
    }

    /// <summary>A third copy's controller, also without the constraint.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx/Gate")]
    public sealed class ThirdCopyController : ControllerBase
    {
        [HttpGet("ClientScript")]
        [AllowAnonymous]
        public ActionResult<string> ClientScript() => "third copy";
    }
}
