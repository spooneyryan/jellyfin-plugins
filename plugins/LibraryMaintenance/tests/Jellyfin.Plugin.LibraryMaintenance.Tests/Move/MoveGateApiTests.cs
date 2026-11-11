using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static Jellyfin.Plugin.LibraryMaintenance.Tests.Move.MoveApiHost;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// The Move routes' gate (403 for a user who isn't an administrator, 403 <c>feature-disabled</c> with Move
/// off, 503 <c>move.unsupported-os</c> on a simulated host that isn't Windows) and <c>GET /LibraryMaintenance/Capabilities</c>.
/// The gate is checked here on a probe controller with the Move controller's attributes; <c>MoveRoutesApiTests</c> checks
/// every real route.
/// </summary>
public sealed class MoveGateApiTests
{
    private const string Probe = "/LibraryMaintenance/Move/GateProbe";
    private const string Capabilities = "/LibraryMaintenance/Capabilities";

    [Fact]
    public async Task TheGate_AdminOnly_ThenFeatureDisabled_ThenTheHost()
    {
        var configuration = new TestConfiguration(new PluginConfiguration());
        await using var host = await StartAsync(configuration, () => true, extraControllers: typeof(GateProbeController));

        using (var anonymous = await host.SendAsync(HttpMethod.Get, Probe))
        using (var viewer = await host.SendAsync(HttpMethod.Get, Probe, As("user")))
        using (var admin = await host.SendAsync(HttpMethod.Get, Probe, As("admin")))
        using (var key = await host.SendAsync(HttpMethod.Get, Probe, ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
            Assert.Equal(HttpStatusCode.OK, key.StatusCode);
        }

        // Move off: the kit's feature-disabled 403 for an admin; a viewer still gets the policy's plain 403.
        configuration.Configuration.MoveEnabled = false;
        using (var admin = await host.SendAsync(HttpMethod.Get, Probe, As("admin")))
        using (var viewer = await host.SendAsync(HttpMethod.Get, Probe, As("user")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
            var body = await Json(admin);
            Assert.Equal(KitProblemDetails.FeatureDisabledType, body.GetProperty("type").GetString());
            Assert.Equal(MoveFeature.DisabledMessage, body.GetProperty("detail").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
            Assert.DoesNotContain("feature-disabled", await viewer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        // Another host (simulated): Move off still reads as turned off first, then 503 with its code.
        await using var other = await StartAsync(configuration, () => false, extraControllers: typeof(GateProbeController));
        using (var off = await other.SendAsync(HttpMethod.Get, Probe, As("admin")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        }

        configuration.Configuration.MoveEnabled = true;
        using (var unsupported = await other.SendAsync(HttpMethod.Get, Probe, As("admin")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsupported.StatusCode);
            var body = await Json(unsupported);
            Assert.Equal(MoveErrorCodes.UnsupportedOs, body.GetProperty("code").GetString());
            Assert.Equal(MoveFeature.UnsupportedOsMessage, body.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task TheGate_RunsBeforeTheBodyIsRead()
    {
        var configuration = new TestConfiguration(new PluginConfiguration { MoveEnabled = false });
        await using var host = await StartAsync(configuration, () => true, extraControllers: typeof(GateProbeController));

        using var post = await host.SendAsync(HttpMethod.Post, Probe, r =>
        {
            As("admin")(r);
            r.Content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(KitProblemDetails.FeatureDisabledType, (await Json(post)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Capabilities_ForAnAdmin_AUser_AnApiKey()
    {
        var configuration = new TestConfiguration(new PluginConfiguration { ShowDetailPageButton = true });
        await using var host = await StartAsync(configuration, () => true);

        using (var anonymous = await host.SendAsync(HttpMethod.Get, Capabilities))
        using (var key = await host.SendAsync(HttpMethod.Get, Capabilities, ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, key.StatusCode);
        }

        using (var admin = await host.SendAsync(HttpMethod.Get, Capabilities, As("admin")))
        {
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
            var body = await Json(admin);
            Assert.True(body.GetProperty("IsAdmin").GetBoolean());
            Assert.True(body.GetProperty("MoveEnabled").GetBoolean());
            Assert.True(body.GetProperty("MoveSupported").GetBoolean());
            Assert.True(body.GetProperty("CanPlanMove").GetBoolean());
            Assert.False(body.GetProperty("CanMove").GetBoolean()); // no job side in 1.1.0.N yet
            Assert.True(body.GetProperty("ShowMoveInItemMenu").GetBoolean());
            Assert.True(body.GetProperty("ShowMoveDetailPageButton").GetBoolean());
            Assert.False(body.GetProperty("ShowMoveInTvLayout").GetBoolean());
        }

        using (var user = await host.SendAsync(HttpMethod.Get, Capabilities, As("user")))
        {
            Assert.Equal(HttpStatusCode.OK, user.StatusCode);
            var body = await Json(user);
            foreach (var flag in new[] { "IsAdmin", "MoveEnabled", "MoveSupported", "CanPlanMove", "CanMove", "ShowMoveInItemMenu", "ShowMoveDetailPageButton", "ShowMoveInTvLayout" })
            {
                Assert.False(body.GetProperty(flag).GetBoolean(), flag);
            }
        }

        // Move off: the entries are hidden.
        configuration.Configuration.MoveEnabled = false;
        using (var admin = await host.SendAsync(HttpMethod.Get, Capabilities, As("admin")))
        {
            var body = await Json(admin);
            Assert.True(body.GetProperty("IsAdmin").GetBoolean());
            Assert.False(body.GetProperty("MoveEnabled").GetBoolean());
            Assert.False(body.GetProperty("CanPlanMove").GetBoolean());
            Assert.False(body.GetProperty("ShowMoveInItemMenu").GetBoolean());
            Assert.False(body.GetProperty("ShowMoveDetailPageButton").GetBoolean());
        }
    }

    [Fact]
    public async Task Capabilities_OnAnotherHost_CantPlan()
    {
        await using var host = await StartAsync(new TestConfiguration(new PluginConfiguration()), () => false);
        using var admin = await host.SendAsync(HttpMethod.Get, Capabilities, As("admin"));
        var body = await Json(admin);
        Assert.True(body.GetProperty("MoveEnabled").GetBoolean());
        Assert.False(body.GetProperty("MoveSupported").GetBoolean());
        Assert.False(body.GetProperty("CanPlanMove").GetBoolean());
        Assert.False(body.GetProperty("ShowMoveInItemMenu").GetBoolean());
    }

    /// <summary>A route with the Move controller's attributes.</summary>
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("LibraryMaintenance/Move/GateProbe")]
    [MoveGate]
    [PluginExceptionFilter]
    public sealed class GateProbeController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(new { Ok = true });

        [HttpPost]
        public IActionResult Post([FromBody] ProbeBody body) => Ok(body);
    }

    public sealed class ProbeBody
    {
        public string? Text { get; set; }
    }
}
