using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Kit.Tests.TestHosting;

/// <summary>
/// An in-process MVC server for kit tests: Jellyfin's authentication claims and authorisation policies
/// (<see cref="JellyfinAuth"/>), attribute-routed controllers limited to the given types, and the usual
/// routing, authentication and authorisation middleware order of Jellyfin's Startup.
/// </summary>
internal sealed class MvcTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private MvcTestHost(IHost host)
    {
        _host = host;
        Client = host.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _host.Services;

    public static async Task<MvcTestHost> StartAsync(IReadOnlyCollection<Type> controllers, Action<IServiceCollection>? configure = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddJellyfinTestAuthentication();
                    services.AddJellyfinAuthorization();
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            manager.ApplicationParts.Add(new AssemblyPart(typeof(MvcTestHost).Assembly));
                            manager.FeatureProviders.Clear();
                            manager.FeatureProviders.Add(new OnlyTheseControllers(controllers));
                        });
                    configure?.Invoke(services);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .Build();
        await host.StartAsync().ConfigureAwait(false);
        return new MvcTestHost(host);
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string? caller = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (caller is not null)
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, caller);
        }

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }

    private sealed class OnlyTheseControllers : ControllerFeatureProvider
    {
        private readonly HashSet<Type> _controllers;

        public OnlyTheseControllers(IEnumerable<Type> controllers)
        {
            _controllers = controllers.ToHashSet();
        }

        protected override bool IsController(TypeInfo typeInfo) => _controllers.Contains(typeInfo.AsType()) && base.IsController(typeInfo);
    }
}
