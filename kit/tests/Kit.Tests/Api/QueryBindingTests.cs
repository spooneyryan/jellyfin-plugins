using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Api;

/// <summary>
/// The kit's query-string binders (400 for a bad value) behind the harness's copy of Jellyfin's MVC, whose
/// binder of nullable enums ignores a value it can't read: <see cref="StrictEnumQueryAttribute"/> makes such a value, and a
/// number no member has, a 400 with MVC's validation ProblemDetails, for parameters and <c>[FromQuery]</c> model properties;
/// <see cref="CommaSeparatedQueryAttribute"/> binds a list comma-separated or repeated, and refuses a value that is no member;
/// since Bookmarks' clips it binds lists of GUIDs the same way, refusing a value that isn't one.
/// </summary>
public sealed class QueryBindingTests
{
    public enum FxShade
    {
        Light,
        Dark,
        Purple
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sort=Purple", "Purple")]
    [InlineData("sort=purple", "Purple")]
    [InlineData("sort=Pink", "null")]
    [InlineData("sort=99", "99")]
    [InlineData("", "null")]
    public async Task JellyfinsOwnBinder_IgnoresANameItCantRead_AndBindsAnyNumber(string query, string bound)
    {
        // The trap the strict filter closes: this is what a sandbox did with GET /Bookmarks/Query?sortBy=Count.
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, "/Fx/Lenient?" + query);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(bound, body);
    }

    [Theory]
    [InlineData("sort=dark&shade=PURPLE&plain=1", "Dark|Purple|Dark|null")]
    [InlineData("sort=2", "Purple|null|Light|null")]
    [InlineData("sort=&shade=%20", "null|null|Light|null")]
    [InlineData("", "null|null|Light|null")]
    [InlineData("shades=Light,dark", "null|null|Light|Light,Dark")]
    [InlineData("shades=Light&shades=Purple", "null|null|Light|Light,Purple")]
    [InlineData("shades=%20Light%20,%20Dark%20,,", "null|null|Light|Light,Dark")]
    [InlineData("shades=2,0", "null|null|Light|Purple,Light")]
    [InlineData("limit=7&other=Pink", "null|null|Light|null")]
    public async Task Strict_TakesMembersInAnyCase_TheirNumbers_AndNothing(string query, string bound)
    {
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, "/Fx/Strict?" + query);

        Assert.True(status == HttpStatusCode.OK, $"{query}: {status} {body}");
        Assert.Equal(bound, body);
    }

    [Theory]
    [InlineData("sort=Pink", "sort", "The value 'Pink' is not one of Light, Dark, Purple.")]
    [InlineData("sort=99", "sort", "The value '99' is not one of Light, Dark, Purple.")]
    [InlineData("sort=Light,Dark", "sort", "The value 'Light,Dark' is not one of Light, Dark, Purple.")]
    [InlineData("shade=Pink", "Shade", "The value 'Pink' is not one of Light, Dark, Purple.")]
    [InlineData("SHADE=-1", "Shade", "The value '-1' is not one of Light, Dark, Purple.")]
    [InlineData("shades=Light,Pink", "Shades", "The value 'Pink' is not one of Light, Dark, Purple.")]
    public async Task Strict_RefusesAValueThatIsNoMember_With400ValidationProblemDetails(string query, string key, string message)
    {
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, "/Fx/Strict?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        var errors = problem.RootElement.GetProperty("errors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()).ToList(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([message], errors[key]);
    }

    [Theory]
    [InlineData("/Fx/Strict?filter.shade=Pink", "filter.Shade", "Pink")]
    [InlineData("/Fx/Strict?FILTER.SHADE=-1", "filter.Shade", "-1")]
    [InlineData("/Fx/Strict?filter.limit=2&shade=Pink", "Shade", "Pink")]
    [InlineData("/Fx/Strict?filter.shades=Light,Pink", "filter.Shades", "Pink")]
    [InlineData("/Fx/Named?f.shade=Pink", "f.Shade", "Pink")]
    [InlineData("/Fx/Unprefixed?shade=Pink", "Shade", "Pink")]
    public async Task Strict_RefusesAValueThatIsNoMember_UnderTheModelsPrefixToo(string path, string key, string value)
    {
        // A key under the parameter's name (filter.shade) makes MVC bind the whole model under that prefix, and Jellyfin's
        // binder then drops a bad filter.shade as it drops a bad shade; an explicit name (f) is always the prefix, and an
        // empty one never is. A bad value under either name the model could bind from is a 400.
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, path);

        Assert.True(status == HttpStatusCode.BadRequest, $"{path}: {status} {body}");
        using var problem = JsonDocument.Parse(body);
        var errors = problem.RootElement.GetProperty("errors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()).ToList(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([$"The value '{value}' is not one of Light, Dark, Purple."], errors[key]);
    }

    [Theory]
    [InlineData("/Fx/Strict?filter.shade=dark&filter.plain=1&filter.shades=purple", "null|Dark|Dark|Purple")]
    [InlineData("/Fx/Strict?filter.limit=2&shade=Dark", "null|null|Light|null")]
    [InlineData("/Fx/Named?f.shade=dark&shade=Pink&filter.shade=Pink", "Dark")]
    [InlineData("/Fx/Unprefixed?shade=dark&filter.shade=Pink&f.shade=Pink", "Dark")]
    public async Task Strict_TakesAMemberUnderTheModelsPrefix_AndLeavesKeysTheModelNeverBinds(string path, string bound)
    {
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, path);

        Assert.True(status == HttpStatusCode.OK, $"{path}: {status} {body}");
        Assert.Equal(bound, body);
    }

    [Theory]
    [InlineData("ids=8fb8a4d6a3e24d14bd3e1b02f6e7a0c1,0b6f1c0e-3a52-4c1d-9a1e-6c8b7d2f4e10", "8fb8a4d6a3e24d14bd3e1b02f6e7a0c1,0b6f1c0e3a524c1d9a1e6c8b7d2f4e10")]
    [InlineData("ids=8FB8A4D6A3E24D14BD3E1B02F6E7A0C1&ids={0b6f1c0e-3a52-4c1d-9a1e-6c8b7d2f4e10}", "8fb8a4d6a3e24d14bd3e1b02f6e7a0c1,0b6f1c0e3a524c1d9a1e6c8b7d2f4e10")]
    [InlineData("ids=%208fb8a4d6a3e24d14bd3e1b02f6e7a0c1%20,,", "8fb8a4d6a3e24d14bd3e1b02f6e7a0c1")]
    [InlineData("ids=", "")]
    [InlineData("", "null")]
    public async Task CommaSeparated_BindsGuids_CommaSeparatedOrRepeated_InAnyFormat(string query, string bound)
    {
        // Bookmarks' GET /Bookmarks/Clips/Status?ids=a,b, as Jellyfin's own endpoints take id lists (written without dashes).
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, "/Fx/Ids?" + query);

        Assert.True(status == HttpStatusCode.OK, $"{query}: {status} {body}");
        Assert.Equal(bound, body);
    }

    [Theory]
    [InlineData("ids=8fb8a4d6a3e24d14bd3e1b02f6e7a0c1,not-a-guid", "not-a-guid")]
    [InlineData("ids=8fb8a4d6&ids=8fb8a4d6a3e24d14bd3e1b02f6e7a0c1", "8fb8a4d6")]
    public async Task CommaSeparated_RefusesAValueThatIsNoGuid_With400ValidationProblemDetails(string query, string value)
    {
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, "/Fx/Ids?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var problem = JsonDocument.Parse(body);
        var errors = problem.RootElement.GetProperty("errors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()).ToList(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([$"The value '{value}' is not a GUID."], errors["ids"]);
    }

    [Fact]
    public async Task ANonNullableEnum_IsMvcsOwn_AndRefusesAValueThatIsNoMember()
    {
        await using var host = await StartAsync();

        var (status, body) = await GetAsync(host, "/Fx/Strict?plain=Pink");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Plain", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Dark", true)]
    [InlineData(" dark ", true)]
    [InlineData("1", true)]
    [InlineData("3", false)]
    [InlineData("Dark,Light", false)]
    [InlineData("Darker", false)]
    public void IsMember_NamesInAnyCaseAndNumbersOfMembers(string value, bool member)
        => Assert.Equal(member, StrictEnumQueryAttribute.IsMember(typeof(FxShade), value));

    [Theory]
    [InlineData("Dark", true)]
    [InlineData(" purple ", true)]
    [InlineData("1", false)]
    [InlineData("01", false)]
    [InlineData("-0", false)]
    [InlineData("Dark,Light", false)]
    [InlineData("Darker", false)]
    public void IsMember_WithNamesOnly_RefusesEveryNumber(string value, bool member)
        => Assert.Equal(member, StrictEnumQueryAttribute.IsMember(typeof(FxShade), value, namesOnly: true));

    [Fact]
    public async Task NamesOnly_ANumberIs400_ANameStillBinds()
    {
        // User Ratings' sort=1 used to sort Oldest; with NamesOnly a number is a 400 like a wrong name.
        await using var host = await new WebHostHarness().WithMvc(typeof(FxNamesOnlyController)).StartAsync();

        Assert.Equal((HttpStatusCode.OK, "Dark"), await GetAsync(host, "/Fx/NamesOnly?sort=dark"));
        Assert.Equal((HttpStatusCode.OK, "null"), await GetAsync(host, "/Fx/NamesOnly"));
        var (status, body) = await GetAsync(host, "/Fx/NamesOnly?sort=1");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("The value '1' is not one of Light, Dark, Purple.", body, StringComparison.Ordinal);
    }

    private static Task<WebHostHarness> StartAsync()
        => new WebHostHarness().WithMvc(typeof(FxStrictController), typeof(FxLenientController), typeof(FxNamedController), typeof(FxUnprefixedController), typeof(FxIdsController)).StartAsync();

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebHostHarness host, string path)
    {
        using var response = await host.SendAsync(HttpMethod.Get, path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A <c>[FromQuery]</c> model with each kind of enum property.</summary>
    public sealed class FxShadeFilter
    {
        public FxShade? Shade { get; set; }

        public FxShade Plain { get; set; }

        [CommaSeparatedQuery]
        public IReadOnlyList<FxShade>? Shades { get; set; }

        public int? Limit { get; set; }
    }

    /// <summary>A controller whose enum query values are strict.</summary>
    [ApiController]
    [Route("Fx/Strict")]
    [StrictEnumQuery]
    public sealed class FxStrictController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> GetFxStrict([FromQuery] FxShade? sort, [FromQuery] FxShadeFilter filter)
            => $"{Show(sort)}|{Show(filter.Shade)}|{filter.Plain}|{(filter.Shades is null ? "null" : string.Join(',', filter.Shades))}";

        private static string Show(FxShade? value) => value?.ToString() ?? "null";
    }

    /// <summary>A controller whose enum query values must be names.</summary>
    [ApiController]
    [Route("Fx/NamesOnly")]
    [StrictEnumQuery(NamesOnly = true)]
    public sealed class FxNamesOnlyController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> GetFxNamesOnly([FromQuery] FxShade? sort) => sort?.ToString() ?? "null";
    }

    /// <summary>A model with an explicit name: MVC always binds it under that prefix (<c>f.shade</c>).</summary>
    [ApiController]
    [Route("Fx/Named")]
    [StrictEnumQuery]
    public sealed class FxNamedController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> GetFxNamed([FromQuery(Name = "f")] FxShadeFilter filter) => filter.Shade?.ToString() ?? "null";
    }

    /// <summary>A model pinned to the empty prefix: MVC never binds it under one.</summary>
    [ApiController]
    [Route("Fx/Unprefixed")]
    [StrictEnumQuery]
    public sealed class FxUnprefixedController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> GetFxUnprefixed([FromQuery(Name = "")] FxShadeFilter filter) => filter.Shade?.ToString() ?? "null";
    }

    /// <summary>A parameter that is a list of GUIDs.</summary>
    [ApiController]
    [Route("Fx/Ids")]
    [StrictEnumQuery]
    public sealed class FxIdsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> GetFxIds([FromQuery, CommaSeparatedQuery] IReadOnlyList<Guid>? ids)
            => ids is null ? "null" : string.Join(',', ids.Select(id => id.ToString("N")));
    }

    /// <summary>The same parameter without the filter: Jellyfin's binder alone.</summary>
    [ApiController]
    [Route("Fx/Lenient")]
    public sealed class FxLenientController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> GetFxLenient([FromQuery] FxShade? sort) => sort?.ToString() ?? "null";
    }
}
