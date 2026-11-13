using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The developer demo of the kit's item identity: off by default (403 "turned off"), admin-only, and when on it
/// captures, resolves with each of its three policies, and reads the move journal, writing nothing; ids resolve as the
/// calling admin, who is shown no item they can't see.
/// </summary>
public sealed class IdentityDemoTests : IDisposable
{
    private const string JournalId = "3f2a9c1e7b5d4e6f8a0b1c2d3e4f5a6b";

    private readonly PluginConfiguration _configuration = new() { EnableIdentityDemo = true };
    private readonly FakeLibrary _library = new();
    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero));
    private readonly User _admin = TestData.CreateUser("admin");

    private string JournalFile => _directory.Combine("librarymaintenance", "moves.jsonl");

    [Fact]
    public void IsOffByDefault_AndEveryCallSaysSo()
    {
        Assert.False(new PluginConfiguration().EnableIdentityDemo);
        _configuration.EnableIdentityDemo = false;
        var demo = CreateDemo();

        foreach (var call in new Action[]
        {
            () => demo.Capture(Guid.NewGuid(), _admin),
            () => demo.Resolve(JsonSerializer.SerializeToElement(new { Kind = "Movie" }), null, null, _admin),
            () => demo.Journal(null, 0, _admin)
        })
        {
            var error = Assert.ThrowsAny<PluginException>(call);
            Assert.Equal(PluginErrorKind.FeatureDisabled, error.Kind);
        }

        Assert.Empty(_library.IdLookups);
        Assert.Empty(_library.UserLookups);
    }

    [Fact]
    public void TheController_IsForAdminsOnly()
    {
        var authorize = typeof(BookmarksIdentityDemoController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
    }

    [Fact]
    public void CaptureThenResolve_RelinksTheOtherCopy_OnlyWithAMatchingRuntime()
    {
        var removed = _library.AddMovie("Edition Movie", 2001, TimeSpan.FromSeconds(60), @"C:\media\Movies\Edition Movie (2001) [tmdbid-900301]\Edition Movie (2001).mkv", ("Tmdb", "900301"));
        var extended = _library.AddMovie("Edition Movie Extended", 2001, TimeSpan.FromSeconds(90), null, ("Tmdb", "900301"));
        var demo = CreateDemo();

        var captured = demo.Capture(removed.Id, _admin);
        var identity = (JsonElement)captured["Identity"]!;
        Assert.Equal("Movie", identity.GetProperty("Kind").GetString());
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, identity.GetProperty("RunTimeTicks").GetInt64());
        Assert.Equal("900301", identity.GetProperty("ProviderIds").GetProperty("Tmdb").GetString());
        Assert.Contains("Movie|Tmdb|900301", (IReadOnlyList<string>)captured["StrongKeys"]!);

        _library.Remove(removed, raiseEvent: false);

        var bookmarks = demo.Resolve(identity, "Bookmarks", null, _admin);
        Assert.Equal("Ambiguous", bookmarks["Outcome"]);
        Assert.Equal("FailedValidation", bookmarks["Reason"]);
        var candidate = Assert.Single((List<Dictionary<string, object?>>)bookmarks["Candidates"]!);
        Assert.Equal(extended.Id, candidate["ItemId"]);
        Assert.Equal(false, candidate["PassesValidation"]);

        var byDefault = demo.Resolve(identity, null, null, _admin);
        Assert.Equal("Relink", byDefault["Outcome"]);
        Assert.Equal("Strong", byDefault["Strength"]);
        Assert.Equal(extended.Id, byDefault["ItemId"]);

        Assert.Equal("Ambiguous", demo.Resolve(identity, "ExactOnly", null, _admin)["Outcome"]);
        Assert.Equal("Missing", byDefault["JournalStatus"]);
    }

    [Fact]
    public void Resolve_FollowsTheJournal()
    {
        var moved = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var oldId = Guid.NewGuid();
        WriteJournal(oldId, moved.Id);

        var snapshot = JsonSerializer.SerializeToElement(new { ItemId = oldId, Kind = "Movie", Name = "Heat" });
        var demo = CreateDemo();

        var result = demo.Resolve(snapshot, "Bookmarks", null, _admin);

        Assert.Equal("Relink", result["Outcome"]);
        Assert.Equal("Exact", result["Strength"]);
        Assert.Equal("Journal", result["Source"]);
        Assert.Equal(moved.Id, result["ItemId"]);
        var chain = (Dictionary<string, object?>)result["Chain"]!;
        Assert.Equal(moved.Id, chain["EndId"]);
        Assert.Single((List<Dictionary<string, object?>>)chain["Steps"]!);

        // A record attached after the line was committed (12:00) doesn't follow it.
        var attachedLater = demo.Resolve(snapshot, "Bookmarks", new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc), _admin);
        Assert.Equal("Unmatched", attachedLater["Outcome"]);
        Assert.Null(attachedLater["Chain"]);
    }

    [Fact]
    public void AnAdminWithALibraryRole_IsShownNoItemOutsideTheirLibraries()
    {
        // Admins have no library bypass. The same film in Movies and in Movies 2, which this admin can't open.
        var visible = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"C:\media\Movies\Heat (1995)\Heat (1995).mkv", ("Tmdb", "949"));
        var hidden = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"C:\media\Movies 2\Heat (1995)\Heat (1995).mkv", ("Tmdb", "949"));
        _library.Hide(_admin, hidden);
        var demo = CreateDemo();

        // Capture: "not visible" is the same 404 as "not found"; an API key sees the whole server.
        Assert.Equal(PluginErrorKind.NotFound, Assert.ThrowsAny<PluginException>(() => demo.Capture(hidden.Id, _admin)).Kind);
        Assert.Equal("Movie", ((JsonElement)demo.Capture(hidden.Id, null)["Identity"]!).GetProperty("Kind").GetString());

        // Resolve: the hidden copy is neither a candidate nor counted, so the admin's record relinks to the visible one.
        var gone = JsonSerializer.SerializeToElement(new { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Heat", Year = 1995, ProviderIds = new { Tmdb = "949" } });
        var asAdmin = demo.Resolve(gone, null, null, _admin);
        Assert.Equal("Relink", asAdmin["Outcome"]);
        Assert.Equal(visible.Id, asAdmin["ItemId"]);
        Assert.Equal(visible.Id, Assert.Single((List<Dictionary<string, object?>>)asAdmin["Candidates"]!)["ItemId"]);
        var asApiKey = demo.Resolve(gone, null, null, null);
        Assert.Equal("Ambiguous", asApiKey["Outcome"]);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)asApiKey["Candidates"]!).Count);

        // A snapshot of the hidden item itself: not found by its id or path, and nothing else to offer.
        var itself = demo.Resolve(JsonSerializer.SerializeToElement(new { ItemId = hidden.Id, Kind = "Movie", Name = "Other", Path = hidden.Path }), null, null, _admin);
        Assert.Equal("Unmatched", itself["Outcome"]);
        Assert.Null(itself["ItemName"]);
        Assert.Null(itself["ItemPath"]);
    }

    [Fact]
    public void Journal_LeavesOutTheLinesThatNameAnItemTheAdminCantSee()
    {
        var hidden = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"C:\media\Movies 2\Heat (1995)\Heat (1995).mkv");
        var shown = _library.AddMovie("Ronin", 1998, TimeSpan.FromMinutes(122), @"C:\media\Movies\Ronin (1998)\Ronin (1998).mkv");
        _library.Hide(_admin, hidden);
        var predicted = Guid.NewGuid();
        WriteJournal((Guid.NewGuid(), hidden.Id, hidden.Path), (Guid.NewGuid(), shown.Id, shown.Path), (Guid.NewGuid(), predicted, @"C:\media\Movies\New\New.mkv"));
        var demo = CreateDemo();

        var asAdmin = demo.Journal(null, 0, _admin);
        var lines = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)asAdmin["CatchUp"]!)["Lines"]!;
        Assert.Equal(new object?[] { shown.Id, predicted }, lines.Select(l => l["NewId"]));
        Assert.Equal(shown.Path, lines[0]["NewPath"]);
        Assert.Null(lines[1]["NewPath"]);
        var pending = (List<Dictionary<string, object?>>)asAdmin["PendingRelinks"]!;
        Assert.Equal(new object?[] { shown.Id, predicted }, pending.Select(p => p["NewId"]));
        Assert.Equal(new object?[] { true, false }, pending.Select(p => p["InLibrary"]));
        Assert.Equal(3, asAdmin["Lines"]);

        var asApiKey = demo.Journal(null, 0, null);
        var all = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)asApiKey["CatchUp"]!)["Lines"]!;
        Assert.Equal(new object?[] { hidden.Id, shown.Id, predicted }, all.Select(l => l["NewId"]));
        Assert.Equal(hidden.Path, all[0]["NewPath"]);
        Assert.Equal(@"C:\media\Movies\New\New.mkv", all[2]["NewPath"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheController_PassesTheSignedInAdmin_OrNoOneForAnApiKey(bool apiKey)
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(_admin.Id)).Returns(_admin);
        var demo = new Mock<IBookmarkIdentityDemo>();
        var controller = new BookmarksIdentityDemoController(demo.Object, users.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = JellyfinAuth.CreatePrincipal(apiKey ? Guid.Empty : _admin.Id, isAdministrator: !apiKey, isApiKey: apiKey) }
            }
        };
        var expected = apiKey ? null : _admin;
        var body = JsonSerializer.SerializeToElement(new { Kind = "Movie" });
        var when = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

        controller.GetBookmarksIdentityDemoItem(_admin.Id);
        controller.ResolveBookmarksIdentityDemo(body, "Bookmarks", when);
        controller.GetBookmarksIdentityDemoJournal(null, 3);

        demo.Verify(d => d.Capture(_admin.Id, expected), Times.Once);
        demo.Verify(d => d.Resolve(body, "Bookmarks", when, expected), Times.Once);
        demo.Verify(d => d.Journal(null, 3, expected), Times.Once);
    }

    [Fact]
    public void Journal_DescribesTheCatchUpAndThePendingRelinks()
    {
        var oldId = Guid.NewGuid();
        var predicted = Guid.NewGuid();
        WriteJournal(oldId, predicted);
        var demo = CreateDemo();

        var first = demo.Journal(null, 0, _admin);
        Assert.Equal("Ok", first["Status"]);
        Assert.Equal(Guid.Parse(JournalId), first["JournalId"]);
        Assert.Equal(1L, first["LastSeq"]);
        var catchUp = (Dictionary<string, object?>)first["CatchUp"]!;
        Assert.Equal("None", catchUp["Reset"]);
        Assert.Single((List<Dictionary<string, object?>>)catchUp["Lines"]!);
        var pending = Assert.Single((List<Dictionary<string, object?>>)first["PendingRelinks"]!);
        Assert.Equal(predicted, pending["NewId"]);
        Assert.Equal(false, pending["InLibrary"]);

        Assert.Equal("NewJournalId", ((Dictionary<string, object?>)demo.Journal(Guid.NewGuid(), 1, _admin)["CatchUp"]!)["Reset"]);
        Assert.Equal("SeqBelowStored", ((Dictionary<string, object?>)demo.Journal(Guid.Parse(JournalId), 9, _admin)["CatchUp"]!)["Reset"]);

        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Empty((List<Dictionary<string, object?>>)demo.Journal(null, 0, _admin)["PendingRelinks"]!);
    }

    [Fact]
    public void BadInput_IsRefused()
    {
        var demo = CreateDemo();

        Assert.Equal(PluginErrorKind.Invalid, Assert.ThrowsAny<PluginException>(() => demo.Capture(Guid.Empty, _admin)).Kind);
        Assert.Equal(PluginErrorKind.NotFound, Assert.ThrowsAny<PluginException>(() => demo.Capture(Guid.NewGuid(), _admin)).Kind);
        Assert.Equal(PluginErrorKind.Invalid, Assert.ThrowsAny<PluginException>(() => demo.Resolve(JsonSerializer.SerializeToElement(new { Kind = "Movie" }), "Sometimes", null, _admin)).Kind);
        Assert.Equal(PluginErrorKind.Invalid, Assert.ThrowsAny<PluginException>(() => demo.Resolve(JsonSerializer.SerializeToElement(new[] { 1 }), null, null, _admin)).Kind);
        Assert.Equal(PluginErrorKind.Invalid, Assert.ThrowsAny<PluginException>(() => demo.Resolve(JsonSerializer.SerializeToElement(new { ItemId = "not a guid" }), null, null, _admin)).Kind);
        Assert.Equal(PluginErrorKind.Invalid, Assert.ThrowsAny<PluginException>(() => demo.Journal(null, -1, _admin)).Kind);
    }

    [Fact]
    public void TheRegistrator_WiresTheDemoToTheKitsIdentityServices()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory.Path);
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new ListLogger());
        var configuration = new Mock<IPluginConfigurationProvider>();
        configuration.Setup(c => c.GetConfiguration()).Returns(_configuration);
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        services.AddSingleton(paths.Object).AddSingleton(_library.Object).AddSingleton(loggerFactory.Object).AddSingleton(configuration.Object);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(JournalFile, provider.GetRequiredService<MoveJournalReader>().FilePath);
        Assert.Equal("Missing", provider.GetRequiredService<IBookmarkIdentityDemo>().Journal(null, 0, null)["Status"]);
    }

    public void Dispose() => _directory.Dispose();

    private BookmarkIdentityDemo CreateDemo()
    {
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(_configuration);
        var moves = new MoveJournalReader(JournalFile, new ListLogger());
        return new BookmarkIdentityDemo(provider.Object, _library.Object, new IdentityResolver(_library.Object, moves, _clock), moves, _clock);
    }

    private void WriteJournal(Guid oldId, Guid newId) => WriteJournal((oldId, newId, null));

    private void WriteJournal(params (Guid OldId, Guid NewId, string? NewPath)[] moves)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalFile)!);
        File.WriteAllText(
            JournalFile,
            "{\"V\":1,\"Header\":true,\"JournalId\":\"" + JournalId + "\",\"CreatedUtc\":\"2026-10-01T12:00:00Z\"}\n"
            + string.Concat(moves.Select((move, i) => "{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":" + (i + 1) + ",\"Status\":\"Committed\",\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\""
                + move.OldId.ToString("N") + "\",\"NewId\":\"" + move.NewId.ToString("N") + "\",\"Kind\":\"Movie\""
                + (move.NewPath is null ? string.Empty : ",\"NewPath\":" + JsonSerializer.Serialize(move.NewPath)) + "}\n")));
    }
}
