using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Search;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// <c>GET /SmartSearch/Libraries</c>, <c>/Values/{fieldId}</c> and <c>/Series</c> on the wire: each from what the caller
/// can see only, so a restricted user is never offered a library, value, count or series from an item they can't see
/// (another library, above their parental limit, unrated where they block it, or carrying a blocked tag); the value
/// index shared by users of one parental profile and rebuilt after an item changes; and the parameters' limits.
/// </summary>
public sealed class LookupApiTests
{
    // The world: what each item carries, and who may see it.
    private static Fixture Build()
    {
        var world = new PlannerWorld();
        var w = new Fixture(world)
        {
            Open = Add(world, BaseItemKind.Movie, "Open", world.Movies, "PG", 10, s =>
            {
                s.Genres = ["Drama", "Action"];
                s.Tags = ["Classic"];
                s.Studios = ["Northwind"];
                s.Container = "mkv";
            }),
            OtherFolder = Add(world, BaseItemKind.Movie, "Other folder", world.Movies, "G", 0, s =>
            {
                s.Genres = ["Drama"];
                s.Container = "mp4,m4v";
                s.TopParentId = world.FolderB;
            }),
            Private = Add(world, BaseItemKind.Movie, "Private", world.Private, "PG", 10, s =>
            {
                s.Genres = ["Secret Genre", "Drama"];
                s.Studios = ["Hidden Studio"];
                s.Tags = ["Private Tag"];
            }),
            Rated = Add(world, BaseItemKind.Movie, "Rated R", world.Movies, "R", 17, s =>
            {
                s.Genres = ["Horror", "Drama"];
                s.Studios = ["Grim Pictures"];
            }),
            Unrated = Add(world, BaseItemKind.Movie, "Unrated", world.Movies, null, null, s => s.Genres = ["Unrated Genre"]),
            Gory = Add(world, BaseItemKind.Movie, "Gory", world.Movies, "PG", 10, s =>
            {
                s.Genres = ["Splatter"];
                s.Tags = ["Gore", "Classic"];
            }),
            Heroes = Add(world, BaseItemKind.Series, "Heroes", world.Shows, "PG", 10, s =>
            {
                s.Genres = ["Drama"];
                s.Year = 2006;
            }),
            Heroines = Add(world, BaseItemKind.Series, "Héroïnes", world.Shows, "PG", 10, null),
            Superheroes = Add(world, BaseItemKind.Series, "Superheroes", world.Shows, "PG", 10, null),
            DarkHeroes = Add(world, BaseItemKind.Series, "Dark Heroes", world.Shows, "R", 17, null),
            GoryHeroes = Add(world, BaseItemKind.Series, "Gory Heroes", world.Shows, "PG", 10, s => s.Tags = ["Gore"]),
            PrivateHeroes = Add(world, BaseItemKind.Series, "Private Heroes", world.Private, "PG", 10, null)
        };
        return w;
    }

    private static FakeRow Add(PlannerWorld world, BaseItemKind kind, string name, CollectionFolder library, string? rating, int? score, Action<RowSpec>? set)
        => world.Add(kind, name, library, s =>
        {
            s.OfficialRating = rating;
            s.InheritedRating = score;
            set?.Invoke(s);
        });

    private static Dictionary<string, int> Values(JsonElement body)
        => body.EnumerateArray().ToDictionary(v => v.GetProperty("Value").GetString()!, v => v.GetProperty("Count").GetInt32(), StringComparer.Ordinal);

    [Fact]
    public async Task Libraries_AreTheCallersOwn()
    {
        var w = Build();
        await using var host = await new SearchApiHost(w.World).StartAsync();

        var admin = await host.OkAsync("GET", "/SmartSearch/Libraries", host.Admin);
        var kid = await host.OkAsync("GET", "/SmartSearch/Libraries", host.Kid);

        Assert.Equal(7, admin.GetArrayLength());
        Assert.Equal(["Movies", "Shows", "Home", "Collections"], kid.EnumerateArray().Select(l => l.GetProperty("Name").GetString()));
        Assert.Equal(["Id", "Name", "CollectionType"], kid[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("movies", kid[0].GetProperty("CollectionType").GetString());
        Assert.DoesNotContain("Private", kid.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden", kid.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Values_CountOnlyWhatTheCallerCanSee()
    {
        var w = Build();
        await using var host = await new SearchApiHost(w.World).StartAsync();

        var admin = Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Admin));
        var kid = Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Kid));

        Assert.Equal(new Dictionary<string, int> { ["Drama"] = 5, ["Action"] = 1, ["Secret Genre"] = 1, ["Horror"] = 1, ["Unrated Genre"] = 1, ["Splatter"] = 1 }, admin);
        Assert.Equal(new Dictionary<string, int> { ["Drama"] = 3, ["Action"] = 1 }, kid);
        Assert.Equal(["Classic"], Values(await host.OkAsync("GET", "/SmartSearch/Values/Tags", host.Kid)).Keys);
        Assert.Equal(1, Values(await host.OkAsync("GET", "/SmartSearch/Values/Tags", host.Kid))["Classic"]);
        Assert.Equal(["Northwind"], Values(await host.OkAsync("GET", "/SmartSearch/Values/Studios", host.Kid)).Keys);
        Assert.Equal(new[] { "G", "PG" }, Values(await host.OkAsync("GET", "/SmartSearch/Values/OfficialRating", host.Kid)).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "m4v", "mkv", "mp4" }, Values(await host.OkAsync("GET", "/SmartSearch/Values/Container", host.Kid)).Keys.Order(StringComparer.Ordinal));

        // The field id ignores case; the twin (Kid's profile, every library) sees the private movie too.
        Assert.Equal(kid, Values(await host.OkAsync("GET", "/SmartSearch/Values/genres?limit=100", host.Kid)));
        Assert.Equal(new Dictionary<string, int> { ["Drama"] = 4, ["Action"] = 1, ["Secret Genre"] = 1 }, Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Twin)));
    }

    [Fact]
    public async Task Values_InTheLibrariesAsked_AreWithinTheCallers()
    {
        var w = Build();
        await using var host = await new SearchApiHost(w.World).StartAsync();
        var world = w.World;

        Assert.Equal(0, (await host.OkAsync("GET", "/SmartSearch/Values/Genres?libraryIds=" + world.Private.Id, host.Kid)).GetArrayLength());
        Assert.Equal(0, (await host.OkAsync("GET", "/SmartSearch/Values/Genres?libraryIds=" + Guid.NewGuid(), host.Kid)).GetArrayLength());
        Assert.Equal(
            new Dictionary<string, int> { ["Secret Genre"] = 1, ["Drama"] = 1 },
            Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?libraryIds=" + world.Private.Id, host.Admin)));
        Assert.Equal(
            new Dictionary<string, int> { ["Drama"] = 3, ["Action"] = 1 },
            Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?libraryIds=" + world.Movies.Id + "&libraryIds=" + world.Shows.Id + "&libraryIds=" + world.Private.Id, host.Kid)));
        Assert.Equal(
            new Dictionary<string, int> { ["Drama"] = 1 },
            Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?libraryIds=" + world.Shows.Id, host.Kid)));

        // Hidden shares Movies' first folder: the administrator's count of it is that folder's, never twice an item.
        Assert.Equal(
            new Dictionary<string, int> { ["Drama"] = 3, ["Action"] = 1, ["Horror"] = 1, ["Unrated Genre"] = 1, ["Splatter"] = 1 },
            Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?libraryIds=" + world.Hidden.Id + "&libraryIds=" + world.Movies.Id, host.Admin)));
    }

    [Fact]
    public async Task Values_MatchTheTextAnywhere_ThoseThatStartWithItFirst_ThenTheMostCommon()
    {
        var w = Build();
        await using var host = await new SearchApiHost(w.World).StartAsync();

        var drama = await host.OkAsync("GET", "/SmartSearch/Values/Genres?prefix=RA", host.Admin);
        Assert.Equal(["Drama", "Unrated Genre"], drama.EnumerateArray().Select(v => v.GetProperty("Value").GetString()));
        var r = await host.OkAsync("GET", "/SmartSearch/Values/Genres?prefix=r&limit=3", host.Admin);
        Assert.Equal(["Drama", "Horror", "Secret Genre"], r.EnumerateArray().Select(v => v.GetProperty("Value").GetString()));
        var a = await host.OkAsync("GET", "/SmartSearch/Values/Genres?prefix=%C3%A0&limit=2", host.Admin);
        Assert.Equal(["Action", "Drama"], a.EnumerateArray().Select(v => v.GetProperty("Value").GetString()));
        var limited = await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=1", host.Admin);
        Assert.Equal(["Drama"], limited.EnumerateArray().Select(v => v.GetProperty("Value").GetString()));
    }

    [Theory]
    [InlineData("/SmartSearch/Values/Year")]
    [InlineData("/SmartSearch/Values/Path")]
    [InlineData("/SmartSearch/Values/Series")]
    [InlineData("/SmartSearch/Values/Nope")]
    [InlineData("/SmartSearch/Values/Genres?limit=0")]
    [InlineData("/SmartSearch/Values/Genres?limit=101")]
    [InlineData("/SmartSearch/Series?prefix=h")]
    [InlineData("/SmartSearch/Series?prefix=%20h%20")]
    [InlineData("/SmartSearch/Series")]
    [InlineData("/SmartSearch/Series?prefix=he&limit=0")]
    public async Task OutOfRangeParameters_Are400(string path)
    {
        await using var host = await new SearchApiHost().StartAsync();

        var (status, _, text) = await host.SendAsync("GET", path, host.Kid);

        Assert.True(status == HttpStatusCode.BadRequest, path + " " + text);
    }

    [Fact]
    public async Task ALongText_Is400()
    {
        await using var host = await new SearchApiHost().StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("GET", "/SmartSearch/Values/Genres?prefix=" + new string('a', 201), host.Kid)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("GET", "/SmartSearch/Series?prefix=" + new string('a', 201), host.Kid)).Status);
    }

    [Fact]
    public async Task TheValueIndex_IsSharedByAProfile_AndRebuiltAfterAnItemChanges_ButNotDuringAScan()
    {
        var w = Build();
        await using var host = await new SearchApiHost(w.World).StartAsync();
        var world = w.World;

        await host.OkAsync("GET", "/SmartSearch/Values/Genres", host.Kid);
        var kidBuilds = host.Values.Builds;
        Assert.Equal(4, kidBuilds); // Movies' two folders, Shows' and Home's.
        await host.OkAsync("GET", "/SmartSearch/Values/Tags", host.Kid);
        Assert.Equal(kidBuilds, host.Values.Builds);

        // The twin has Kid's profile: only the Private library's folder is new.
        await host.OkAsync("GET", "/SmartSearch/Values/Genres", host.Twin);
        Assert.Equal(kidBuilds + 1, host.Values.Builds);

        // A new genre after an item changes, once the events have settled; a scan serves the last index.
        Add(world, BaseItemKind.Movie, "New", world.Movies, "PG", 10, s => s.Genres = ["Western"]);
        host.ScanRunning = true;
        world.Core.Library.Raise(l => l.ItemAdded += null, world.Core.Library.Object, new ItemChangeEventArgs());
        host.Clock.Advance(ValueIndexService.Debounce);
        Assert.DoesNotContain("Western", Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Kid)).Keys);
        host.ScanRunning = false;
        var built = host.Values.Builds;

        // The scan's changes rebuild each of Kid's folders once, in the background: the stale index is served meanwhile.
        await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Kid);
        for (var i = 0; i < 1000 && host.Values.Builds < built + 4; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(built + 4, host.Values.Builds);
        Assert.Equal(1, Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Kid))["Western"]);
        Assert.Equal(built + 4, host.Values.Builds);
    }

    [Fact]
    public async Task Values_CountOnlyTheItemKindsTheServerAllows()
    {
        var w = Build();
        await using var host = await new SearchApiHost(w.World).StartAsync();

        Assert.Equal(new Dictionary<string, int> { ["Drama"] = 3, ["Action"] = 1 }, Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Kid)));
        host.AllowedKinds = [BaseItemKind.Movie];
        Assert.Equal(new Dictionary<string, int> { ["Drama"] = 2, ["Action"] = 1 }, Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Kid)));
        host.AllowedKinds = [BaseItemKind.Series];
        Assert.Equal(new Dictionary<string, int> { ["Drama"] = 1 }, Values(await host.OkAsync("GET", "/SmartSearch/Values/Genres?limit=100", host.Kid)));
    }

    [Theory]
    [InlineData("/SmartSearch/Values/Genres?limit=abc", "limit")]
    [InlineData("/SmartSearch/Values/Genres?libraryIds=not-a-guid", "libraryIds")]
    [InlineData("/SmartSearch/Series?prefix=he&limit=1.5", "limit")]
    public async Task ParametersThatDontBind_Are400InTheKitsShape(string path, string parameter)
    {
        await using var host = await new SearchApiHost().StartAsync();

        var (status, problem, text) = await host.SendAsync("GET", path, host.Kid);

        Assert.True(status == HttpStatusCode.BadRequest, text);
        Assert.Equal(["title", "status", "detail", "code", "errors"], problem.EnumerateObject().Select(p => p.Name));
        Assert.Equal("InvalidRequest", problem.GetProperty("code").GetString());
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(parameter, error.GetProperty("path").GetString());
        Assert.Equal("InvalidValue", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Series_AreTheCallersOwn_ThoseThatStartWithTheTextFirst()
    {
        var w = Build();
        await using var host = await new SearchApiHost(w.World).StartAsync();

        var kid = await host.OkAsync("GET", "/SmartSearch/Series?prefix=hero", host.Kid);
        var admin = await host.OkAsync("GET", "/SmartSearch/Series?prefix=HERO", host.Admin);

        Assert.Equal(["Heroes", "Héroïnes", "Superheroes"], kid.EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        Assert.Equal(["Id", "Name", "ProductionYear", "LibraryId"], kid[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(2006, kid[0].GetProperty("ProductionYear").GetInt32());
        Assert.Equal(w.Heroes.Id, Guid.Parse(kid[0].GetProperty("Id").GetString()!));
        Assert.Equal(w.World.Shows.Id, Guid.Parse(kid[0].GetProperty("LibraryId").GetString()!));
        Assert.Equal(6, admin.GetArrayLength());
        Assert.DoesNotContain("Dark", kid.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Gory", kid.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Private", kid.GetRawText(), StringComparison.Ordinal);

        // Accents and case are ignored; the limit applies after the order.
        Assert.Equal(["Héroïnes"], (await host.OkAsync("GET", "/SmartSearch/Series?prefix=heroi", host.Kid)).EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        Assert.Equal(["Heroes"], (await host.OkAsync("GET", "/SmartSearch/Series?prefix=hero&limit=1", host.Kid)).EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
        Assert.Equal(["Private Heroes"], (await host.OkAsync("GET", "/SmartSearch/Series?prefix=private", host.Twin)).EnumerateArray().Select(s => s.GetProperty("Name").GetString()));
    }

    private sealed class Fixture(PlannerWorld world)
    {
        public PlannerWorld World { get; } = world;

        public required FakeRow Open { get; init; }

        public required FakeRow OtherFolder { get; init; }

        public required FakeRow Private { get; init; }

        public required FakeRow Rated { get; init; }

        public required FakeRow Unrated { get; init; }

        public required FakeRow Gory { get; init; }

        public required FakeRow Heroes { get; init; }

        public required FakeRow Heroines { get; init; }

        public required FakeRow Superheroes { get; init; }

        public required FakeRow DarkHeroes { get; init; }

        public required FakeRow GoryHeroes { get; init; }

        public required FakeRow PrivateHeroes { get; init; }
    }
}
