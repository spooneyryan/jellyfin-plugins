using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Settings;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Api;

/// <summary>
/// The settings endpoints through the registrator's services: <c>GET</c> and <c>POST /Admin/Settings</c> (the
/// fields, the libraries offered, a dry run by default, the version check, each value a save refuses, the preview of a
/// change to the index's scope and its counts hidden from a limited administrator), and <c>GET /Admin/Export</c> with
/// <c>POST /Admin/Import</c> (the acceptance: the settings round-trip; a lenient import's warnings; another plugin's file,
/// a newer schema).
/// </summary>
public sealed class SettingsApiTests
{
    private const string Settings = "/LibraryMaintenance/Admin/Settings";
    private const string Export = "/LibraryMaintenance/Admin/Export";
    private const string Import = "/LibraryMaintenance/Admin/Import";
    private const string ServerId = "5e7f0000000000000000000000000001";

    // Alice can open every library; Bob, also an administrator, only Movies; Carol, an administrator, every library, with
    // a parental limit.
    private static readonly Guid Alice = new("e0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = new("e0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Carol = new("e0000000-0000-0000-0000-00000000000c");

    [Fact]
    public async Task TheSettingsRoutes_AnswerAdministratorsOnly()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        foreach (var (method, path) in new[] { (HttpMethod.Get, Settings), (HttpMethod.Post, Settings), (HttpMethod.Get, Export), (HttpMethod.Post, Import) })
        {
            using var anonymous = await host.SendAsync(method, path, Body("{}"));
            using var user = await host.SendAsync(method, path, r => { As("user", Alice)(r); Body("{}")(r); });
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        }

        Assert.Empty(library.IdQueries);
    }

    [Fact]
    public async Task GetSettings_GivesEveryField_TheVersion_TheMovieLibraries_TheLanguages_AndTheDefaultPhrases()
    {
        var library = Library();
        var gone = Guid.NewGuid();
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = [library.Libraries[0].Id, gone], ExcludedPathPrefixes = [@"D:\Clips"] };
        await using var host = await StartAsync(library);

        var body = await Get(host, Alice);
        var settings = body.GetProperty("Settings");
        Assert.True(settings.GetProperty("DuplicatesEnabled").GetBoolean());
        Assert.Equal("TitleAndYear", settings.GetProperty("DefaultMatchMode").GetString());
        Assert.Equal("MatchUnknownOnly", settings.GetProperty("UnknownYearPolicy").GetString());
        Assert.Equal([library.Libraries[0].Id, gone], settings.GetProperty("IncludedLibraryIds").EnumerateArray().Select(Id));
        Assert.Equal([@"D:\Clips"], Strings(settings, "ExcludedPathPrefixes"));
        Assert.Equal(["En"], Strings(settings, "ArticleLanguages"));
        Assert.Equal(5, settings.GetProperty("RequestWaitSeconds").GetInt32());
        // The web client's and Move's switches, on by default.
        Assert.All(new[] { "EnableWebUi", "MoveEnabled", "ShowInItemMenu", "ReportWebAnchorProblems" }, name => Assert.True(settings.GetProperty(name).GetBoolean(), name));
        Assert.Equal(SettingsRules.Version(library.Configuration), body.GetProperty("Version").GetString());
        Assert.Matches("^[0-9a-f]{16}$", body.GetProperty("Version").GetString());

        // The movie and mixed libraries in the server's order (Shows, a tvshows library, isn't one), then the included id
        // that is no longer a library.
        var libraries = body.GetProperty("Libraries").EnumerateArray().ToList();
        Assert.Equal(["Movies", "More Movies", "Kids", string.Empty], libraries.Select(l => l.GetProperty("Name").GetString()));
        Assert.Equal(["movies", "mixed", "movies", string.Empty], libraries.Select(l => l.GetProperty("Kind").GetString()));
        Assert.Equal([true, false, false, true], libraries.Select(l => l.GetProperty("Included").GetBoolean()));
        Assert.Equal([false, false, false, true], libraries.Select(l => l.GetProperty("Missing").GetBoolean()));
        Assert.Equal(gone, Id(libraries[3].GetProperty("Id")));

        Assert.Equal(["En", "Fr", "De", "Es", "It", "Nl"], body.GetProperty("ArticlesByLanguage").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["the", "a", "an"], Strings(body.GetProperty("ArticlesByLanguage"), "En"));
        Assert.Contains("director's cut", Strings(body, "DefaultEditionPhrases"));
    }

    [Fact]
    public async Task ASave_IsADryRunByDefault_ThenSavesWithItsVersion_AndAStaleVersionIsRefused()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        var before = SettingsRules.Version(library.Configuration);

        // A title rule and the default mode: no scope change, so no preview; the dry run saves nothing.
        var change = """{ "DefaultMatchMode": "title", "ArticleLanguages": ["En", "de"], "RequestWaitSeconds": 10 }""";
        var dry = await Post(host, Settings + "?version=" + before, change);
        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        Assert.False(dry.GetProperty("Applied").GetBoolean());
        Assert.False(dry.GetProperty("ScopeChanged").GetBoolean());
        Assert.False(dry.GetProperty("RebuildsIndex").GetBoolean());
        Assert.True(dry.GetProperty("TitleRulesChanged").GetBoolean());
        Assert.True(Absent(dry, "Preview"));
        Assert.Equal(before, dry.GetProperty("Version").GetString());
        Assert.Equal(
            ["DefaultMatchMode: Title + Year -> Title only", "ArticleLanguages: En -> En, De", "RequestWaitSeconds: 5 s -> 10 s"],
            dry.GetProperty("Changes").EnumerateArray().Select(c => c.GetProperty("Field").GetString() + ": " + c.GetProperty("From").GetString() + " -> " + c.GetProperty("To").GetString()));
        Assert.Equal("Title", dry.GetProperty("Settings").GetProperty("DefaultMatchMode").GetString());
        Assert.Equal("TitleAndYear", library.Configuration.DefaultMatchMode);
        Assert.Equal(before, SettingsRules.Version(library.Configuration));

        // Saved: the values in this version's spelling, a new version, and no build (the scope is the same).
        var saved = await Post(host, Settings + "?dryRun=false&version=" + before, change);
        Assert.True(saved.GetProperty("Applied").GetBoolean());
        Assert.Equal("Title", library.Configuration.DefaultMatchMode);
        Assert.Equal(["En", "De"], library.Configuration.ArticleLanguages);
        Assert.Equal(10, library.Configuration.RequestWaitSeconds);
        var after = saved.GetProperty("Version").GetString();
        Assert.NotEqual(before, after);
        Assert.Equal(SettingsRules.Version(library.Configuration), after);
        Assert.Empty(library.IdQueries);

        // The same change again changes nothing; the old version is now stale (409), the new one isn't.
        using var stale = await host.SendAsync(HttpMethod.Post, Settings + "?dryRun=false&version=" + before, r => { As("admin", Alice)(r); Body(change)(r); });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = await Json(stale);
        Assert.Contains("saved since you opened them", staleBody.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("SettingsChanged", staleBody.GetProperty("code").GetString());
        var again = await Post(host, Settings + "?dryRun=false&version=" + after, change);
        Assert.Empty(again.GetProperty("Changes").EnumerateArray());
        Assert.False(again.GetProperty("Applied").GetBoolean());
    }

    [Fact]
    public async Task ASaveCheckedWhileAnotherIsInItsPreview_WaitsForIt_AndIsRefused()
    {
        // The first save changes the scope, so its preview builds an index between its version check and its save; it is
        // held there. A second save from the same version (no preview) waits for it, then gets 409: it isn't applied and
        // then overwritten by the first.
        var library = Library();
        await using var host = await StartAsync(library);
        var version = (await Get(host, Alice)).GetProperty("Version").GetString();
        using var gate = new ManualResetEventSlim(false);
        var previewing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.BeforeItemIds = _ =>
        {
            previewing.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(10));
        };

        var first = host.SendAsync(HttpMethod.Post, Settings + "?dryRun=false&version=" + version, r => { As("admin", Alice)(r); Body(JsonSerializer.Serialize(new { ExcludedPathPrefixes = new[] { @"D:\Clips" } }))(r); });
        await previewing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = host.SendAsync(HttpMethod.Post, Settings + "?dryRun=false&version=" + version, r => { As("admin", Bob)(r); Body("""{ "RequestWaitSeconds": 9 }""")(r); });
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        gate.Set();

        using var applied = await first;
        using var refused = await second;
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal([@"D:\Clips"], library.Configuration.ExcludedPathPrefixes);
        Assert.Equal(5, library.Configuration.RequestWaitSeconds);
    }


    [Fact]
    public async Task APreview_NeverOpensANetworkFolder_AndChecksTheLocalOnesAllAtOnce()
    {
        // The third review: a UNC path is never looked for (nothing connects to a server before the save); the local
        // folders are checked at the same time, each check here waiting until all three have started.
        var library = Library();
        await using var host = await StartAsync(library);
        var service = host.Services.GetRequiredService<SettingsService>();
        var asked = new List<string>();
        using var started = new CountdownEvent(3);
        var together = 0;
        service.FolderExists = path =>
        {
            lock (asked)
            {
                asked.Add(path);
            }

            started.Signal();
            if (started.Wait(TimeSpan.FromSeconds(4)))
            {
                Interlocked.Increment(ref together);
            }

            return path.EndsWith("Clips", StringComparison.Ordinal);
        };

        var dry = await Post(host, Settings, JsonSerializer.Serialize(new
        {
            ExcludedPathPrefixes = new[] { @"\\nas\movies\Trailers", "//nas/movies/Extras", @"D:\Clips", @"D:\Extras", @"E:\Samples" }
        }));
        var folders = dry.GetProperty("Preview").GetProperty("Folders").EnumerateArray().ToList();
        Assert.Equal([@"D:\Clips", @"D:\Extras", @"E:\Samples"], asked.Order(StringComparer.Ordinal));
        Assert.Equal(3, together);
        Assert.Equal(
            [@"\\nas\movies\Trailers - Network", "//nas/movies/Extras - Network", @"D:\Clips True -", @"D:\Extras False -", @"E:\Samples False -"],
            folders.Select(f => f.GetProperty("Path").GetString() + " " + (Absent(f, "Exists") ? "-" : (f.GetProperty("Exists").GetBoolean() ? "True" : "False")) + " " + (Absent(f, "NotChecked") ? "-" : f.GetProperty("NotChecked").GetString())));
    }

    [Fact]
    public async Task APreview_DoesntWaitPastItsCap_ForAFolderThatDoesntAnswer()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        var service = host.Services.GetRequiredService<SettingsService>();
        using var gate = new ManualResetEventSlim(false);
        service.FolderCheckTimeout = TimeSpan.FromMilliseconds(300);
        service.FolderExists = path => path == @"D:\Slow" ? gate.Wait(TimeSpan.FromSeconds(10)) : true;
        try
        {
            var dry = await Post(host, Settings, JsonSerializer.Serialize(new { ExcludedPathPrefixes = new[] { @"D:\Slow", @"D:\Quick" } }));
            Assert.Equal(
                [@"D:\Slow - NoAnswer", @"D:\Quick True -"],
                dry.GetProperty("Preview").GetProperty("Folders").EnumerateArray().Select(f => f.GetProperty("Path").GetString() + " " + (Absent(f, "Exists") ? "-" : (f.GetProperty("Exists").GetBoolean() ? "True" : "False")) + " " + (Absent(f, "NotChecked") ? "-" : f.GetProperty("NotChecked").GetString())));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task ADevicePath_IsRefusedBySave_AndLeftOutByAnImport()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        var service = host.Services.GetRequiredService<SettingsService>();
        var asked = new List<string>();
        service.FolderExists = path =>
        {
            lock (asked)
            {
                asked.Add(path);
            }

            return true;
        };

        // The NT object prefix \??\ (Win32 hands it to the object manager as it is, so \??\UNC\host\share
        // is a share), its slash spellings, and GLOBALROOT, as well as \\?\ and \\.\.
        var devices = new[]
        {
            @"\\?\C:\Clips", @"\\.\pipe\clips", "//?/C:/Clips", @"\\?\UNC\nas\movies", @"\\?\GLOBALROOT\Device\Mup\nas\movies", @"\\?",
            @"\??\UNC\x\y", @"\??\C:\x", @"\??\GLOBALROOT\Device\Mup\nas\movies", "/??/C:/x", @"\??/C:/x", @"  \??\UNC\x\y",
            // A ? as the second character (since 1.0.2): one slash, which .NET takes for a
            // full path and Win32 resolves to C:\.
            @"\?\C:\x", @"\?\UNC\host\share", "/?/C:/x"
        };
        foreach (var device in devices)
        {
            foreach (var dryRun in new[] { true, false })
            {
                using var refused = await host.SendAsync(HttpMethod.Post, Settings + "?dryRun=" + (dryRun ? "true" : "false") + "&version=" + SettingsRules.Version(library.Configuration), r => { As("admin", Alice)(r); Body(JsonSerializer.Serialize(new { ExcludedPathPrefixes = new[] { device } }))(r); });
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Equal($"ExcludedPathPrefixes: \"{device.Trim()}\" is a device path, not a folder path (like D:\\Clips).", (await Json(refused)).GetProperty("detail").GetString());
            }
        }

        Assert.Empty(library.Configuration.ExcludedPathPrefixes ?? []);
        Assert.Empty(asked);

        // The negative control: a network folder and a local one are kept by the same dry run, and only the local one
        // is looked for.
        var kept = await Post(host, Settings, JsonSerializer.Serialize(new { ExcludedPathPrefixes = new[] { @"\\nas\movies", @"D:\Kept" } }));
        Assert.Equal([@"\\nas\movies", @"D:\Kept"], Strings(kept.GetProperty("Settings"), "ExcludedPathPrefixes"));
        Assert.Equal([@"D:\Kept"], asked);
        asked.Clear();

        using var exported = await host.SendAsync(HttpMethod.Get, Export, As("admin", Alice));
        var file = JsonNode.Parse(await exported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        file["Settings"]!["ExcludedPathPrefixes"] = new JsonArray(@"\\?\C:\Clips", @"\??\UNC\evil\share", @"\??\C:\x", "/??/C:/x", @"\?\C:\x", @"D:\Clips");
        var report = await Post(host, Import, file.ToJsonString());
        Assert.Equal([@"D:\Clips"], Strings(report.GetProperty("Settings"), "ExcludedPathPrefixes"));
        foreach (var device in new[] { @"\\?\C:\Clips", @"\??\UNC\evil\share", @"\??\C:\x", "/??/C:/x", @"\?\C:\x" })
        {
            Assert.Contains($"The excluded folder \"{device}\" is a device path, not a folder path (like D:\\Clips), so it is left out.", Strings(report, "Warnings"));
        }

        Assert.Equal([@"D:\Clips"], asked);
    }

    [Fact]
    public async Task APreview_NeverOpensADevicePath_TheSavedSettingsAlreadyHold()
    {
        // The 1.0 verifier: the settings refuse a new device path, but one already saved (before that rule, or through
        // Jellyfin's own configuration route) reached Directory.Exists when a preview removed it, and \\?\UNC\host\share
        // made an SMB connection to the host. It is "not checked" like a network folder, and never opened.
        var library = Library();
        library.Configuration = new PluginConfiguration
        {
            // \??\ (the NT object prefix) too, and a saved folder isn't trimmed, so its form is read
            // trimmed; the negative controls are D:\Old, looked for, and \\nas\old, a network folder.
            ExcludedPathPrefixes =
            [
                @"\\?\UNC\nas\movies", @"\\.\pipe\clips", "//?/C:/Clips", @"\??\UNC\nas\movies", @"\??\C:\Clips",
                "/??/C:/Other", @"\\?\GLOBALROOT\Device\Mup\nas\movies", @"  \??\UNC\nas\padded", @"D:\Old", @"\\nas\old"
            ]
        };
        await using var host = await StartAsync(library);
        var service = host.Services.GetRequiredService<SettingsService>();
        var asked = new List<string>();
        service.FolderExists = path =>
        {
            lock (asked)
            {
                asked.Add(path);
            }

            return true;
        };

        var dry = await Post(host, Settings, JsonSerializer.Serialize(new { ExcludedPathPrefixes = new[] { @"D:\New" } }));
        Assert.Equal([@"D:\New", @"D:\Old"], asked.Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                @"D:\New True -", @"\\?\UNC\nas\movies - Device", @"\\.\pipe\clips - Device", "//?/C:/Clips - Device",
                @"\??\UNC\nas\movies - Device", @"\??\C:\Clips - Device", "/??/C:/Other - Device",
                @"\\?\GLOBALROOT\Device\Mup\nas\movies - Device", @"  \??\UNC\nas\padded - Device", @"D:\Old True -", @"\\nas\old - Network"
            ],
            dry.GetProperty("Preview").GetProperty("Folders").EnumerateArray().Select(f => f.GetProperty("Path").GetString() + " " + (Absent(f, "Exists") ? "-" : (f.GetProperty("Exists").GetBoolean() ? "True" : "False")) + " " + (Absent(f, "NotChecked") ? "-" : f.GetProperty("NotChecked").GetString())));
    }


    [Theory]
    [InlineData("""{ "RequestWaitSeconds": 31 }""", "RequestWaitSeconds must be a whole number from 0 to 30.")]
    [InlineData("""{ "RequestWaitSeconds": -1 }""", "RequestWaitSeconds must be a whole number from 0 to 30.")]
    [InlineData("""{ "DefaultMatchMode": "Fuzzy" }""", "DefaultMatchMode must be TitleAndYear or Title.")]
    [InlineData("""{ "UnknownYearPolicy": "Never" }""", "UnknownYearPolicy must be MatchUnknownOnly or MatchAny.")]
    [InlineData("""{ "ExcludedPathPrefixes": ["Clips"] }""", "ExcludedPathPrefixes: \"Clips\" isn't a full folder path on this server (like D:\\Clips).")]
    [InlineData("""{ "IncludedLibraryIds": ["c0000000-0000-0000-0000-000000000004"] }""", "IncludedLibraryIds: c0000000000000000000000000000004 isn't a movie or mixed library on this server.")]
    [InlineData("""{ "ArticleLanguages": ["Xx"] }""", "ArticleLanguages: \"Xx\" isn't a language this version knows (En, Fr, De, Es, It, Nl).")]
    public async Task ASave_RefusesAValueItCantUse_SayingWhich_AndSavesNothing(string change, string message)
    {
        var library = Library();
        await using var host = await StartAsync(library);
        var before = SettingsRules.Version(library.Configuration);

        using var response = await host.SendAsync(HttpMethod.Post, Settings + "?dryRun=false&version=" + before, r => { As("admin", Alice)(r); Body(change)(r); });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, (await Json(response)).GetProperty("detail").GetString());
        Assert.Equal(before, SettingsRules.Version(library.Configuration));
    }

    [Fact]
    public async Task AValueANewerBuildSaved_IsKeptByASaveThatLeavesItAlone_AndNeverWritten()
    {
        var library = Library();
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, DefaultMatchMode = "Fuzzy", ArticleLanguages = ["En", "Pt"] };
        await using var host = await StartAsync(library);

        // The tab sends the values back as it read them: kept.
        var saved = await Post(host, Settings + "?dryRun=false&version=" + SettingsRules.Version(library.Configuration), """{ "DefaultMatchMode": "Fuzzy", "ArticleLanguages": ["En", "Pt"], "WarmIndexAfterScan": false }""");
        Assert.Equal(["WarmIndexAfterScan"], saved.GetProperty("Changes").EnumerateArray().Select(c => c.GetProperty("Field").GetString()));
        Assert.Equal("Fuzzy", library.Configuration.DefaultMatchMode);
        Assert.Equal(["En", "Pt"], library.Configuration.ArticleLanguages);

        // Changed to a value this version knows, it can't go back.
        await Post(host, Settings + "?dryRun=false&version=" + SettingsRules.Version(library.Configuration), """{ "DefaultMatchMode": "Title" }""");
        using var back = await host.SendAsync(HttpMethod.Post, Settings, r => { As("admin", Alice)(r); Body("""{ "DefaultMatchMode": "Fuzzy" }""")(r); });
        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
    }

    [Fact]
    public async Task AScopeChange_IsPreviewed_WithTheMoviesBeforeAndAfter_AndBuildsTheIndexOnlyWhenSaved()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        var index = host.Services.GetRequiredService<MovieIndexService>();
        await index.Rebuild();
        var version = index.Current!.Version;
        Assert.Equal(4, index.Current.Stats.Movies);

        // Kids joins (one movie), and Alien's folder is excluded (two movies leave).
        using var folder = new TempFolder();
        var kids = library.Libraries[2].Id;
        var change = JsonSerializer.Serialize(new
        {
            IncludedLibraryIds = new[] { library.Libraries[0].Id, library.Libraries[1].Id, kids },
            ExcludedPathPrefixes = new[] { @"C:\media\Movies\Alien (1979)", folder.Path }
        });
        var queries = library.IdQueries.Count;
        var dry = await Post(host, Settings, change);
        Assert.True(dry.GetProperty("ScopeChanged").GetBoolean());
        Assert.True(dry.GetProperty("RebuildsIndex").GetBoolean());
        var preview = dry.GetProperty("Preview");
        Assert.True(preview.GetProperty("Counted").GetBoolean());
        Assert.True(Absent(preview, "Note"));
        Assert.Equal((4, 3), (preview.GetProperty("MoviesBefore").GetInt32(), preview.GetProperty("MoviesAfter").GetInt32()));
        Assert.Equal((0, 2), (preview.GetProperty("ExcludedBefore").GetInt32(), preview.GetProperty("ExcludedAfter").GetInt32()));
        Assert.Equal(
            ["Movies Stays 3 1", "More Movies Stays 1 1", "Kids Joins - 1"],
            preview.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString() + " " + l.GetProperty("Change").GetString() + " " + Count(l, "MoviesBefore") + " " + Count(l, "MoviesAfter")));
        Assert.Equal(
            [@"C:\media\Movies\Alien (1979) Added False", folder.Path + " Added True"],
            preview.GetProperty("Folders").EnumerateArray().Select(f => f.GetProperty("Path").GetString() + " " + f.GetProperty("Change").GetString() + " " + f.GetProperty("Exists").GetBoolean()));

        // The preview read the library (three libraries' ids) and changed nothing: the same index, the same settings.
        Assert.Equal(queries + 3, library.IdQueries.Count);
        Assert.Equal(version, index.Current.Version);
        Assert.Equal(2, library.Configuration.IncludedLibraryIds.Length);

        // Saved, the index is built again with the new scope.
        var saved = await Post(host, Settings + "?dryRun=false&version=" + dry.GetProperty("Version").GetString(), change);
        Assert.True(saved.GetProperty("Applied").GetBoolean());
        await WaitFor(() => index.Current!.Version > version);
        Assert.Equal((3, 2), (index.Current!.Stats.Movies, index.Current.Stats.Excluded));
        Assert.Equal(3, index.Current.Libraries.Count);

        // Removing the folders and Kids again: Kids leaves, the folders are removed, two movies come back.
        var undo = await Post(host, Settings, JsonSerializer.Serialize(new { IncludedLibraryIds = new[] { library.Libraries[0].Id, library.Libraries[1].Id }, ExcludedPathPrefixes = Array.Empty<string>() }));
        var back = undo.GetProperty("Preview");
        Assert.Equal((3, 4), (back.GetProperty("MoviesBefore").GetInt32(), back.GetProperty("MoviesAfter").GetInt32()));
        Assert.Equal(["Movies Stays", "More Movies Stays", "Kids Leaves"], back.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString() + " " + l.GetProperty("Change").GetString()));
        Assert.Equal(["Removed", "Removed"], back.GetProperty("Folders").EnumerateArray().Select(f => f.GetProperty("Change").GetString()));
    }

    [Fact]
    public async Task ALimitedAdministrator_GetsThePreviewsLibrariesAndFolders_WithoutTheCounts_AndNoPreviewBuild()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild();

        var queries = library.IdQueries.Count;
        var dry = await Post(host, Settings, JsonSerializer.Serialize(new { IncludedLibraryIds = new[] { library.Libraries[0].Id } }), Bob);
        var preview = dry.GetProperty("Preview");
        Assert.False(preview.GetProperty("Counted").GetBoolean());
        Assert.Contains("aren't counted", preview.GetProperty("Note").GetString(), StringComparison.Ordinal);
        foreach (var field in new[] { "MoviesBefore", "MoviesAfter", "ExcludedBefore", "ExcludedAfter" })
        {
            Assert.True(Absent(preview, field), field);
        }

        Assert.Equal(["Movies Stays - -", "More Movies Leaves - -"], preview.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString() + " " + l.GetProperty("Change").GetString() + " " + Count(l, "MoviesBefore") + " " + Count(l, "MoviesAfter")));
        Assert.Equal(queries, library.IdQueries.Count);
    }

    [Fact]
    public async Task AnAdministratorWithAParentalLimit_WhoOpensEveryLibrary_GetsThePreviewWithoutTheCounts()
    {
        // Every library is hers, but her parental limit hides some of their movies, so the
        // preview's numbers (every movie's) aren't hers to see either.
        var library = Library();
        await using var host = await StartAsync(library);
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild();
        var change = JsonSerializer.Serialize(new { IncludedLibraryIds = new[] { library.Libraries[0].Id } });

        var queries = library.IdQueries.Count;
        var preview = (await Post(host, Settings, change, Carol)).GetProperty("Preview");
        Assert.False(preview.GetProperty("Counted").GetBoolean());
        Assert.Contains("by a parental rating or tags", preview.GetProperty("Note").GetString(), StringComparison.Ordinal);
        foreach (var field in new[] { "MoviesBefore", "MoviesAfter", "ExcludedBefore", "ExcludedAfter" })
        {
            Assert.True(Absent(preview, field), field);
        }

        Assert.Equal(["Movies Stays - -", "More Movies Leaves - -"], preview.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString() + " " + l.GetProperty("Change").GetString() + " " + Count(l, "MoviesBefore") + " " + Count(l, "MoviesAfter")));
        Assert.Equal(queries, library.IdQueries.Count);

        // The same change, for Alice (no limit), is counted.
        Assert.True((await Post(host, Settings, change)).GetProperty("Preview").GetProperty("Counted").GetBoolean());
    }

    [Fact]
    public async Task TheSettings_RoundTrip_ThroughAnExportAndAnImport()
    {
        // The round trip: export, change every field of the tab (the ten 1.0 fields and, since 1.1.0.5, the
        // web client's and Move's four switches), import the file: the settings are the exported ones.
        var library = Library();
        library.Configuration = new PluginConfiguration
        {
            IncludedLibraryIds = [library.Libraries[0].Id, library.Libraries[1].Id],
            ExcludedPathPrefixes = [@"C:\media\Movies\Clips"],
            EditionPhrases = ["extended", "redux"],
            ArticleLanguages = ["En", "Fr"],
            UnknownYearPolicy = "MatchAny",
            RequestWaitSeconds = 7,
            ShowInItemMenu = false,
            ReportWebAnchorProblems = false
        };
        await using var host = await StartAsync(library);
        var exported = SettingsRules.Version(library.Configuration);

        using var export = await host.SendAsync(HttpMethod.Get, Export, As("admin", Alice));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("no-store", export.Headers.CacheControl?.ToString());
        Assert.Matches(@"^librarymaintenance-export-\d{8}-\d{6}\.json$", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var file = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var envelope = JsonNode.Parse(file)!.AsObject();
        Assert.Equal("jellyfin-plugin-export/1", (string?)envelope["Format"]);
        Assert.Equal(Plugin.PluginId, (string?)envelope["PluginId"]);
        Assert.Equal(1, (int?)envelope["SchemaVersion"]);
        Assert.Equal(ServerId, (string?)envelope["ServerId"]);
        Assert.Empty(envelope["Data"]!.AsObject());
        Assert.Equal(@"C:\media\Movies\Clips", (string?)envelope["Settings"]!["ExcludedPathPrefixes"]![0]);
        Assert.Equal([true, true, false, false], new[] { "EnableWebUi", "MoveEnabled", "ShowInItemMenu", "ReportWebAnchorProblems" }.Select(n => (bool)envelope["Settings"]![n]!));

        // Every field changed and saved.
        await Post(host, Settings + "?dryRun=false&version=" + SettingsRules.Version(library.Configuration), JsonSerializer.Serialize(new
        {
            DuplicatesEnabled = false,
            DefaultMatchMode = "Title",
            UnknownYearPolicy = "MatchUnknownOnly",
            IncludedLibraryIds = Array.Empty<Guid>(),
            ExcludedPathPrefixes = Array.Empty<string>(),
            StripEditionSuffixes = false,
            EditionPhrases = new[] { "imax" },
            ArticleLanguages = new[] { "De" },
            WarmIndexAfterScan = false,
            RequestWaitSeconds = 0,
            EnableWebUi = false,
            MoveEnabled = false,
            ShowInItemMenu = true,
            ReportWebAnchorProblems = true
        }));
        var changed = SettingsRules.Version(library.Configuration);
        Assert.False(library.Configuration.EnableWebUi);
        Assert.False(library.Configuration.MoveEnabled);
        Assert.NotEqual(exported, changed);

        // The import's dry run lists the fourteen fields back and saves nothing; applied, the settings are the exported ones.
        var dry = await Post(host, Import + "?version=" + changed, file);
        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        Assert.Equal(14, dry.GetProperty("Changes").GetArrayLength());
        Assert.Equal(["Add Library Maintenance to the web client", "Move media", "Move in the item menu", "Report missing web client elements"],
            dry.GetProperty("Changes").EnumerateArray().Skip(10).Select(c => c.GetProperty("Label").GetString()));
        Assert.True(dry.GetProperty("ScopeChanged").GetBoolean());
        Assert.Equal(JsonValueKind.Object, dry.GetProperty("Preview").ValueKind);
        Assert.Empty(dry.GetProperty("Warnings").EnumerateArray());
        var source = dry.GetProperty("Source");
        Assert.True(source.GetProperty("SameServer").GetBoolean());
        Assert.Equal(changed, SettingsRules.Version(library.Configuration));

        var applied = await Post(host, Import + "?dryRun=false&version=" + changed, file);
        Assert.True(applied.GetProperty("Applied").GetBoolean());
        Assert.Equal(exported, SettingsRules.Version(library.Configuration));
        Assert.Equal(exported, applied.GetProperty("Version").GetString());
        Assert.Equal([@"C:\media\Movies\Clips"], library.Configuration.ExcludedPathPrefixes);
        Assert.Equal(["En", "Fr"], library.Configuration.ArticleLanguages);
        Assert.Equal(7, library.Configuration.RequestWaitSeconds);
        Assert.True(library.Configuration.EnableWebUi);
        Assert.True(library.Configuration.MoveEnabled);
        Assert.False(library.Configuration.ShowInItemMenu);
        Assert.False(library.Configuration.ReportWebAnchorProblems);

        // Imported again, it changes nothing.
        var again = await Post(host, Import + "?dryRun=false&version=" + SettingsRules.Version(library.Configuration), file);
        Assert.Empty(again.GetProperty("Changes").EnumerateArray());
        Assert.False(again.GetProperty("Applied").GetBoolean());
    }

    [Fact]
    public async Task AnImport_KeepsOrLeavesOutWhatThisServerCantUse_AndSaysSo()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        var before = SettingsRules.Version(library.Configuration);
        var file = Envelope(new JsonObject
        {
            ["DefaultMatchMode"] = "Fuzzy",
            ["IncludedLibraryIds"] = new JsonArray(Guid.NewGuid().ToString("N"), library.Libraries[2].Id.ToString("N")),
            ["ExcludedPathPrefixes"] = new JsonArray("clips", @"D:\Clips"),
            ["ArticleLanguages"] = new JsonArray("En", "Xx"),
            ["RequestWaitSeconds"] = 99,
            ["DefaultYearTolerance"] = 1
        }, serverId: "00000000000000000000000000000099");

        var dry = await Post(host, Import, file);
        var warnings = Strings(dry, "Warnings");
        Assert.Contains(warnings, w => w.StartsWith("The file has settings this version doesn't have, which are left out: DefaultYearTolerance.", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("The file doesn't have these settings, which keep their saved values: Find duplicate movies, A movie with no year,", StringComparison.Ordinal));
        Assert.Contains("The file is from another server: its libraries and folders are this server's only where their ids and paths are the same.", warnings);
        Assert.Contains("DefaultMatchMode \"Fuzzy\" isn't one this version knows (TitleAndYear, Title); the saved \"TitleAndYear\" is kept.", warnings);
        Assert.Contains("1 of the file's included libraries isn't on this server and is left out.", warnings);
        Assert.Contains("The excluded folder \"clips\" isn't a full folder path on this server (like D:\\Clips), so it is left out.", warnings);
        Assert.Contains("The article language \"Xx\" isn't one this version knows (En, Fr, De, Es, It, Nl), so it is left out.", warnings);
        Assert.Contains("RequestWaitSeconds 99 isn't from 0 to 30; the saved 5 is kept.", warnings);
        Assert.False(dry.GetProperty("Source").GetProperty("SameServer").GetBoolean());
        var settings = dry.GetProperty("Settings");
        Assert.Equal("TitleAndYear", settings.GetProperty("DefaultMatchMode").GetString());
        Assert.Equal([library.Libraries[2].Id], settings.GetProperty("IncludedLibraryIds").EnumerateArray().Select(Id));
        Assert.Equal([@"D:\Clips"], Strings(settings, "ExcludedPathPrefixes"));
        Assert.Equal(5, settings.GetProperty("RequestWaitSeconds").GetInt32());
        Assert.Equal(before, SettingsRules.Version(library.Configuration));

        // None of the file's libraries here: the index would read every one, which the report says.
        var none = await Post(host, Import, Envelope(new JsonObject { ["IncludedLibraryIds"] = new JsonArray(Guid.NewGuid().ToString()) }));
        Assert.Contains("None of the file's 1 included libraries is on this server, so the index would read every movie and mixed library.", Strings(none, "Warnings"));
        Assert.True(none.GetProperty("ScopeChanged").GetBoolean());
    }

    [Theory]
    [InlineData("""{ "Format": "jellyfin-plugin-export/1", "Plugin": "Bookmarks", "PluginId": "d0ee1b2a-7f71-4654-8ff2-163a81441b03", "SchemaVersion": 1, "ExportedUtc": "2026-11-01T00:00:00Z", "Settings": {}, "Data": {} }""", HttpStatusCode.BadRequest, "This export is from Bookmarks")]
    [InlineData("""{ "Format": "jellyfin-plugin-export/1", "Plugin": "Library Maintenance", "PluginId": "fd47a1d3-dd8c-4e34-aeea-4f2edd973800", "SchemaVersion": 2, "ExportedUtc": "2026-11-01T00:00:00Z", "Settings": {}, "Data": {} }""", HttpStatusCode.Conflict, "newer Library Maintenance version")]
    [InlineData("""{ "Format": "jellyfin-plugin-export/1", "Plugin": "Library Maintenance", "PluginId": "fd47a1d3-dd8c-4e34-aeea-4f2edd973800", "SchemaVersion": 1, "ExportedUtc": "2026-11-01T00:00:00Z", "Data": {} }""", HttpStatusCode.BadRequest, "The file has no Settings to import.")]
    [InlineData("""{ "Format": "jellyfin-plugin-export/1", "Plugin": "Library Maintenance", "PluginId": "fd47a1d3-dd8c-4e34-aeea-4f2edd973800", "SchemaVersion": 1, "ExportedUtc": "2026-11-01T00:00:00Z", "Settings": { "RequestWaitSeconds": "soon" }, "Data": {} }""", HttpStatusCode.BadRequest, "The file's Settings can't be read at RequestWaitSeconds.")]
    [InlineData("""{ "Settings": {} }""", HttpStatusCode.BadRequest, "isn't an export of the format")]
    public async Task AnImport_RefusesAFileThatIsntThisPluginsOrItCantRead(string file, HttpStatusCode status, string message)
    {
        var library = Library();
        await using var host = await StartAsync(library);
        using var response = await host.SendAsync(HttpMethod.Post, Import + "?dryRun=false", r => { As("admin", Alice)(r); Body(file)(r); });
        Assert.Equal(status, response.StatusCode);
        var body = await Json(response);
        Assert.Contains(message, body.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // A newer schema's 409 has its own code, which the page tells from the version check's by (the third review).
        Assert.Equal(status == HttpStatusCode.Conflict ? "NewerSchema" : null, Absent(body, "code") ? null : body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASave_NeedsItsVersion_ADryRunDoesnt()
    {
        // The third review: a save without the version it was made from could overwrite another administrator's unseen.
        var library = Library();
        await using var host = await StartAsync(library);
        var before = SettingsRules.Version(library.Configuration);
        using var exported = await host.SendAsync(HttpMethod.Get, Export, As("admin", Alice));
        var file = await exported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var (path, body) in new[] { (Settings + "?dryRun=false", """{ "RequestWaitSeconds": 9 }"""), (Settings + "?dryRun=false&version=%20", """{ "RequestWaitSeconds": 9 }"""), (Import + "?dryRun=false", file) })
        {
            using var response = await host.SendAsync(HttpMethod.Post, path, r => { As("admin", Alice)(r); Body(body)(r); });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("A save needs the version of the settings it was made from: read them (GET /LibraryMaintenance/Admin/Settings) and send their Version as version.", (await Json(response)).GetProperty("detail").GetString());
        }

        Assert.Equal(before, SettingsRules.Version(library.Configuration));
        Assert.True((await Post(host, Settings, """{ "RequestWaitSeconds": 9 }""")).GetProperty("DryRun").GetBoolean());
        Assert.True((await Post(host, Import, file)).GetProperty("DryRun").GetBoolean());
    }

    [Fact]
    public async Task AnImportOver1MB_IsRefusedWithItsLimit()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        var over = Envelope(new JsonObject { ["Padding"] = new string('x', 1024 * 1024) });
        using var response = await host.SendAsync(HttpMethod.Post, Import, r => { As("admin", Alice)(r); Body(over)(r); });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("An import file is at most 1 MB.", (await Json(response)).GetProperty("detail").GetString());

        var under = Envelope(new JsonObject { ["Padding"] = new string('x', 1000 * 1000) });
        Assert.True(Encoding.UTF8.GetByteCount(under) < 1024 * 1024);
        Assert.Contains("The file has settings this version doesn't have, which are left out: Padding.", Strings(await Post(host, Import, under), "Warnings"));
    }

    [Fact]
    public async Task AnImportOfA10Export_KeepsTheWebClientAndMoveSwitches_AndSaysTheFileDoesntHaveThem()
    {
        // A 1.0.x export has the ten 1.0 fields only; its import keeps the four switches as
        // saved (here two of them off) and names them.
        var library = Library();
        library.Configuration = new PluginConfiguration { EnableWebUi = false, ShowInItemMenu = false };
        await using var host = await StartAsync(library);
        var settings = new JsonObject
        {
            ["DuplicatesEnabled"] = true, ["DefaultMatchMode"] = "Title", ["UnknownYearPolicy"] = "MatchUnknownOnly", ["IncludedLibraryIds"] = new JsonArray(),
            ["ExcludedPathPrefixes"] = new JsonArray(), ["StripEditionSuffixes"] = true, ["EditionPhrases"] = new JsonArray("extended"),
            ["ArticleLanguages"] = new JsonArray("En"), ["WarmIndexAfterScan"] = true, ["RequestWaitSeconds"] = 5
        };

        var report = await Post(host, Import + "?dryRun=false&version=" + SettingsRules.Version(library.Configuration), Envelope(settings));
        Assert.True(report.GetProperty("Applied").GetBoolean());
        Assert.Contains("The file doesn't have these settings, which keep their saved values: Add Library Maintenance to the web client, Move media, Move in the item menu, Report missing web client elements.", Strings(report, "Warnings"));
        Assert.Equal("Title", library.Configuration.DefaultMatchMode);
        Assert.False(library.Configuration.EnableWebUi);
        Assert.True(library.Configuration.MoveEnabled);
        Assert.False(library.Configuration.ShowInItemMenu);
        Assert.True(library.Configuration.ReportWebAnchorProblems);
    }

    [Fact]
    public async Task AnImport_NamesAtMost50SettingsItDoesntHave_AndSaysWhichHaveNoValue()
    {
        // The third review: a 1 MB file can hold thousands of names, each as long as it likes; and a setting with a null
        // value is left out like one the file doesn't have, which the report now says.
        var library = Library();
        await using var host = await StartAsync(library);
        var settings = new JsonObject { ["RequestWaitSeconds"] = null, ["DefaultMatchMode"] = "Title", ["A" + new string('k', 200)] = 1 };
        for (var i = 0; i < 120; i++)
        {
            settings["Zz" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)] = i;
        }

        var report = await Post(host, Import, Envelope(settings));
        var warnings = Strings(report, "Warnings");
        var named = string.Join(", ", new[] { "A" + new string('k', 79) + "…" }.Concat(Enumerable.Range(0, 49).Select(i => "Zz" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Contains("The file has settings this version doesn't have, which are left out: " + named + ", and 71 more.", warnings);
        Assert.Contains("The file has no value (null) for these settings, which keep their saved values: Wait for a build.", warnings);
        Assert.Equal(5, report.GetProperty("Settings").GetProperty("RequestWaitSeconds").GetInt32());
        Assert.Equal("Title", report.GetProperty("Settings").GetProperty("DefaultMatchMode").GetString());
        Assert.DoesNotContain(warnings, w => w.Contains("Wait for a build", StringComparison.Ordinal) && w.StartsWith("The file doesn't have", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedPreview_SaysSoInItsOwnWords_NeverTheErrors()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild();
        library.BeforeItemIds = _ => throw new InvalidOperationException(@"The disk at \\nas\private is gone.");

        var preview = (await Post(host, Settings, JsonSerializer.Serialize(new { IncludedLibraryIds = new[] { library.Libraries[0].Id } }))).GetProperty("Preview");
        Assert.Equal("The movies couldn't be counted: the preview's read of the library failed. The server's log has the details.", preview.GetProperty("Note").GetString());
        Assert.True(Absent(preview, "MoviesAfter"));
        Assert.Equal(["Movies Stays", "More Movies Leaves"], preview.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString() + " " + l.GetProperty("Change").GetString()));
    }

    [Fact]
    public async Task Diagnostics_CountEachVersionState()
    {
        var library = Library();
        await using var host = await StartAsync(library);
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild();

        using var response = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", As("admin", Alice));
        var states = (await Json(response)).GetProperty("DuplicatesIndex").GetProperty("States");
        Assert.Equal(["Orphaned", "StaleLinks", "Chained", "Unlisted", "ListedNotPointing", "Cycle"], states.EnumerateObject().Select(p => p.Name));
        Assert.All(states.EnumerateObject(), p => Assert.Equal(0, p.Value.GetInt32()));
    }

    private static DuplicatesLibrary Library()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        library.Add("Heat", 1995, DuplicatesLibrary.Kids);
        library.Add("Alien", 1979);
        library.Add("Alien", 1979, file: "Alien (1979) - 2.mkv");
        return library;
    }

    private static string Envelope(JsonObject settings, string? serverId = ServerId) => new JsonObject
    {
        ["Format"] = "jellyfin-plugin-export/1",
        ["Plugin"] = "Library Maintenance",
        ["PluginId"] = Plugin.PluginId,
        ["PluginVersion"] = "1.0.0.0",
        ["SchemaVersion"] = 1,
        ["ExportedUtc"] = "2026-11-01T12:00:00Z",
        ["ServerId"] = serverId,
        ["Users"] = new JsonArray(),
        ["Settings"] = settings,
        ["Data"] = new JsonObject()
    }.ToJsonString();

    // Jellyfin's JSON leaves a null property out.
    private static bool Absent(JsonElement element, string property)
        => !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private static string Count(JsonElement element, string property)
        => Absent(element, property) ? "-" : element.GetProperty(property).GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static Action<HttpRequestMessage> As(string caller, Guid userId) => r =>
    {
        r.Headers.Add(JellyfinAuth.CallerHeader, caller);
        r.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString());
    };

    private static Action<HttpRequestMessage> Body(string json) => r => r.Content = new StringContent(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Get(WebHostHarness host, Guid admin)
    {
        using var response = await host.SendAsync(HttpMethod.Get, Settings, As("admin", admin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static async Task<JsonElement> Post(WebHostHarness host, string path, string body, Guid? admin = null)
    {
        using var response = await host.SendAsync(HttpMethod.Post, path, r => { As("admin", admin ?? Alice)(r); Body(body)(r); });
        var json = await Json(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToString());
        return json;
    }

    private static List<string> Strings(JsonElement body, string property) => body.GetProperty(property).EnumerateArray().Select(i => i.GetString()!).ToList();

    // Jellyfin writes ids as 32 hex digits (its JsonGuidConverter), which JsonElement.GetGuid doesn't read.
    private static Guid Id(JsonElement element) => Guid.Parse(element.GetString()!);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    private static Task<WebHostHarness> StartAsync(DuplicatesLibrary library)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        host.SetupGet(h => h.SystemId).Returns(ServerId);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);

        var users = new Mock<IUserManager>();
        var alice = new User("alice", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Alice };
        var bob = new User("bob", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Bob };
        users.Setup(u => u.GetUserById(Alice)).Returns(alice);
        var carol = new User("carol", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Carol, MaxParentalRatingScore = 13 };
        users.Setup(u => u.GetUserById(Bob)).Returns(bob);
        users.Setup(u => u.GetUserById(Carol)).Returns(carol);
        var access = new LibraryAccess(library.Fake.Object, user => user is null || user.Id != Bob ? library.Libraries : [library.Libraries[0]], library.Fake.IsVisible);
        foreach (var folder in library.Libraries)
        {
            library.Fake.Add(folder);
        }

        Type[] replaced = [typeof(IPluginConfigurationProvider), typeof(MovieIndexBuilder), typeof(LibraryAccess)];
        return new WebHostHarness()
            .WithServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(library.Fake.Object);
                services.AddSingleton(host.Object);
                services.AddSingleton(users.Object);
                foreach (var descriptor in registered.Where(d => !replaced.Contains(d.ServiceType)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(library.ConfigurationProvider);
                services.AddSingleton(library.Builder());
                services.AddSingleton(access);
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
            .WithMvc(typeof(LibraryMaintenanceDuplicatesController), typeof(LibraryMaintenanceAdminController))
            .StartAsync();
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jflm-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path);
    }
}
