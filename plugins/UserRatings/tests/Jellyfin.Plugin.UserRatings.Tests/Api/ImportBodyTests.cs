using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Services;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Api;

/// <summary>
/// The import route reads its body as the bytes it is, whatever its <c>Content-Type</c> (found in 0.3.19's verification):
/// behind Jellyfin's web pipeline, MVC's form value providers read a form body before the action unless the route
/// stops them (<see cref="RawBodyAttribute"/>).
/// </summary>
public sealed class ImportBodyTests : IAsyncDisposable
{
    private readonly Mock<IUserRatingsAdminApi> _api = new();
    private WebHostHarness? _host;
    private byte[]? _received;

    public ImportBodyTests()
    {
        _api.Setup(a => a.StartImportAsync(It.IsAny<Guid?>(), It.IsAny<Stream>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid? admin, Stream body, long? length, string? mode, bool? dryRun, string? by, string? plan, CancellationToken ct) =>
            {
                using var copy = new MemoryStream();
                await body.CopyToAsync(copy, ct);
                _received = copy.ToArray();
                return new UserRatingsJobDto { Id = Guid.Empty.ToString("D"), Kind = "Import", State = "Running", DryRun = dryRun ?? true };
            });
    }

    public static TheoryData<string> FormTypes => new()
    {
        "application/x-www-form-urlencoded",
        "multipart/form-data; boundary=zz"
    };

    [Theory]
    [MemberData(nameof(FormTypes))]
    public async Task AnExportSentAsAForm_ReachesTheImportAsTheBytesItIs(string contentType)
    {
        // An export far over the form reader's 2 KiB key limit, and a multipart body that parses as a form.
        var json = "{\"Format\":\"jellyfin-plugin-export\",\"ZzPadding\":\"" + new string('x', 10_000) + "\"}";
        var body = contentType.StartsWith("multipart", StringComparison.Ordinal)
            ? "--zz\r\nContent-Disposition: form-data; name=\"file\"; filename=\"export.json\"\r\nContent-Type: application/json\r\n\r\n" + json + "\r\n--zz--\r\n"
            : json;
        var host = await StartAsync();
        using var response = await host.SendAsync(HttpMethod.Post, "/UserRatings/Admin/Import?mode=Merge&dryRun=true&mapUsersBy=Id", request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "apikey");
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(body, Encoding.UTF8.GetString(Assert.IsType<byte[]>(_received)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private async Task<WebHostHarness> StartAsync()
    {
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        _host = await new WebHostHarness()
            .WithServices(services =>
            {
                KitAbiGuard.Register(services, applicationHost.Object, UserRatingsKit.Options);
                services.AddSingleton(UserRatingsKit.Options);
                services.AddSingleton(_api.Object);
                services.AddSingleton(Mock.Of<IKitDiagnostics>());
                ApiFixture.UseJellyfinJson(services);
            })
            .WithMvc(typeof(UserRatingsAdminController))
            .StartAsync();
        return _host;
    }
}
