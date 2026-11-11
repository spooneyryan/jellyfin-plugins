using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// Jellyfin 10.11.5's web pipeline in process, for tests of <c>index.html</c> injection and of controllers:
/// the plugin startup filters outermost, in registration order (the first is the outermost middleware, as in Jellyfin,
/// where plugins register in plugin-name order), then a copy of <c>Startup.Configure</c>
/// (<c>v10.11.5:Jellyfin.Server/Startup.cs:149-203</c>): BaseUrl redirection, <c>Map(BaseUrl)</c>,
/// <c>UseResponseCompression</c>, <c>UsePathTrim</c> (a leading <c>/emby</c> or <c>/mediabrowser</c> removed), and
/// <c>UseDefaultFiles</c>/<c>UseStaticFiles</c> on <c>/web</c> over a temporary web root, with
/// <c>Cache-Control: no-cache</c> on <c>index.html</c>. <see cref="WithMvc(Type[])"/> adds MVC behind it,
/// with Jellyfin's authentication claims and authorisation policies (<see cref="JellyfinAuth"/>).
/// </summary>
/// <remarks>
/// The web root holds the synthetic <c>index.html</c> (<c>kit/testdata/web</c>), a compressible
/// <c>main.jellyfin.bundle.js</c> and <c>main.jellyfin.css</c>. Files are read from disk on every request, so a test
/// can change them with <see cref="WithFile"/> while the host runs. <see cref="IServerConfigurationManager"/> is a mock
/// whose network configuration has <see cref="BaseUrl"/>.
/// </remarks>
public sealed class WebHostHarness : IAsyncDisposable
{
    /// <summary>The embedded synthetic index.html (TestSupport.props).</summary>
    public const string IndexResource = "Jellyfin.Plugin.Kit.TestData.web.index.html";

    /// <summary>The bundle file in the web root.</summary>
    public const string BundleFile = "main.jellyfin.bundle.js";

    /// <summary>The stylesheet in the web root.</summary>
    public const string CssFile = "main.jellyfin.css";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Lazy<string> _syntheticIndex = new(ReadSyntheticIndex);

    private readonly List<Func<IServiceProvider, IStartupFilter>> _filters = [];
    private readonly List<Action<IServiceCollection>> _services = [];
    private readonly List<Type> _controllers = [];
    private readonly List<Action<MvcOptions>> _mvcOptions = [];
    private readonly TempDirectory _webRoot = new();
    private bool _mvc;
    private PhysicalFileProvider? _files;
    private IHost? _host;
    private HttpClient? _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebHostHarness"/> class with the default web root.
    /// </summary>
    public WebHostHarness()
    {
        WithIndexHtml(SyntheticIndexHtml);
        WithFile(BundleFile, _utf8.GetBytes(string.Concat(Enumerable.Repeat("console.log('synthetic bundle');\n", 400))));
        WithFile(CssFile, _utf8.GetBytes(string.Concat(Enumerable.Repeat(".synthetic{color:inherit}\n", 200))));
    }

    /// <summary>
    /// Gets the synthetic <c>index.html</c> (<c>kit/testdata/web/index.html</c>).
    /// </summary>
    public static string SyntheticIndexHtml => _syntheticIndex.Value;

    /// <summary>
    /// Gets the BaseUrl: empty, or a path such as <c>/jf</c>.
    /// </summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the web root folder.
    /// </summary>
    public string WebRoot => _webRoot.Path;

    /// <summary>
    /// Gets the client of the started host.
    /// </summary>
    public HttpClient Client => _client ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>
    /// Gets the services of the started host.
    /// </summary>
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>
    /// Sets the BaseUrl, as Jellyfin's network setting does (normalised to a leading slash and no trailing one).
    /// </summary>
    /// <param name="baseUrl">The BaseUrl, for example <c>/jf</c>; empty for none.</param>
    /// <returns>This harness.</returns>
    public WebHostHarness WithBaseUrl(string baseUrl)
    {
        EnsureNotStarted();
        BaseUrl = new NetworkConfiguration { BaseUrl = baseUrl }.BaseUrl;
        return this;
    }

    /// <summary>
    /// Replaces the web root's <c>index.html</c>, UTF-8 without a BOM. Allowed while the host runs.
    /// </summary>
    /// <param name="html">The document.</param>
    /// <returns>This harness.</returns>
    public WebHostHarness WithIndexHtml(string html) => WithFile("index.html", _utf8.GetBytes(html));

    /// <summary>
    /// Writes a file into the web root. Allowed while the host runs: files are read on every request.
    /// </summary>
    /// <param name="relativePath">The path under the web root, for example <c>index.html</c>.</param>
    /// <param name="content">The bytes.</param>
    /// <returns>This harness.</returns>
    public WebHostHarness WithFile(string relativePath, byte[] content)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentNullException.ThrowIfNull(content);
        var path = _webRoot.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return this;
    }

    /// <summary>
    /// Adds a startup filter. Filters and the plugin services added with <see cref="WithServices"/> register in call
    /// order, and the first registered filter is the outermost middleware.
    /// </summary>
    /// <param name="filter">Creates the filter from the host's services.</param>
    /// <returns>This harness.</returns>
    public WebHostHarness WithStartupFilter(Func<IServiceProvider, IStartupFilter> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        EnsureNotStarted();
        _services.Add(services => services.AddSingleton(filter));
        return this;
    }

    /// <summary>
    /// Adds services, for example a plugin's registrations. Runs in call order with <see cref="WithStartupFilter"/>,
    /// before MVC is added.
    /// </summary>
    /// <param name="configure">Adds the services.</param>
    /// <returns>This harness.</returns>
    public WebHostHarness WithServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureNotStarted();
        _services.Add(configure);
        return this;
    }

    /// <summary>
    /// Adds MVC behind the static files, as Jellyfin's pipeline does (<c>UseAuthentication</c>, <c>UseRouting</c>,
    /// <c>UseAuthorization</c>, <c>MapControllers</c>): <c>[ApiController]</c> model validation, Jellyfin's claims and
    /// <c>RequiresElevation</c> (<see cref="JellyfinAuth"/>; callers are chosen with <see cref="JellyfinAuth.CallerHeader"/>),
    /// Jellyfin's binder of nullable enums in front of MVC's (<see cref="JellyfinNullableEnumBinder"/>: a value it can't read
    /// leaves the parameter null), and only the given controllers, which may be nested public test types named
    /// like Jellyfin's own.
    /// </summary>
    /// <param name="controllers">The controller types.</param>
    /// <returns>This harness.</returns>
    public WebHostHarness WithMvc(params Type[] controllers) => WithMvc(static _ => { }, controllers);

    /// <summary>
    /// Adds MVC as <see cref="WithMvc(Type[])"/> does, with a change to the MVC options, for example global filters
    /// (<c>options.Filters.Add(..., order)</c>, the way Jellyfin Enhanced and Access Roles register theirs).
    /// </summary>
    /// <param name="configure">Changes the MVC options.</param>
    /// <param name="controllers">The controller types.</param>
    /// <returns>This harness.</returns>
    public WebHostHarness WithMvc(Action<MvcOptions> configure, params Type[] controllers)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(controllers);
        EnsureNotStarted();
        _mvc = true;
        _mvcOptions.Add(configure);
        _controllers.AddRange(controllers);
        return this;
    }

    /// <summary>
    /// Builds and starts the host.
    /// </summary>
    /// <returns>This harness.</returns>
    public async Task<WebHostHarness> StartAsync()
    {
        EnsureNotStarted();
        var baseUrl = BaseUrl;
        _files = new PhysicalFileProvider(WebRoot);
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.GetConfiguration("network")).Returns(() => new NetworkConfiguration { BaseUrl = baseUrl });

        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(configuration.Object);
                    foreach (var configure in _services)
                    {
                        configure(services);
                    }

                    services.AddResponseCompression();
                    if (_mvc)
                    {
                        AddMvc(services);
                    }
                })
                .Configure(app => ConfigureJellyfinPipeline(app, baseUrl)))
            .Build();
        await _host.StartAsync().ConfigureAwait(false);
        _client = _host.GetTestClient();
        return this;
    }

    /// <summary>
    /// Sends a request to a path under the BaseUrl.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path without the BaseUrl, for example <c>/web/index.html</c>.</param>
    /// <param name="configure">Adds headers.</param>
    /// <returns>The response.</returns>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        configure?.Invoke(request);
        return await Client.SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a path under the BaseUrl as a string, for example <c>/jf/web/</c>.
    /// </summary>
    /// <param name="path">The path without the BaseUrl.</param>
    /// <returns>The document text.</returns>
    public async Task<string> GetStringAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }

        _files?.Dispose();
        _webRoot.Dispose();
    }

    private static string ReadSyntheticIndex()
    {
        using var stream = typeof(WebHostHarness).Assembly.GetManifestResourceStream(IndexResource)
            ?? throw new InvalidOperationException($"The test project doesn't embed {IndexResource} (kit/build/TestSupport.props).");
        using var reader = new StreamReader(stream, _utf8);
        return reader.ReadToEnd();
    }

    private void AddMvc(IServiceCollection services)
    {
        services.AddJellyfinTestAuthentication();
        services.AddJellyfinAuthorization();
        services
            .AddControllers(options =>
            {
                // AddJellyfinApi (ApiServiceCollectionExtensions.cs:134): Jellyfin's nullable-enum binder comes first.
                options.ModelBinderProviders.Insert(0, new JellyfinNullableEnumBinder.Provider());
                foreach (var configure in _mvcOptions)
                {
                    configure(options);
                }
            })
            .ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                foreach (var assembly in _controllers.Select(c => c.Assembly).Distinct())
                {
                    manager.ApplicationParts.Add(new AssemblyPart(assembly));
                }

                manager.FeatureProviders.Clear();
                manager.FeatureProviders.Add(new OnlyTheseControllers(_controllers));
            });
    }

    // Startup.Configure of v10.11.5 (Startup.cs:149-203), reduced to what injection and controllers meet.
    private void ConfigureJellyfinPipeline(IApplicationBuilder app, string baseUrl)
    {
        app.Use((HttpContext context, RequestDelegate next) => RedirectOutsideBaseUrl(context, next, baseUrl));
        app.Map(baseUrl, main =>
        {
            main.UseResponseCompression();
            main.Use(TrimLegacyPrefix);
            main.UseDefaultFiles(new DefaultFilesOptions { FileProvider = _files, RequestPath = "/web" });
            main.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = _files,
                RequestPath = "/web",
                OnPrepareResponse = context =>
                {
                    if (Path.GetFileName(context.File.Name).Equals("index.html", StringComparison.Ordinal))
                    {
                        context.Context.Response.Headers.CacheControl = "no-cache";
                    }
                },
            });

            if (_mvc)
            {
                main.UseAuthentication();
                main.UseRouting();
                main.UseAuthorization();
                main.UseEndpoints(endpoints => endpoints.MapControllers());
            }
        });
    }

    // Jellyfin's BaseUrlRedirectionMiddleware (v10.11.5 Jellyfin.Api/Middleware/BaseUrlRedirectionMiddleware.cs): a path
    // outside the BaseUrl, or the BaseUrl itself, is redirected to the web client.
    private static Task RedirectOutsideBaseUrl(HttpContext context, RequestDelegate next, string baseUrl)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.Length == 0
            || path.Equals(baseUrl, StringComparison.OrdinalIgnoreCase)
            || path.Equals(baseUrl + "/", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect(baseUrl + "/web/");
            return Task.CompletedTask;
        }

        return next(context);
    }

    // Jellyfin's UsePathTrim (v10.11.5 Jellyfin.Api/Middleware/LegacyEmbyRouteRewriteMiddleware.cs): one leading "/emby",
    // else "/mediabrowser", in any case, is removed from the path inside Map(BaseUrl).
    private static Task TrimLegacyPrefix(HttpContext context, RequestDelegate next)
    {
        const string Emby = "/emby";
        const string MediaBrowser = "/mediabrowser";
        var path = context.Request.Path.ToString();
        if (path.StartsWith(Emby, StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = path[Emby.Length..];
        }
        else if (path.StartsWith(MediaBrowser, StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = path[MediaBrowser.Length..];
        }

        return next(context);
    }

    private void EnsureNotStarted()
    {
        if (_host is not null)
        {
            throw new InvalidOperationException("The host has started; configure the harness before StartAsync.");
        }
    }

    private sealed class OnlyTheseControllers : ControllerFeatureProvider
    {
        private readonly HashSet<Type> _controllers;

        public OnlyTheseControllers(IEnumerable<Type> controllers)
        {
            _controllers = controllers.ToHashSet();
        }

        // Nested public test types count as controllers here (MVC's own rule takes top-level types only).
        protected override bool IsController(TypeInfo typeInfo) => _controllers.Contains(typeInfo.AsType());
    }
}
