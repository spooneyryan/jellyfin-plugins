using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.Fixtures;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Api;

/// <summary>
/// [KitAbiGate]: an inert build answers 503 ProblemDetails before the controller is constructed.
/// </summary>
public sealed class KitAbiGateTests
{
    [Fact]
    public async Task InactiveBuild_Answers503BeforeTheControllerIsConstructed()
    {
        var counter = new FxConstructionCounter();
        var status = new KitAbiStatus("Kit Test", "10.11", "12.1.0", false, "Kit Test is built for Jellyfin 10.11, but the server is Jellyfin 12.1.0.");
        await using var host = await StartAsync(counter, status);

        using var response = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Gate/Ping", "user"), TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal(status.Message, body.GetProperty("detail").GetString());
        var abi = body.GetProperty("abi");
        Assert.Equal("10.11", abi.GetProperty("Target").GetString());
        Assert.Equal("12.1.0", abi.GetProperty("Server").GetString());
        Assert.False(abi.GetProperty("Active").GetBoolean());
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task InactiveBuild_GatesAnonymousActionsToo()
    {
        var counter = new FxConstructionCounter();
        await using var host = await StartAsync(counter, new KitAbiStatus("Kit Test", "10.11", "12.1.0", false, "Inactive."));

        using var response = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Gate/ClientScript"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task InactiveBuild_StillRequiresAuthenticationFirst()
    {
        // Authorisation filters run before resource filters: an anonymous caller learns nothing about the plugin.
        await using var host = await StartAsync(new FxConstructionCounter(), new KitAbiStatus("Kit Test", "10.11", "12.1.0", false, "Inactive."));

        using var response = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Gate/Ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingStatus_Answers503()
    {
        var counter = new FxConstructionCounter();
        await using var host = await StartAsync(counter, status: null);

        using var response = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Gate/Ping", "user"), TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(KitAbiGateAttribute.NotRegisteredMessage, body.GetProperty("detail").GetString());
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task ActiveBuild_PassesThrough()
    {
        var counter = new FxConstructionCounter();
        await using var host = await StartAsync(counter, new KitAbiStatus("Kit Test", "10.11", "10.11.5", true, "Active."));

        using var ping = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Gate/Ping", "user"), TestContext.Current.CancellationToken);
        using var script = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Gate/ClientScript"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.Equal("pong", await ping.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal(2, counter.Count);
    }

    private static Task<MvcTestHost> StartAsync(FxConstructionCounter counter, KitAbiStatus? status)
        => MvcTestHost.StartAsync(
            [typeof(FxGateController)],
            services =>
            {
                services.AddSingleton(counter);
                if (status is not null)
                {
                    services.AddSingleton(status);
                }
            });
}
