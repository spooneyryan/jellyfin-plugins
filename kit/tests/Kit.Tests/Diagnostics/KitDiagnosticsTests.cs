using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Tests.Storage;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Diagnostics;

/// <summary>
/// The kit's two diagnostics endpoints over HTTP, in Jellyfin's pipeline with its authentication: <c>POST
/// /&lt;Route&gt;/Diagnostics/ClientHealth</c> answers 204 whatever happened to the report (413 over 16 KB, 403 for API
/// keys), and <c>GET /&lt;Route&gt;/Admin/Diagnostics</c> shows the accepted reports to admins.
/// </summary>
public sealed class KitDiagnosticsTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("a11ce000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0b00000-0000-0000-0000-000000000002");

    private readonly ListLogger _logger = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly TempDirectory _data = new();
    private bool _reporting = true;

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task AValidReport_Answers204_AndAppearsInTheLog_AndInDiagnostics()
    {
        await using var host = await StartAsync();

        using var response = await Post(host, ClientHealthReportTests.Valid, "user", Alice);
        var diagnostics = await Diagnostics(host);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("web anchor fixtureList is missing on the fixture view (web 10.11.5)", StringComparison.Ordinal));
        var entry = Assert.Single(diagnostics.GetProperty("ClientHealth").EnumerateArray());
        Assert.Equal("fixtureList", entry.GetProperty("Anchor").GetString());
        Assert.Equal("fixture", entry.GetProperty("View").GetString());
        Assert.Equal("10.11.5", entry.GetProperty("WebVersion").GetString());
        Assert.Equal("Desktop", entry.GetProperty("Layout").GetString());
        Assert.Equal(2, entry.GetProperty("Count").GetInt32());
        Assert.Equal(1, entry.GetProperty("Users").GetInt32());
        Assert.Equal(1, diagnostics.GetProperty("WebAnchors").GetProperty("Reports").GetProperty("Accepted").GetInt32());
    }

    [Fact]
    public async Task ForgedReports_Answer204_AndAreDropped()
    {
        await using var host = await StartAsync();
        string[] forged =
        [
            ClientHealthReportTests.Valid.Replace("\"Anchor\": \"fixtureList\"", "\"Anchor\": \"forged\"", StringComparison.Ordinal),
            ClientHealthReportTests.Valid.Replace("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"ten\"", StringComparison.Ordinal),
            ClientHealthReportTests.Valid.Replace("\"Client\": \"Jellyfin Web\"", "\"Client\": \"<b>x</b>\"", StringComparison.Ordinal),
            "{ not json",
        ];

        foreach (var body in forged)
        {
            using var response = await Post(host, body, "user", Alice);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        // A flood from one user: the first valid report counts, the rest are dropped.
        for (var i = 0; i < 5; i++)
        {
            using var response = await Post(host, ClientHealthReportTests.Valid, "user", Bob);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var diagnostics = await Diagnostics(host);
        var reports = diagnostics.GetProperty("WebAnchors").GetProperty("Reports");
        Assert.Equal(4, reports.GetProperty("Invalid").GetInt32());
        Assert.Equal(4, reports.GetProperty("RateLimited").GetInt32());
        Assert.Equal(1, reports.GetProperty("Accepted").GetInt32());
        var entry = Assert.Single(diagnostics.GetProperty("ClientHealth").EnumerateArray());
        Assert.Equal(2, entry.GetProperty("Count").GetInt32());
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task ABodyOver16KB_Answers413()
    {
        await using var host = await StartAsync();
        var big = ClientHealthReportTests.Valid.Replace("\"Layout\"", "\"Padding\": \"" + new string('x', ClientHealthReport.MaxBodyBytes) + "\", \"Layout\"", StringComparison.Ordinal);

        using var sized = await Post(host, big, "user", Alice);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, sized.StatusCode);
        Assert.Equal("application/problem+json", sized.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, (await Diagnostics(host)).GetProperty("WebAnchors").GetProperty("Reports").GetProperty("TooLarge").GetInt32());
    }

    [Fact]
    public async Task ABodyWithoutAContentLength_IsCountedAsItIsRead()
    {
        // A chunked request carries no Content-Length: the endpoint stops reading after 16 KB and answers 413.
        var options = WebKit.Options();
        var catalog = WebAnchorCatalog.Load(options.PluginAssembly, options.PluginAssembly);
        var registry = new ClientHealthRegistry(options, catalog, WebKit.LoggerFactory(_logger), _time);
        using var services = new ServiceCollection().BuildServiceProvider();
        var diagnostics = new KitDiagnostics(options, new KitAbiStatus("Kit Test", "10.11", "10.11.5", isActive: true, "ok"), registry, services, []);
        var big = ClientHealthReportTests.Valid.Replace("\"Layout\"", "\"Padding\": \"" + new string('x', ClientHealthReport.MaxBodyBytes) + "\", \"Layout\"", StringComparison.Ordinal);

        var tooLarge = await diagnostics.ReceiveClientHealthAsync(Context(big, services));
        var fine = await diagnostics.ReceiveClientHealthAsync(Context(ClientHealthReportTests.Valid, services));

        Assert.Equal(413, Assert.IsType<KitProblemResult>(tooLarge).StatusCode);
        Assert.IsType<NoContentResult>(fine);
        Assert.Equal(1, registry.Counts[ClientHealthOutcome.TooLarge]);
        Assert.Equal(1, registry.Counts[ClientHealthOutcome.Accepted]);
    }

    private static DefaultHttpContext Context(string body, IServiceProvider services)
    {
        var context = new DefaultHttpContext { User = JellyfinAuth.CreatePrincipal(Alice, isAdministrator: false), RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Body = new TrickleStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    /// <summary>A request body that hands out at most 1000 bytes per read, as a network stream does.</summary>
    private sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], cancellationToken);
    }

    [Fact]
    public async Task APIKeys_Get403_AndAnonymousCallers_401()
    {
        await using var host = await StartAsync();

        using var apiKey = await Post(host, ClientHealthReportTests.Valid, "apikey", null);
        using var anonymous = await Post(host, ClientHealthReportTests.Valid, null, null);

        Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(await Entries(host));
    }

    [Fact]
    public async Task WithReportingOff_ReportsAreDropped_AndNothingIsLogged()
    {
        _reporting = false;
        await using var host = await StartAsync();

        using var response = await Post(host, ClientHealthReportTests.Valid, "user", Alice);
        var diagnostics = await Diagnostics(host);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(_logger.At(LogLevel.Warning));
        Assert.Empty(diagnostics.GetProperty("ClientHealth").EnumerateArray());
        Assert.False(diagnostics.GetProperty("WebAnchors").GetProperty("Reporting").GetBoolean());
        Assert.Equal(1, diagnostics.GetProperty("WebAnchors").GetProperty("Reports").GetProperty("Disabled").GetInt32());
    }

    [Fact]
    public async Task Diagnostics_IsForAdminsOnly_AndCarriesTheKitShape()
    {
        await using var host = await StartAsync();

        using var user = await host.SendAsync(HttpMethod.Get, "/Fx/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "user"));
        var diagnostics = await Diagnostics(host);

        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        Assert.Equal(
            ["Abi", "ClientHealth", "KitVersion", "Pages", "PluginVersion", "Store", "WebAnchors"],
            diagnostics.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(KitBuild.KitVersion, diagnostics.GetProperty("KitVersion").GetString());
        Assert.True(diagnostics.GetProperty("Abi").GetProperty("Active").GetBoolean());
        Assert.Equal("Ok", diagnostics.GetProperty("Store").GetProperty("Status").GetString());
        Assert.Equal(FxNotesDocument.CurrentVersion, diagnostics.GetProperty("Store").GetProperty("Version").GetInt32());
        Assert.Equal(Path.Combine(_data.Path, "notes.json"), diagnostics.GetProperty("Store").GetProperty("File").GetString());
        Assert.Equal("NotStarted", Assert.Single(diagnostics.GetProperty("Pages").EnumerateArray()).GetProperty("Host").GetString());
        Assert.Contains("fixtureList", diagnostics.GetProperty("WebAnchors").GetProperty("Declared").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("kit.f1Host", diagnostics.GetProperty("WebAnchors").GetProperty("Declared").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(diagnostics.GetProperty("WebAnchors").GetProperty("Problems").EnumerateArray());
        Assert.Equal(ClientHealthRegistry.MaxEntries, diagnostics.GetProperty("WebAnchors").GetProperty("MaxEntries").GetInt32());
        Assert.Equal(
            ["Accepted", "Disabled", "Invalid", "Overflow", "RateLimited", "TooLarge", "TooManyWebClients"],
            diagnostics.GetProperty("WebAnchors").GetProperty("Reports").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AddKitDiagnostics_RegistersOneInstance_ForBothServiceTypes()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(WebKit.Options())
            .AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", isActive: true, "ok"));
        services.AddKitDiagnostics().AddKitDiagnostics();

        using var provider = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(IKitDiagnostics));
        Assert.Single(services, d => d.ServiceType == typeof(ClientHealthRegistry));
        Assert.Same(provider.GetRequiredService<KitDiagnostics>(), provider.GetRequiredService<IKitDiagnostics>());
        Assert.Empty(provider.GetRequiredService<ClientHealthRegistry>().Catalog.Problems);
        Assert.False(provider.GetRequiredService<IKitDiagnostics>().Describe().ContainsKey("Pages"), "no page hosts without AddKitPages");
    }

    [Fact]
    public void ASourceThatFails_ShowsItsError_AndTheRestStill()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(WebKit.Options())
            .AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", isActive: true, "ok"))
            .AddKitDiagnostics()
            .AddKitDiagnosticsSource("Broken", _ => throw new IOException("disk gone"));
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<IKitDiagnostics>().Describe();

        Assert.Equal("disk gone", ((IDictionary<string, object?>)result["Broken"]!)["Error"]);
        Assert.NotNull(result["ClientHealth"]);
    }

    private static Task<HttpResponseMessage> Post(WebHostHarness host, string body, string? caller, Guid? userId)
        => host.SendAsync(HttpMethod.Post, "/Fx/Diagnostics/ClientHealth", request =>
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
            }

            if (userId is not null)
            {
                request.Headers.Add(JellyfinAuth.UserIdHeader, userId.Value.ToString("N"));
            }
        });

    private static async Task<JsonElement> Diagnostics(WebHostHarness host)
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/Fx/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private static async Task<List<JsonElement>> Entries(WebHostHarness host)
        => (await Diagnostics(host)).GetProperty("ClientHealth").EnumerateArray().ToList();

    private Task<WebHostHarness> StartAsync()
    {
        var options = new KitOptions
        {
            PluginName = "Kit Test",
            RoutePrefix = "Fx",
            Prefix = "jfkt",
            SchemaPrefix = "Fx",
            DataFolder = "kittest",
            PluginAssembly = typeof(KitDiagnosticsTests).Assembly,
            Pages = [new KitPage("alpha", "Alpha", "star", "AlphaPage")],
            ReportWebAnchorProblems = _ => _reporting
        };
        var catalog = WebAnchorCatalog.Load(options.PluginAssembly, options.PluginAssembly);
        var store = new JsonDocumentStore<FxNotesDocument>(Path.Combine(_data.Path, "notes.json"), FxNotesDocument.CurrentVersion, FxNotesDocument.CreateEmpty, FxNotesDocument.Migrations, _logger);
        return new WebHostHarness()
            .WithServices(services =>
            {
                services.AddSingleton(options);
                services.AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", isActive: true, "ok"));
                services.AddSingleton(new ClientHealthRegistry(options, catalog, WebKit.LoggerFactory(_logger), _time));
                services.AddSingleton(new KitPageHosts(options));
                services.AddKitDiagnostics();
                services.AddKitDiagnosticsSource("Store", _ => KitDiagnostics.DescribeStore(store));
            })
            .WithMvc(typeof(FxDiagnosticsController), typeof(FxAdminController))
            .StartAsync();
    }

    /// <summary>A plugin's ClientHealth action, as the kit's API describes it.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx/Diagnostics")]
    [PluginExceptionFilter]
    public sealed class FxDiagnosticsController : ControllerBase
    {
        private readonly IKitDiagnostics _diagnostics;

        public FxDiagnosticsController(IKitDiagnostics diagnostics)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        [HttpPost("ClientHealth")]
        [RequestSizeLimit(ClientHealthReport.MaxBodyBytes)]
        public Task<IActionResult> PostFxClientHealth() => _diagnostics.ReceiveClientHealthAsync(HttpContext);
    }

    /// <summary>A plugin's admin Diagnostics action, as the kit's API describes it.</summary>
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("Fx/Admin")]
    [PluginExceptionFilter]
    public sealed class FxAdminController : ControllerBase
    {
        private readonly IKitDiagnostics _diagnostics;

        public FxAdminController(IKitDiagnostics diagnostics)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        [HttpGet("Diagnostics")]
        public ActionResult<IDictionary<string, object?>> GetFxDiagnostics() => Ok(_diagnostics.Describe());
    }
}
