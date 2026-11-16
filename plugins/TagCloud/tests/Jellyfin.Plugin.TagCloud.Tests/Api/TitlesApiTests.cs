using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.TagCloud.Api;
using Jellyfin.Plugin.TagCloud.Services;
using Jellyfin.Plugin.TagCloud.Tests.TestSupport;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Api;

/// <summary>
/// <c>GET /TagCloud/Titles</c> on the wire, behind Jellyfin's pipeline in the kit's harness: the filter and sort
/// parameters bind, an unknown or out-of-range value is a 400 (never a silent default), and the people cloud's words carry
/// <c>PersonId</c> and <c>ImageTag</c>. The service is the real one over the fixture's library.
/// </summary>
public sealed class TitlesApiTests : IAsyncLifetime
{
    private readonly TagCloudFixture _fixture = new();
    private WebHostHarness? _web;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _fixture.Heat.Width = 1920;
        _fixture.Heat.Height = 1080;
        await _fixture.Index.BuildAsync(Ct);
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(_fixture.Admin.Id)).Returns(_fixture.Admin);
        users.Setup(u => u.GetUserById(_fixture.Kid.Id)).Returns(_fixture.Kid);
        _web = await new WebHostHarness()
            .WithServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(new KitAbiStatus("Tag Cloud", "test", "test", true, string.Empty));
                services.AddSingleton<ITagCloudQuery>(_fixture.Query);
                services.AddSingleton(users.Object);
                services.AddSingleton(Mock.Of<IEmbeddedScript>());
                services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
                {
                    var jellyfin = JsonDefaults.PascalCaseOptions;
                    o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
                    o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
                    o.JsonSerializerOptions.Converters.Clear();
                    foreach (var converter in jellyfin.Converters)
                    {
                        o.JsonSerializerOptions.Converters.Add(converter);
                    }
                });
            })
            .WithMvc(typeof(TagCloudController))
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_web is not null)
        {
            await _web.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheFiltersBind_AndTheCountsSayHowManyTheyKeep()
    {
        var (status, body) = await GetAsync("/TagCloud/Titles?source=Tags&scope=Movies&key=heist&sort=Rating&descending=true&q=HEAT&minResolution=P1080&unwatched=false", _fixture.Admin.Id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("Total").GetInt32());
        Assert.Equal(2, body.GetProperty("Unfiltered").GetInt32());
        Assert.Equal(["Heat"], body.GetProperty("Items").EnumerateArray().Select(t => t.GetProperty("Name").GetString()));
    }

    [Theory]
    [InlineData("sort=Popularity")]
    [InlineData("sort=7")]
    [InlineData("minResolution=8K")]
    [InlineData("minResolution=1080")]
    [InlineData("descending=maybe")]
    [InlineData("unwatched=2")]
    [InlineData("startIndex=-1")]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("order=Random&sort=Name")]
    [InlineData("order=Random&descending=true")]
    [InlineData("order=Shuffle")]
    public async Task AValueItCantUse_IsA400(string query)
    {
        using var response = await SendAsync("/TagCloud/Titles?source=Tags&key=heist&" + query, _fixture.Admin.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ANameFilterTooLong_IsA400()
    {
        using var response = await SendAsync("/TagCloud/Titles?source=Tags&key=heist&q=" + new string('x', 101), _fixture.Admin.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheKid_NeverGetsAHiddenTitle_ThroughTheFilters()
    {
        var (_, body) = await GetAsync("/TagCloud/Titles?source=Tags&scope=Both&key=heist&q=scarface", _fixture.Kid.Id);

        Assert.Equal(0, body.GetProperty("Total").GetInt32());
        Assert.Equal(1, body.GetProperty("Unfiltered").GetInt32());
    }

    [Fact]
    public async Task ThePeopleWords_CarryPersonIdAndImageTag_OnTheWire()
    {
        var (_, body) = await GetAsync("/TagCloud/Words?source=Actors&scope=Both", _fixture.Admin.Id);

        var weaver = body.GetProperty("Words").EnumerateArray().Single(w => w.GetProperty("Text").GetString() == "Sigourney Weaver");
        Assert.Equal(_fixture.Persons["Sigourney Weaver"].Id, Guid.Parse(weaver.GetProperty("PersonId").GetString()!));
        Assert.Equal("tag-Sigourney Weaver-Primary", weaver.GetProperty("ImageTag").GetString());
    }

    private async Task<HttpResponseMessage> SendAsync(string path, Guid userId)
        => await _web!.SendAsync(HttpMethod.Get, path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "user");
            request.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString("N"));
        });

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, Guid userId)
    {
        using var response = await SendAsync(path, userId);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, path + ": " + response.StatusCode + " " + text);
        using var document = JsonDocument.Parse(text);
        return (response.StatusCode, document.RootElement.Clone());
    }
}
