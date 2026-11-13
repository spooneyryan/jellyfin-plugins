using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.System;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// The admin-surface, server-wide and hardening rules through the global filter (<see cref="ServerHardening"/>): the
/// <c>AdminSurfaces</c> and <c>UserDirectory</c> locks, the configuration reads (allow-list, key case, <c>Redact</c> on a
/// copy, the live objects never changed), the restart block and the public user list (anonymous callers included), and
/// every <c>LibraryHardening</c> Standard row over a library the viewer can't open, with owned items and named views; each
/// with its negative control (an unlocked user, an administrator, an API key, the setting off), fail closed, counted and
/// timed. The fixture: <c>Movies</c> (a movie and its local trailer) the viewer may open, <c>Hidden</c> (a movie, a series
/// with a season and an episode, a plain folder, a named sub-view of it) they may not.
/// </summary>
public sealed class ServerHardeningTests : IDisposable
{
    private const string Ns = "Jellyfin.Api.Controllers.";

    // The encoding options' six server paths and devices.
    private static readonly string[] _encodingPaths = ["EncoderAppPath", "EncoderAppPathDisplay", "TranscodingTempPath", "FallbackFontPath", "VaapiDevice", "QsvDevice"];

    private readonly FilterHarness _h = new();
    private readonly User _viewer;
    private readonly User _other;
    private readonly User _admin;
    private readonly CollectionFolder _movies;
    private readonly CollectionFolder _hidden;
    private readonly Movie _visible;
    private readonly Trailer _trailer;
    private readonly Movie _hiddenMovie;
    private readonly Series _series;
    private readonly Season _season;
    private readonly Episode _episode;
    private readonly Folder _folder;
    private readonly UserView _subView;

    public ServerHardeningTests()
    {
        _viewer = _h.AddUser("viewer");
        _other = _h.AddUser("other");
        _admin = _h.AddUser("admin");
        _movies = _h.Library.Add(new CollectionFolder { Name = "Movies", PhysicalFolderIds = [Guid.NewGuid()] });
        _hidden = _h.Library.Add(new CollectionFolder { Name = "Hidden", PhysicalFolderIds = [Guid.NewGuid()] });
        _visible = _h.Library.Add(new Movie { Name = "Visible" }, _movies);
        _trailer = _h.Library.Add(new Trailer { Name = "Visible trailer", OwnerId = _visible.Id });
        _hiddenMovie = _h.Library.Add(new Movie { Name = "Hidden movie" }, _hidden);
        _series = _h.Library.AddSeries("Hidden series");
        _series.ParentId = _hidden.Id;
        _season = _h.Library.AddSeason(_series, 1);
        _episode = _h.Library.AddEpisode(_series, _season, 1);
        _folder = _h.Library.Add(new Folder { Name = "Trip" }, _hidden);
        _subView = _h.Library.Add(new UserView { Name = "Movies", ViewType = CollectionType.movies, DisplayParentId = _hidden.Id });
        _h.Library.Hide(_viewer, _hidden);
        _h.VisibleLibraries = user => user == _viewer ? [_movies] : [_movies, _hidden];
    }

    public static TheoryData<string> AdminSurfacesActions() => [.. ActionManifest.ForBuild().Actions
        .Where(a => a.Rules.Any(r => r.Kind == RuleKind.LockedReject && r.Lock == AccessRolesLock.AdminSurfaces))
        .Select(a => a.Key[Ns.Length..])];

    public static TheoryData<string> HiddenParents() => ["hidden", "series", "season", "folder", "subView", "episode"];

    [Theory]
    [MemberData(nameof(AdminSurfacesActions))]
    public async Task AdminSurfaces_RefusesALockedNonAdmin_AndNoOneElse(string action)
    {
        await _h.LockAsync([AccessRolesLock.AdminSurfaces], _viewer.Id, _admin.Id);
        var descriptor = Descriptor(action);

        var locked = await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_viewer.Id));
        Assert.True(locked.Locked, action);
        Assert.Equal(ServerHardening.AdminSurfacesMessage, locked.Problem!.Detail);

        Assert.True((await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_other.Id))).Ran, action);
        Assert.True((await _h.RunAsync(descriptor, FilterHarness.Administrator(_admin.Id))).Ran, action);
        Assert.True((await _h.RunAsync(descriptor, FilterHarness.ApiKey())).Ran, action);
        Assert.Equal(1, Totals()[BlockCounter.Refused]);
    }

    [Fact]
    public async Task SystemInfo_UnderAdminSurfaces_BlanksTheServerPaths_OnACopy()
    {
        // With LibraryHardening Off, the lock alone decides (under Standard every non-admin gets the copy, the next test).
        _h.Sync.Configuration.LibraryHardening = "Off";
        await _h.LockAsync([AccessRolesLock.AdminSurfaces], _viewer.Id);
        var info = new SystemInfo { ServerName = "box", Version = "10.11.5", Id = "server-id" };
#pragma warning disable CS0618 // The obsolete path fields are what the rule blanks.
        info.ProgramDataPath = @"C:\ProgramData\Jellyfin\Server";
        info.CachePath = @"C:\cache";
        info.LogPath = @"C:\log";
        info.WebPath = @"C:\web";

        var (locked, result) = await _h.RunWithResultAsync(Descriptor("SystemController.GetSystemInfo"), FilterHarness.NonAdmin(_viewer.Id), null, new ObjectResult(info));
        var copy = Assert.IsType<SystemInfo>(Assert.IsType<ObjectResult>(result).Value);
        Assert.True(locked.Ran);
        Assert.NotSame(info, copy);
        Assert.Equal(string.Empty, copy.ProgramDataPath + copy.CachePath + copy.LogPath + copy.WebPath + copy.ItemsByNamePath + copy.InternalMetadataPath + copy.TranscodingTempPath);
        Assert.Equal(("box", "10.11.5", "server-id"), (copy.ServerName, copy.Version, copy.Id));
        Assert.Equal(@"C:\ProgramData\Jellyfin\Server", info.ProgramDataPath);

        var (_, open) = await _h.RunWithResultAsync(Descriptor("SystemController.GetSystemInfo"), FilterHarness.NonAdmin(_other.Id), null, new ObjectResult(info));
        Assert.Same(info, Assert.IsType<ObjectResult>(open).Value);
#pragma warning restore CS0618
    }

    [Fact]
    public async Task SystemInfo_UnderLibraryHardeningStandard_BlanksTheServerPaths_ForEveryNonAdmin_AndOffOnlyUnderAdminSurfaces()
    {
        // The paths are no user's business, locked or not.
        var info = new SystemInfo { ServerName = "box", Version = "10.11.5", Id = "server-id" };
#pragma warning disable CS0618 // The obsolete path fields are what the rule blanks.
        info.ProgramDataPath = @"C:\ProgramData\Jellyfin\Server";
        info.CachePath = @"C:\cache";
        info.LogPath = @"C:\log";
        info.WebPath = @"C:\web";
        info.ItemsByNamePath = @"C:\ibn";
        info.InternalMetadataPath = @"C:\metadata";
        info.TranscodingTempPath = @"D:\transcodes";
        var descriptor = Descriptor("SystemController.GetSystemInfo");

        Assert.Equal("Standard", _h.Sync.Configuration.LibraryHardening);
        var (unlocked, standard) = await _h.RunWithResultAsync(descriptor, FilterHarness.NonAdmin(_other.Id), null, new ObjectResult(info));
        var copy = Assert.IsType<SystemInfo>(Assert.IsType<ObjectResult>(standard).Value);
        var (_, admin) = await _h.RunWithResultAsync(descriptor, FilterHarness.Administrator(_admin.Id), null, new ObjectResult(info));
        var (_, key) = await _h.RunWithResultAsync(descriptor, FilterHarness.ApiKey(), null, new ObjectResult(info));
        _h.Sync.Configuration.LibraryHardening = "Off";
        var (_, off) = await _h.RunWithResultAsync(descriptor, FilterHarness.NonAdmin(_other.Id), null, new ObjectResult(info));

        Assert.True(unlocked.Ran);
        Assert.NotSame(info, copy);
        Assert.Equal(string.Empty, copy.ProgramDataPath + copy.CachePath + copy.LogPath + copy.WebPath + copy.ItemsByNamePath + copy.InternalMetadataPath + copy.TranscodingTempPath);
        Assert.Equal(("box", "10.11.5", "server-id"), (copy.ServerName, copy.Version, copy.Id));
        Assert.Equal(@"D:\transcodes", info.TranscodingTempPath);
        Assert.Same(info, Assert.IsType<ObjectResult>(admin).Value);
        Assert.Same(info, Assert.IsType<ObjectResult>(key).Value);

        // The negative control: with LibraryHardening Off, an unlocked non-admin gets the native answer, as before.
        Assert.Same(info, Assert.IsType<ObjectResult>(off).Value);
#pragma warning restore CS0618
    }

    [Theory]
    [InlineData("AllowList")]
    [InlineData("Redact")]
    public async Task ConfigurationReads_TheServerConfiguration_AnswersWithoutTheServersPaths_AndKeepsEveryOtherSetting(string mode)
    {
        // Redact used to answer GET /System/Configuration with the cache and metadata folders.
        _h.Sync.Configuration.NonAdminConfigurationReads = mode;
        _h.Sync.Configuration.ConfigurationReadAllowList = ["encoding", "system"];
        var live = new ServerConfiguration
        {
            ServerName = "box",
            CachePath = @"D:\jellyfin-cache",
            MetadataPath = @"D:\jellyfin-metadata",
            PathSubstitutions = [new PathSubstitution { From = @"\\nas\media", To = @"M:\media" }],
            PreferredMetadataLanguage = "fr"
        };
        var liveTv = new LiveTvOptions { RecordingPath = @"D:\recordings", MovieRecordingPath = @"D:\movies", SeriesRecordingPath = @"D:\series", RecordingPostProcessor = @"C:\tools\post.exe", MediaLocationsCreated = [@"D:\recordings\Movies"], GuideDays = 7 };
        var before = Json(live);
        var beforeTv = Json(liveTv);

        var (run, result) = mode == "Redact"
            ? await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetConfiguration"), FilterHarness.NonAdmin(_viewer.Id), null, new ObjectResult(live))
            : await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "system" }, new ObjectResult(live));
        _h.Sync.Configuration.ConfigurationReadAllowList = ["encoding", "livetv"];
        var (_, tvResult) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "livetv" }, new ObjectResult(liveTv));

        Assert.True(run.Ran);
        var copy = Assert.IsType<ServerConfiguration>(Assert.IsType<ObjectResult>(result).Value);
        Assert.NotSame(live, copy);
        Assert.Equal((string.Empty, string.Empty), (copy.CachePath, copy.MetadataPath));
        Assert.Equal((string.Empty, string.Empty), (Assert.Single(copy.PathSubstitutions).From, copy.PathSubstitutions[0].To));
        Assert.Equal(("box", "fr"), (copy.ServerName, copy.PreferredMetadataLanguage));
        Assert.DoesNotContain(@"D:\\", Json(copy), StringComparison.Ordinal);
        Assert.DoesNotContain("nas", Json(copy), StringComparison.Ordinal);
        var tv = Assert.IsType<LiveTvOptions>(Assert.IsType<ObjectResult>(tvResult).Value);
        Assert.Equal(string.Empty, tv.RecordingPath + tv.MovieRecordingPath + tv.SeriesRecordingPath + tv.RecordingPostProcessor);
        Assert.Empty(tv.MediaLocationsCreated);
        Assert.Equal(7, tv.GuideDays);

        // The live objects keep every path.
        Assert.Equal(before, Json(live));
        Assert.Equal(beforeTv, Json(liveTv));
    }

    [Fact]
    public async Task UserDirectory_ListsOnlyTheCaller_AndAnotherUserIsNotFound()
    {
        await _h.LockAsync([AccessRolesLock.UserDirectory], _viewer.Id);
        List<UserDto> All() => [new() { Id = _viewer.Id, Name = "viewer" }, new() { Id = _other.Id, Name = "other" }, new() { Id = _admin.Id, Name = "admin" }];

        var (_, list) = await _h.RunWithResultAsync(Descriptor("UserController.GetUsers"), FilterHarness.NonAdmin(_viewer.Id), null, new OkObjectResult(All()));
        Assert.Equal([_viewer.Id], ((IEnumerable<UserDto>)Assert.IsAssignableFrom<ObjectResult>(list).Value!).Select(u => u.Id));
        var (_, openList) = await _h.RunWithResultAsync(Descriptor("UserController.GetUsers"), FilterHarness.NonAdmin(_other.Id), null, new OkObjectResult(All()));
        Assert.Equal(3, ((IEnumerable<UserDto>)Assert.IsAssignableFrom<ObjectResult>(openList).Value!).Count());

        var someoneElse = await _h.RunAsync(Descriptor("UserController.GetUserById"), FilterHarness.NonAdmin(_viewer.Id), new() { ["userId"] = _other.Id });
        Assert.False(someoneElse.Ran);
        Assert.Equal((404, ServerHardening.UserNotFound), (Assert.IsType<NotFoundObjectResult>(someoneElse.Context.Result).StatusCode, (string?)((NotFoundObjectResult)someoneElse.Context.Result!).Value));
        Assert.True((await _h.RunAsync(Descriptor("UserController.GetUserById"), FilterHarness.NonAdmin(_viewer.Id), new() { ["userId"] = _viewer.Id })).Ran);
        Assert.True((await _h.RunAsync(Descriptor("UserController.GetUserById"), FilterHarness.NonAdmin(_other.Id), new() { ["userId"] = _viewer.Id })).Ran);
        Assert.True((await _h.RunAsync(Descriptor("UserController.GetUserById"), FilterHarness.ApiKey(), new() { ["userId"] = _viewer.Id })).Ran);
        Assert.Equal(2, Totals()[BlockCounter.Hidden]);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("NETWORK")]
    [InlineData("Network")]
    [InlineData("livetv")]
    [InlineData("LiveTV")]
    [InlineData("database")]
    [InlineData(" encoding2")]
    public async Task ConfigurationReads_AllowList_RefusesEveryKeyNotOnIt_InAnyCase_AndTheWholeConfiguration(string key)
    {
        var named = await _h.RunAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = key });
        Assert.True(named.Locked, key);
        Assert.Equal(ServerHardening.ConfigurationMessage, named.Problem!.Detail);
        Assert.True((await _h.RunAsync(Descriptor("ConfigurationController.GetConfiguration"), FilterHarness.NonAdmin(_viewer.Id))).Locked);

        // An administrator and an API key read every key; the core answers them.
        Assert.True((await _h.RunAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.Administrator(_admin.Id), new() { ["key"] = key })).Ran);
        Assert.True((await _h.RunAsync(Descriptor("ConfigurationController.GetConfiguration"), FilterHarness.ApiKey())).Ran);
    }

    [Theory]
    [InlineData("encoding")]
    [InlineData("ENCODING")]
    [InlineData("Encoding")]
    public async Task ConfigurationReads_AllowList_LetsTheVideoPlayersKeyOn_AsACopyThatSerialisesLikeTheOriginal(string key)
    {
        var live = new EncodingOptions { EnableThrottling = true, EnableFallbackFont = true, VaapiDevice = string.Empty };
        var before = Json(live);

        var (run, result) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = key }, new ObjectResult(live) { DeclaredType = typeof(object) });

        Assert.True(run.Ran);
        var answer = Assert.IsType<ObjectResult>(result);
        Assert.NotSame(live, answer.Value);
        Assert.Equal(typeof(object), answer.DeclaredType);
        Assert.Equal(before, Json(answer.Value!));
        Assert.Equal(before, Json(live));
    }

    [Theory]
    [InlineData("AllowList")]
    [InlineData("Redact")]
    public async Task ConfigurationReads_Encoding_AnswersWithoutTheServersPathsAndDevices_InEitherMode_AndKeepsEveryOtherSetting(string mode)
    {
        _h.Sync.Configuration.NonAdminConfigurationReads = mode;
        var live = new EncodingOptions
        {
            EncoderAppPath = @"C:\ffmpeg\ffmpeg.exe",
            EncoderAppPathDisplay = @"C:\ffmpeg\ffmpeg.exe",
            TranscodingTempPath = @"D:\transcodes",
            FallbackFontPath = @"D:\fonts",
            VaapiDevice = "/dev/dri/renderD129",
            QsvDevice = "/dev/dri/renderD130",
            EnableFallbackFont = true,
            EnableThrottling = true,
            EncodingThreadCount = 3,
            HardwareAccelerationType = MediaBrowser.Model.Entities.HardwareAccelerationType.qsv
        };
        var before = Json(live);

        var (run, result) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "encoding" }, new ObjectResult(live));

        Assert.True(run.Ran);
        var copy = Assert.IsType<EncodingOptions>(Assert.IsType<ObjectResult>(result).Value);
        Assert.NotSame(live, copy);
        Assert.All(_encodingPaths, name => Assert.Equal(string.Empty, (string?)typeof(EncodingOptions).GetProperty(name)!.GetValue(copy)));
        Assert.DoesNotContain("ffmpeg", Json(copy), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/dev/dri", Json(copy), StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\\", Json(copy), StringComparison.Ordinal);

        // What the web reads stays, and so does every other setting: the answer is the original but for the six fields.
        Assert.True(copy.EnableFallbackFont);
        var expected = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(before)!;
        var actual = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Json(copy))!;
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        Assert.All(expected.Keys.Where(k => !_encodingPaths.Contains(k, StringComparer.OrdinalIgnoreCase)), k => Assert.Equal(expected[k].GetRawText(), actual[k].GetRawText()));

        // The live options keep every path; an administrator reads them all.
        Assert.Equal(before, Json(live));
        var (_, admin) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.Administrator(_admin.Id), new() { ["key"] = "encoding" }, new ObjectResult(live));
        Assert.Same(live, Assert.IsType<ObjectResult>(admin).Value);
    }

    [Fact]
    public async Task ConfigurationReads_AnAllowListedSecretKey_IsStillStripped_OnACopy_AndTheLiveObjectKeepsItsSecret()
    {
        // Adding network or livetv to the list for a native client never returns their secrets.
        _h.Sync.Configuration.ConfigurationReadAllowList = ["encoding", "Network"];
        var live = new NetworkConfiguration { CertificatePassword = "secret-pfx", CertificatePath = @"C:\cert.pfx", KnownProxies = ["10.0.0.1"] };
        var before = Json(live);

        var (_, result) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "NETWORK" }, new ObjectResult(live));
        var copy = Assert.IsType<NetworkConfiguration>(Assert.IsType<ObjectResult>(result).Value);

        Assert.Equal(string.Empty, copy.CertificatePassword);
        Assert.Equal((string.Empty, "10.0.0.1"), (copy.CertificatePath, Assert.Single(copy.KnownProxies)));
        Assert.Equal("secret-pfx", live.CertificatePassword);
        Assert.Equal(before, Json(live));

        // An allow-listed key of a type the guard doesn't know (another plugin's) goes as it is: nothing known to strip.
        _h.Sync.Configuration.ConfigurationReadAllowList = ["otherplugin"];
        var other = new { Setting = 1 };
        var (_, otherResult) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "OtherPlugin" }, new ObjectResult(other));
        Assert.Same(other, Assert.IsType<ObjectResult>(otherResult).Value);
    }

    [Fact]
    public async Task ConfigurationReads_Redact_AnswersEveryKnownKey_WithItsSecretsStripped_OnACopy_AndRefusesAnUnknownType()
    {
        _h.Sync.Configuration.NonAdminConfigurationReads = "Redact";
        var liveTv = new LiveTvOptions
        {
            TunerHosts = [new TunerHostInfo { Url = "http://user:pass@m3u.example/list.m3u", Source = "secret-source", Type = "m3u" }],
            ListingProviders = [new ListingsProviderInfo { Username = "sd-user", Password = "sd-hash", Path = "http://xmltv.example/?key=k", Type = "SchedulesDirect" }]
        };
        var network = new NetworkConfiguration { CertificatePassword = "secret-pfx" };
#if JELLYFIN_12
        // Jellyfin 12 has no subtitles configuration (SubtitleOptions was removed).
        var lives = new object[] { liveTv, network, new ServerConfiguration(), new EncodingOptions(), new MetadataConfiguration() };
#else
        var subtitles = new SubtitleOptions { OpenSubtitlesUsername = "os-user", OpenSubtitlesPasswordHash = "os-hash" };
        var lives = new object[] { liveTv, subtitles, network, new ServerConfiguration(), new EncodingOptions(), new MetadataConfiguration() };
#endif
        var before = lives.Select(Json).ToList();

        foreach (var live in lives)
        {
            var (run, result) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "anything" }, new ObjectResult(live));
            Assert.True(run.Ran);
            var copy = Assert.IsType<ObjectResult>(result).Value!;
            Assert.NotSame(live, copy);
            Assert.DoesNotContain("secret", Json(copy), StringComparison.Ordinal);
            Assert.DoesNotContain("sd-", Json(copy), StringComparison.Ordinal);
            Assert.DoesNotContain("os-", Json(copy), StringComparison.Ordinal);
            Assert.DoesNotContain("pass@", Json(copy), StringComparison.Ordinal);
        }

        Assert.Equal(before, lives.Select(Json).ToList());
        var (_, whole) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetConfiguration"), FilterHarness.NonAdmin(_viewer.Id), null, new ObjectResult(new ServerConfiguration()));
        Assert.IsType<ServerConfiguration>(Assert.IsType<ObjectResult>(whole).Value);

        var (_, unknown) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "otherplugin" }, new ObjectResult(new { Token = "x" }));
        Assert.Equal(403, Assert.IsType<Jellyfin.Plugin.Kit.Api.KitProblemResult>(unknown).StatusCode);
    }

    [Fact]
    public void TheConfigurationCopy_OfEveryKnownType_SerialisesLikeTheOriginal_ButForItsStrippedSecrets()
    {
        var guard = new ConfigurationReadGuard(() => JsonDefaults.Options);
        var database = new Jellyfin.Database.Implementations.DbConfiguration.DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-SQLite",
            CustomProviderOptions = new Jellyfin.Database.Implementations.DbConfiguration.CustomDatabaseOptions { PluginName = "p", PluginAssembly = "a", ConnectionString = "Server=db;Password=p" }
        };
        var cases = new object[]
        {
            new NetworkConfiguration { CertificatePassword = "x", KnownProxies = ["1.2.3.4"], LocalNetworkSubnets = ["10.0.0.0/8"] },
            new LiveTvOptions { TunerHosts = [new TunerHostInfo { Url = "u", Source = "s", Id = "1" }], ListingProviders = [new ListingsProviderInfo { Username = "n", Password = "p", Path = "x", Id = "2" }] },
#if !JELLYFIN_12
            new SubtitleOptions { OpenSubtitlesUsername = "n", OpenSubtitlesPasswordHash = "h", DownloadLanguages = ["eng"] },
#endif
            database,
            new ServerConfiguration { ServerName = "box", MetadataPath = "metadata" },
            new EncodingOptions { EncoderAppPath = "ffmpeg" },
            new MetadataConfiguration(),
            new XbmcMetadataOptions { UserId = "u" },
            new MediaBrowser.Model.Branding.BrandingOptions { LoginDisclaimer = "hi", SplashscreenLocation = "/config/data/splashscreen-upload.png" }
        };

        foreach (var live in cases)
        {
            Assert.True(ConfigurationReadGuard.IsKnown(live.GetType()), live.GetType().FullName);
            var copy = guard.Copy(live, out var stripped);
            var expected = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Json(live))!;
            var actual = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Json(copy))!;
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            Assert.Equal(ConfigurationReadGuard.TypesWithStrippedFields.Contains(live.GetType().FullName!), stripped.Count > 0);
        }

        Assert.Equal(string.Empty, ((Jellyfin.Database.Implementations.DbConfiguration.DatabaseConfigurationOptions)guard.Copy(database, out _)).CustomProviderOptions!.ConnectionString);
        Assert.Equal("Server=db;Password=p", database.CustomProviderOptions!.ConnectionString);

        // The splash screen's path on disk goes; what the login page shows stays.
        var branding = (MediaBrowser.Model.Branding.BrandingOptions)guard.Copy(cases[^1], out var brandingStripped);
        Assert.Equal(["SplashscreenLocation"], brandingStripped);
        Assert.Equal(string.Empty, branding.SplashscreenLocation);
        Assert.Equal("hi", branding.LoginDisclaimer);
        Assert.Throws<InvalidOperationException>(() => guard.Copy(new { A = 1 }, out _));
    }

    [Fact]
    public async Task ARedactedCopyThatCantBeMade_RefusesTheRead_FailClosed()
    {
        // A copy whose options throw stands for one that doesn't serialise like the original (a Jellyfin update's new field).
        var broken = new ServerHardening(_h.UserManager.Object, _h.Access, _h.People, new ConfigurationReadGuard(() => throw new InvalidOperationException("no options")), _h.Counter);
        var filter = _h.Build(_h.UserManager.Object, hardening: broken);

        var (run, result) = await _h.RunWithResultAsync(Descriptor("ConfigurationController.GetNamedConfiguration"), FilterHarness.NonAdmin(_viewer.Id), new() { ["key"] = "encoding" }, new ObjectResult(new EncodingOptions()), filter);

        Assert.True(run.Ran);
        var refused = Assert.IsType<Jellyfin.Plugin.Kit.Api.KitProblemResult>(result);
        Assert.Equal((403, ServerHardening.UnavailableMessage), (refused.StatusCode, refused.Value!.Detail));
        Assert.Equal(1, Totals()[BlockCounter.FailedClosed]);
        Assert.Equal(1L, _h.Status.FilterErrors);
    }

    [Fact]
    public async Task Restart_IsRefusedToNonAdmins_AnonymousCallersAndBadTokens_OnlyAdministratorsAndApiKeysPass()
    {
        var restart = Descriptor("SystemController.RestartApplication");

        var nonAdmin = await _h.RunAsync(restart, FilterHarness.NonAdmin(_viewer.Id));
        var anonymous = await _h.RunAsync(restart, FilterHarness.Anonymous());
        Assert.True(nonAdmin.Locked);
        Assert.True(anonymous.Locked);
        Assert.Equal(ServerHardening.RestartMessage, anonymous.Problem!.Detail);
        Assert.True((await _h.RunAsync(restart, FilterHarness.Administrator(_admin.Id))).Ran);
        Assert.True((await _h.RunAsync(restart, FilterHarness.ApiKey())).Ran);

        // The negative control: with the setting off, the core decides (on 10.11.5 any LAN caller restarts it).
        _h.Sync.Configuration.BlockNonAdminRestart = false;
        Assert.True((await _h.RunAsync(restart, FilterHarness.Anonymous())).Ran);
    }

    [Fact]
    public async Task ThePublicUserList_IsTrimmedToTheLoginFields_ForAnonymousCallersAndNonAdmins_OnNewObjects()
    {
#pragma warning disable CS0618 // Obsolete on Jellyfin 12 (always true there), still sent.
        List<UserDto> Users() =>
        [
            new UserDto { Id = _viewer.Id, Name = "viewer", ServerId = "srv", PrimaryImageTag = "tag", HasPassword = true, HasConfiguredPassword = true, LastLoginDate = DateTime.UnixEpoch, Policy = new() { EnabledFolders = [_hidden.Id] } }
        ];
#pragma warning restore CS0618

        var (_, anonymous) = await _h.RunWithResultAsync(Descriptor("UserController.GetPublicUsers"), FilterHarness.Anonymous(), null, new OkObjectResult(Users()));
        var trimmed = Assert.Single((IEnumerable<UserDto>)Assert.IsAssignableFrom<ObjectResult>(anonymous).Value!);
        var json = JsonSerializer.Serialize(trimmed, JsonDefaults.Options);
        Assert.Equal(
            ["HasConfiguredEasyPassword", "HasConfiguredPassword", "HasPassword", "Id", "Name", "PrimaryImageTag", "ServerId"],
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.Keys.Order(StringComparer.Ordinal));

        var (_, admin) = await _h.RunWithResultAsync(Descriptor("UserController.GetPublicUsers"), FilterHarness.Administrator(_admin.Id), null, new OkObjectResult(Users()));
        Assert.NotNull(Assert.Single((IEnumerable<UserDto>)Assert.IsAssignableFrom<ObjectResult>(admin).Value!).Policy);

        _h.Sync.Configuration.TrimPublicUsers = false;
        var (_, untrimmed) = await _h.RunWithResultAsync(Descriptor("UserController.GetPublicUsers"), FilterHarness.Anonymous(), null, new OkObjectResult(Users()));
        Assert.NotNull(Assert.Single((IEnumerable<UserDto>)Assert.IsAssignableFrom<ObjectResult>(untrimmed).Value!).Policy);
    }

    [Theory]
    [MemberData(nameof(HiddenParents))]
    public async Task AHiddenParent_OfItemsResumeAndTheBackstop_Is404_AVisibleOneRuns(string which)
    {
        var parent = Id(which);
        foreach (var action in new[] { "ItemsController.GetItems", "ItemsController.GetItemsByUserIdLegacy", "TrailersController.GetTrailers", "ItemsController.GetResumeItems", "ItemsController.GetResumeItemsLegacy", "MoviesController.GetMovieRecommendations", "CollectionController.CreateCollection" })
        {
            var hidden = await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = parent });
            Assert.False(hidden.Ran, action + " " + which);
            Assert.IsType<NotFoundResult>(hidden.Context.Result);
            Assert.True((await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = _movies.Id })).Ran, action);
            Assert.True((await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_other.Id), new() { ["parentId"] = parent })).Ran, action);
        }
    }

    [Fact]
    public async Task ItemsIds_KeepOnlyTheVisible_OwnedItemsByTheirOwner_AndWithNoneLeftAnswerEmptyWithoutTheAction()
    {
        var unknown = Guid.NewGuid();
        foreach (var action in new[] { "ItemsController.GetItems", "ItemsController.GetItemsByUserIdLegacy", "TrailersController.GetTrailers" })
        {
            var mixed = new Dictionary<string, object?> { ["ids"] = new[] { _visible.Id, _hiddenMovie.Id, _trailer.Id, _episode.Id, unknown } };
            var run = await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), mixed);
            Assert.True(run.Ran, action);
            Assert.Equal([_visible.Id, _trailer.Id, unknown], (Guid[])run.Context.ActionArguments["ids"]!);

            // An emptied ids list is no filter (ItemsController.cs:369): the action never runs with one.
            var none = await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), new() { ["ids"] = new[] { _hiddenMovie.Id, _episode.Id }, ["recursive"] = true });
            Assert.False(none.Ran, action);
            var empty = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(none.Context.Result).Value);
            Assert.Equal((0, 0), (empty.Items.Count, empty.TotalRecordCount));

            var all = new Dictionary<string, object?> { ["ids"] = new[] { _hiddenMovie.Id } };
            Assert.True((await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_other.Id), all)).Ran);
            Assert.Equal([_hiddenMovie.Id], (Guid[])all["ids"]!);
        }
    }

    [Fact]
    public async Task Latest_Upcoming_NextUp_AndTheAggregates_AnswerTheirEmptyResult_ForAHiddenParent()
    {
        var empties = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["UserLibraryController.GetLatestMedia"] = typeof(BaseItemDto[]),
            ["UserLibraryController.GetLatestMediaLegacy"] = typeof(BaseItemDto[]),
            ["TvShowsController.GetUpcomingEpisodes"] = typeof(QueryResult<BaseItemDto>),
            ["TvShowsController.GetNextUp"] = typeof(QueryResult<BaseItemDto>),
            ["GenresController.GetGenres"] = typeof(QueryResult<BaseItemDto>),
            ["StudiosController.GetStudios"] = typeof(QueryResult<BaseItemDto>),
            ["MusicGenresController.GetMusicGenres"] = typeof(QueryResult<BaseItemDto>),
            ["ArtistsController.GetArtists"] = typeof(QueryResult<BaseItemDto>),
            ["ArtistsController.GetAlbumArtists"] = typeof(QueryResult<BaseItemDto>),
            ["YearsController.GetYears"] = typeof(QueryResult<BaseItemDto>),
            ["FilterController.GetQueryFilters"] = typeof(QueryFilters),
            ["FilterController.GetQueryFiltersLegacy"] = typeof(QueryFiltersLegacy),
            ["SearchController.GetSearchHints"] = typeof(SearchHintResult)
        };
        foreach (var (action, type) in empties)
        {
            foreach (var parent in new[] { _hidden.Id, _subView.Id, _series.Id })
            {
                var run = await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = parent });
                Assert.False(run.Ran, action);
                Assert.IsType(type, Assert.IsType<OkObjectResult>(run.Context.Result).Value);
            }

            Assert.True((await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = _movies.Id })).Ran, action);
            Assert.True((await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id))).Ran, action);
        }

        var nextUp = await _h.RunAsync(Descriptor("TvShowsController.GetNextUp"), FilterHarness.NonAdmin(_viewer.Id), new() { ["seriesId"] = _series.Id });
        Assert.False(nextUp.Ran);
    }

    [Fact]
    public async Task Episodes_OfAHiddenSeriesOrSeason_Are404()
    {
        var bySeries = await _h.RunAsync(Descriptor("TvShowsController.GetEpisodes"), FilterHarness.NonAdmin(_viewer.Id), new() { ["seriesId"] = _series.Id });
        var bySeason = await _h.RunAsync(Descriptor("TvShowsController.GetEpisodes"), FilterHarness.NonAdmin(_viewer.Id), new() { ["seriesId"] = Guid.NewGuid(), ["seasonId"] = _season.Id });
        var seasons = await _h.RunAsync(Descriptor("TvShowsController.GetSeasons"), FilterHarness.NonAdmin(_viewer.Id), new() { ["seriesId"] = _series.Id });

        Assert.IsType<NotFoundResult>(bySeries.Context.Result);
        Assert.IsType<NotFoundResult>(bySeason.Context.Result);
        Assert.IsType<NotFoundResult>(seasons.Context.Result);
        Assert.True((await _h.RunAsync(Descriptor("TvShowsController.GetEpisodes"), FilterHarness.NonAdmin(_other.Id), new() { ["seriesId"] = _series.Id })).Ran);
    }

    [Fact]
    public async Task PlaylistItems_ForceTheCallersId_AndDropTheHiddenMembers_AndThePlaylistsItemIdsToo()
    {
        var arguments = new Dictionary<string, object?> { ["playlistId"] = Guid.NewGuid(), ["userId"] = (Guid?)_admin.Id };
        var page = new QueryResult<BaseItemDto>(0, 3, [new BaseItemDto { Id = _visible.Id }, new BaseItemDto { Id = _hiddenMovie.Id }, new BaseItemDto { Id = _episode.Id }]);

        var (run, result) = await _h.RunWithResultAsync(Descriptor("PlaylistsController.GetPlaylistItems"), FilterHarness.NonAdmin(_viewer.Id), arguments, new ObjectResult(page));

        Assert.Equal(_viewer.Id, (Guid?)run.Context.ActionArguments["userId"]);
        var kept = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal([_visible.Id], kept.Items.Select(i => i.Id));
        Assert.Equal(1, kept.TotalRecordCount);
        Assert.Equal(3, page.Items.Count);

        var playlist = new PlaylistDto { OpenAccess = true, Shares = [], ItemIds = [_visible.Id, _hiddenMovie.Id, _trailer.Id] };
        var (_, dto) = await _h.RunWithResultAsync(Descriptor("PlaylistsController.GetPlaylist"), FilterHarness.NonAdmin(_viewer.Id), new() { ["playlistId"] = Guid.NewGuid() }, new ObjectResult(playlist));
        var filtered = Assert.IsType<PlaylistDto>(Assert.IsType<ObjectResult>(dto).Value);
        Assert.Equal([_visible.Id, _trailer.Id], filtered.ItemIds);
        Assert.True(filtered.OpenAccess);
        Assert.Equal(3, playlist.ItemIds.Count);
    }

    [Fact]
    public async Task PeopleSearch_KeepsTheNamesWithAnItemInTheCallersLibraries_InOneQueryPerPage()
    {
        _h.People.TopParentsOf["Visible Actor"] = [_movies.PhysicalFolderIds[0]];
        _h.People.TopParentsOf["Shared Actor"] = [_movies.PhysicalFolderIds[0], _hidden.PhysicalFolderIds[0]];
        _h.People.TopParentsOf["Hidden Actor"] = [_hidden.PhysicalFolderIds[0]];
        QueryResult<BaseItemDto> Page() => new([new BaseItemDto { Name = "Visible Actor" }, new BaseItemDto { Name = "Hidden Actor" }, new BaseItemDto { Name = "Shared Actor" }]);

        var (_, result) = await _h.RunWithResultAsync(Descriptor("PersonsController.GetPersons"), FilterHarness.NonAdmin(_viewer.Id), new() { ["searchTerm"] = "Actor" }, new ObjectResult(Page()));

        var kept = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(["Visible Actor", "Shared Actor"], kept.Items.Select(p => p.Name));
        Assert.Equal(2, kept.TotalRecordCount);
        var asked = Assert.Single(_h.People.Asked);
        Assert.Equal(3, asked.Names.Count);
        Assert.Equal(_movies.PhysicalFolderIds, asked.TopParents);

        // Without searchTerm the list is Strict's (1.1): untouched; for a user who sees both libraries every name stays.
        var (_, all) = await _h.RunWithResultAsync(Descriptor("PersonsController.GetPersons"), FilterHarness.NonAdmin(_viewer.Id), null, new ObjectResult(Page()));
        Assert.Equal(3, Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<ObjectResult>(all).Value).Items.Count);
        var (_, other) = await _h.RunWithResultAsync(Descriptor("PersonsController.GetPersons"), FilterHarness.NonAdmin(_other.Id), new() { ["searchTerm"] = "Actor" }, new ObjectResult(Page()));
        Assert.Equal(3, Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<ObjectResult>(other).Value).Items.Count);

        // A hidden item's cast: empty, without the action.
        var cast = await _h.RunAsync(Descriptor("PersonsController.GetPersons"), FilterHarness.NonAdmin(_viewer.Id), new() { ["appearsInItemId"] = _hiddenMovie.Id });
        Assert.False(cast.Ran);
        Assert.Empty(Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(cast.Context.Result).Value).Items);
    }

    [Fact]
    public async Task PeopleSearch_WhoseQueryFails_IsRefused_FailClosed()
    {
        _h.People.Throw = new InvalidOperationException("no such table: PeopleBaseItemMap");

        var (run, result) = await _h.RunWithResultAsync(Descriptor("PersonsController.GetPersons"), FilterHarness.NonAdmin(_viewer.Id), new() { ["searchTerm"] = "Actor" }, new ObjectResult(new QueryResult<BaseItemDto>([new BaseItemDto { Name = "Hidden Actor" }])));

        Assert.True(run.Ran);
        Assert.Equal(403, Assert.IsType<Jellyfin.Plugin.Kit.Api.KitProblemResult>(result).StatusCode);
        Assert.Equal(1, Totals()[BlockCounter.FailedClosed]);
    }

    [Fact]
    public async Task PlaybackAndViewingReports_OfAHiddenItem_Are204WithoutTheAction_AndTheQueueKeepsTheVisible()
    {
        foreach (var action in new[] { "PlaystateController.OnPlaybackStart", "PlaystateController.OnPlaybackProgressLegacy", "PlaystateController.OnPlaybackStopped" })
        {
            var hidden = await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), new() { ["itemId"] = _hiddenMovie.Id });
            Assert.False(hidden.Ran, action);
            Assert.IsType<NoContentResult>(hidden.Context.Result);
            Assert.True((await _h.RunAsync(Descriptor(action), FilterHarness.NonAdmin(_viewer.Id), new() { ["itemId"] = _visible.Id })).Ran, action);
        }

        foreach (var form in new[] { "N", "D", "B" })
        {
            var viewing = await _h.RunAsync(Descriptor("SessionController.ReportViewing"), FilterHarness.NonAdmin(_viewer.Id), new() { ["itemId"] = _hiddenMovie.Id.ToString(form) });
            Assert.False(viewing.Ran, form);
        }

        var start = new PlaybackStartInfo { ItemId = _hiddenMovie.Id };
        Assert.False((await _h.RunAsync(Descriptor("PlaystateController.ReportPlaybackStart"), FilterHarness.NonAdmin(_viewer.Id), new() { ["playbackStartInfo"] = start })).Ran);
        var stop = new PlaybackStopInfo { ItemId = _hiddenMovie.Id };
        Assert.False((await _h.RunAsync(Descriptor("PlaystateController.ReportPlaybackStopped"), FilterHarness.NonAdmin(_viewer.Id), new() { ["playbackStopInfo"] = stop })).Ran);

        var progress = new PlaybackProgressInfo { ItemId = _visible.Id, NowPlayingQueue = [new QueueItem { Id = _visible.Id }, new QueueItem { Id = _hiddenMovie.Id }, new QueueItem { Id = _trailer.Id }] };
        Assert.True((await _h.RunAsync(Descriptor("PlaystateController.ReportPlaybackProgress"), FilterHarness.NonAdmin(_viewer.Id), new() { ["playbackProgressInfo"] = progress })).Ran);
        Assert.Equal([_visible.Id, _trailer.Id], progress.NowPlayingQueue.Select(q => q.Id));
    }

    [Fact]
    public async Task Sessions_LoseWhatTheCallerCantSee_OnThePerCallDtos_TheLiveListsUntouched()
    {
        var queue = new List<QueueItem> { new() { Id = _visible.Id }, new() { Id = _hiddenMovie.Id } };
        var full = new List<BaseItemDto> { new() { Id = _visible.Id }, new() { Id = _hiddenMovie.Id } };
        var playing = new BaseItemDto { Id = _hiddenMovie.Id, Name = "Hidden movie" };
        var sessions = new List<SessionInfoDto>
        {
#if JELLYFIN_12
            // Jellyfin 12 sends no full items of the queue.
            new() { Id = "admin's", NowPlayingItem = playing, NowViewingItem = new BaseItemDto { Id = _episode.Id }, NowPlayingQueue = queue },
#else
            new() { Id = "admin's", NowPlayingItem = playing, NowViewingItem = new BaseItemDto { Id = _episode.Id }, NowPlayingQueue = queue, NowPlayingQueueFullItems = full },
#endif
            new() { Id = "viewer's", NowPlayingItem = new BaseItemDto { Id = _visible.Id } }
        };

        var (_, result) = await _h.RunWithResultAsync(Descriptor("SessionController.GetSessions"), FilterHarness.NonAdmin(_viewer.Id), new() { ["controllableByUserId"] = (Guid?)_viewer.Id }, new OkObjectResult(sessions));

        var list = ((IEnumerable<SessionInfoDto>)Assert.IsAssignableFrom<ObjectResult>(result).Value!).ToList();
        Assert.Null(list[0].NowPlayingItem);
        Assert.Null(list[0].NowViewingItem);
        Assert.Equal([_visible.Id], list[0].NowPlayingQueue!.Select(q => q.Id));
#if !JELLYFIN_12
        Assert.Equal([_visible.Id], list[0].NowPlayingQueueFullItems!.Select(d => d.Id));
#endif
        Assert.Equal(_visible.Id, list[1].NowPlayingItem!.Id);
        Assert.Equal(2, queue.Count);
        Assert.Equal(2, full.Count);
        Assert.Equal("Hidden movie", playing.Name);
    }

    [Fact]
    public async Task TheTrickplayPlaylist_TheGuidesLibrarySeries_AndAPlayCommand_HideWhatTheCallerCantSee()
    {
        var trickplay = await _h.RunAsync(Descriptor("TrickplayController.GetTrickplayHlsPlaylist"), FilterHarness.NonAdmin(_viewer.Id), new() { ["itemId"] = _visible.Id, ["mediaSourceId"] = (Guid?)_hiddenMovie.Id });
        Assert.IsType<NotFoundResult>(trickplay.Context.Result);
        Assert.True((await _h.RunAsync(Descriptor("TrickplayController.GetTrickplayHlsPlaylist"), FilterHarness.NonAdmin(_viewer.Id), new() { ["itemId"] = _visible.Id })).Ran);

        var guide = await _h.RunAsync(Descriptor("LiveTvController.GetLiveTvPrograms"), FilterHarness.NonAdmin(_viewer.Id), new() { ["librarySeriesId"] = (Guid?)_series.Id });
        Assert.Empty(Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(guide.Context.Result).Value).Items);
        var posted = await _h.RunAsync(Descriptor("LiveTvController.GetPrograms"), FilterHarness.NonAdmin(_viewer.Id), new() { ["body"] = new ProgramsBody { LibrarySeriesId = _series.Id } });
        Assert.False(posted.Ran);
        Assert.True((await _h.RunAsync(Descriptor("LiveTvController.GetPrograms"), FilterHarness.NonAdmin(_viewer.Id), new() { ["body"] = new ProgramsBody() })).Ran);

        var none = await _h.RunAsync(Descriptor("SessionController.Play"), FilterHarness.NonAdmin(_viewer.Id), new() { ["itemIds"] = new[] { _folder.Id } });
        Assert.IsType<NoContentResult>(none.Context.Result);
        var some = await _h.RunAsync(Descriptor("SessionController.Play"), FilterHarness.NonAdmin(_viewer.Id), new() { ["itemIds"] = new[] { _folder.Id, _visible.Id } });
        Assert.True(some.Ran);
        Assert.Equal([_visible.Id], (Guid[])some.Context.ActionArguments["itemIds"]!);
    }

    [Fact]
    public async Task TheGenericBackstop_CoversAnyOtherPluginsActionThatBindsAScopeParameter_InAnyCase()
    {
        var parentId = FilterHarness.Descriptor("Jellyfin.Plugin.Other.OtherController", "List", "Jellyfin.Plugin.Other", "GET", "Other/List", "ParentID");
        var bound = FilterHarness.Descriptor("Jellyfin.Plugin.Other.OtherController", "Seasons", "Jellyfin.Plugin.Other", "GET", "Other/Seasons", "show=seriesId");
        var none = FilterHarness.Descriptor("Jellyfin.Plugin.Other.OtherController", "Get", "Jellyfin.Plugin.Other", "GET", "Other/Get", "itemId");

        Assert.True(_h.Table.For(parentId).RuntimeBackstop);
        Assert.IsType<NotFoundResult>((await _h.RunAsync(parentId, FilterHarness.NonAdmin(_viewer.Id), new() { ["ParentID"] = _hidden.Id.ToString() })).Context.Result);
        Assert.IsType<NotFoundResult>((await _h.RunAsync(bound, FilterHarness.NonAdmin(_viewer.Id), new() { ["show"] = (Guid?)_series.Id })).Context.Result);
        Assert.True((await _h.RunAsync(parentId, FilterHarness.NonAdmin(_viewer.Id), new() { ["ParentID"] = _movies.Id })).Ran);
        Assert.True((await _h.RunAsync(parentId, FilterHarness.NonAdmin(_viewer.Id), new() { ["ParentID"] = Guid.NewGuid() })).Ran);
        Assert.Empty(_h.Table.For(none).Hardening);
        Assert.True((await _h.RunAsync(none, FilterHarness.NonAdmin(_viewer.Id), new() { ["itemId"] = _hiddenMovie.Id })).Ran);
    }

    [Fact]
    public async Task LibraryHardeningOff_LetsEveryRowThrough_StrictIsStandardInThisBuild()
    {
        // The negative control of every Standard row at once: with it off the core answers (the leaks measured on 10.11.5).
        var rows = HardeningRows().ToList();
        Assert.All(rows, row => Assert.False(_h.RunAsync(row.Descriptor, FilterHarness.NonAdmin(_viewer.Id), row.Arguments()).GetAwaiter().GetResult().Ran));

        _h.Sync.Configuration.LibraryHardening = "Off";
        Assert.All(rows, row => Assert.True(_h.RunAsync(row.Descriptor, FilterHarness.NonAdmin(_viewer.Id), row.Arguments()).GetAwaiter().GetResult().Ran, row.Name));

        foreach (var level in new[] { "Strict", "standard", "SomethingNew" })
        {
            _h.Sync.Configuration.LibraryHardening = level;
            Assert.All(rows, row => Assert.False(_h.RunAsync(row.Descriptor, FilterHarness.NonAdmin(_viewer.Id), row.Arguments()).GetAwaiter().GetResult().Ran, level + " " + row.Name));
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdministratorsApiKeysAndAnonymousCallers_AreNeverHardened_AndEnforcementOffOrSafeModeTurnsTheHardeningOff()
    {
        await _h.LockAsync(FilterHarness.EveryLock, _admin.Id, _viewer.Id);
        _h.Library.Hide(_admin, _hidden);
        foreach (var row in HardeningRows())
        {
            Assert.True((await _h.RunAsync(row.Descriptor, FilterHarness.Administrator(_admin.Id), row.Arguments())).Ran, row.Name);
            Assert.True((await _h.RunAsync(row.Descriptor, FilterHarness.ApiKey(), row.Arguments())).Ran, row.Name);
            Assert.True((await _h.RunAsync(row.Descriptor, FilterHarness.Anonymous(), row.Arguments())).Ran, row.Name);
        }

        _h.Sync.Configuration.EnforcementEnabled = false;
        Assert.True((await _h.RunAsync(Descriptor("SystemController.RestartApplication"), FilterHarness.Anonymous())).Ran);
        Assert.True((await _h.RunAsync(Descriptor("LiveTvController.AddTunerHost"), FilterHarness.NonAdmin(_viewer.Id))).Ran);
        Assert.All(HardeningRows(), row => Assert.True(_h.RunAsync(row.Descriptor, FilterHarness.NonAdmin(_viewer.Id), row.Arguments()).GetAwaiter().GetResult().Ran));

        _h.Sync.Configuration.EnforcementEnabled = true;
        Directory.CreateDirectory(Path.GetDirectoryName(_h.Sync.Paths.SafeModeFile)!);
        await File.WriteAllTextAsync(_h.Sync.Paths.SafeModeFile, string.Empty, TestContext.Current.CancellationToken);
        Assert.True((await _h.RunAsync(Descriptor("SystemController.RestartApplication"), FilterHarness.Anonymous())).Ran);
        File.Delete(_h.Sync.Paths.SafeModeFile);
        Assert.True((await _h.RunAsync(Descriptor("SystemController.RestartApplication"), FilterHarness.Anonymous())).Locked);
    }

    [Fact]
    public async Task AVisibilityLookupThatThrows_RefusesTheRequest_FailClosed_CountedAndShown()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Throws(new InvalidOperationException("The library is gone."));
        var broken = new ServerHardening(_h.UserManager.Object, new Jellyfin.Plugin.Kit.Library.LibraryAccess(library.Object, _ => []), _h.People, _h.Guard, _h.Counter);
        var filter = _h.Build(_h.UserManager.Object, hardening: broken);

        var run = await _h.RunAsync(Descriptor("UserLibraryController.GetLatestMedia"), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = _movies.Id }, filter: filter);

        Assert.True(run.Locked);
        Assert.Equal(ServerHardening.UnavailableMessage, run.Problem!.Detail);
        Assert.Equal(1, Totals()[BlockCounter.FailedClosed]);
        Assert.Contains("GetLatestMedia", (string?)_h.Status.Describe()["LastError"], StringComparison.Ordinal);

        // A signed-in caller the user manager doesn't know can't be checked either.
        Assert.True((await _h.RunAsync(Descriptor("UserLibraryController.GetLatestMedia"), FilterHarness.NonAdmin(Guid.NewGuid()), new() { ["parentId"] = _movies.Id })).Locked);
    }

    [Fact]
    public async Task AStoreThatCantBeTrusted_LocksTheSurfacesForEveryNonAdmin_AndAnUnreadableOneFailsTheLockRulesClosed()
    {
        using var directory = new Jellyfin.Plugin.Kit.TestSupport.TempDirectory();
        await File.WriteAllTextAsync(directory.Combine("accessroles.json"), "{\"Version\": 1}", TestContext.Current.CancellationToken);
        using (var held = new FileStream(directory.Combine("accessroles.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var unreadable = Jellyfin.Plugin.AccessRoles.Storage.AccessRolesStore.Create(directory.Combine("accessroles.json"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var filter = _h.Build(_h.UserManager.Object, new EffectiveRulesCache(unreadable));
            var lockRule = await _h.RunAsync(Descriptor("LiveTvController.AddTunerHost"), FilterHarness.NonAdmin(_other.Id), filter: filter);
            Assert.True(lockRule.Locked);
            Assert.Equal(ServerHardening.UnavailableMessage, lockRule.Problem!.Detail);

            // The server-wide rules need no store: the restart is refused as such, a visible parent goes on.
            Assert.Equal(ServerHardening.RestartMessage, (await _h.RunAsync(Descriptor("SystemController.RestartApplication"), FilterHarness.NonAdmin(_other.Id), filter: filter)).Problem!.Detail);
            Assert.True((await _h.RunAsync(Descriptor("UserLibraryController.GetLatestMedia"), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = _movies.Id }, filter: filter)).Ran);
        }

        await File.WriteAllTextAsync(directory.Combine("empty.json"), "{\"Version\": 99}", TestContext.Current.CancellationToken);
        using var newer = Jellyfin.Plugin.AccessRoles.Storage.AccessRolesStore.Create(directory.Combine("empty.json"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var degraded = _h.Build(_h.UserManager.Object, new EffectiveRulesCache(newer));
        Assert.True((await _h.RunAsync(Descriptor("LiveTvController.AddTunerHost"), FilterHarness.NonAdmin(_other.Id), filter: degraded)).Locked);
        Assert.False((await _h.RunAsync(Descriptor("UserController.GetUserById"), FilterHarness.NonAdmin(_other.Id), new() { ["userId"] = _viewer.Id }, filter: degraded)).Ran);
        Assert.True((await _h.RunAsync(Descriptor("LiveTvController.AddTunerHost"), FilterHarness.Administrator(_admin.Id), filter: degraded)).Ran);
    }

    [Fact]
    public async Task TheRulesHidings_AreCountedPerRule_AndTheirTimesRecorded()
    {
        await _h.RunAsync(Descriptor("UserLibraryController.GetLatestMedia"), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = _hidden.Id });
        await _h.RunAsync(Descriptor("UserLibraryController.GetLatestMedia"), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = _movies.Id });
        await _h.RunAsync(Descriptor("ItemsController.GetItems"), FilterHarness.NonAdmin(_viewer.Id), new() { ["ids"] = new[] { _visible.Id, _hiddenMovie.Id } });
        await _h.RunAsync(Descriptor("SystemController.RestartApplication"), FilterHarness.Anonymous());

        var open = (IDictionary<string, int>)_h.Counter.Describe()["OpenWindows"]!;
        Assert.Equal(1, open["Hardening.Latest Hidden"]);
        Assert.Equal(1, open["Hardening.Items Hidden"]);
        Assert.Equal(1, open["ServerWide.Restart Refused"]);
        var cost = _h.Cost.Describe();
        Assert.Equal(2L, ((IDictionary<string, object?>)cost["Hardening.Latest"]!)["Count"]);
        Assert.Equal(2, ((double[])((IDictionary<string, object?>)cost["Hardening.Latest"]!)["RecentUs"]!).Length);
        Assert.Equal(1L, ((IDictionary<string, object?>)cost["ServerWide.Restart"]!)["Count"]);

        // A rule that doesn't apply is neither run nor timed: the library hardening while it is Off, a rule for signed-in
        // callers with an anonymous one, any rule for an administrator; so a measurement's "off" requests add nothing.
        _h.Sync.Configuration.LibraryHardening = "Off";
        await _h.RunAsync(Descriptor("UserLibraryController.GetLatestMedia"), FilterHarness.NonAdmin(_viewer.Id), new() { ["parentId"] = _hidden.Id });
        await _h.RunAsync(Descriptor("UserLibraryController.GetLatestMedia"), FilterHarness.Anonymous(), new() { ["parentId"] = _hidden.Id });
        await _h.RunAsync(Descriptor("SystemController.RestartApplication"), FilterHarness.Administrator(_admin.Id));
        Assert.Equal(2L, ((IDictionary<string, object?>)_h.Cost.Describe()["Hardening.Latest"]!)["Count"]);
        Assert.Equal(1L, ((IDictionary<string, object?>)_h.Cost.Describe()["ServerWide.Restart"]!)["Count"]);
    }

    [Fact]
    public void TheCost_KeepsTheLatestWindowOfTimes_WithTheirPercentiles()
    {
        var cost = new HardeningCost();
        for (var i = 1; i <= HardeningCost.Window + 44; i++)
        {
            cost.Record("Hardening.Items", TimeSpan.FromMilliseconds(i / 10.0));
        }

        var items = (IDictionary<string, object?>)cost.Describe()["Hardening.Items"]!;
        var recent = (double[])items["RecentUs"]!;
        Assert.Equal((long)HardeningCost.Window + 44, items["Count"]);
        Assert.Equal(HardeningCost.Window, recent.Length);
        Assert.Equal((4500.0, 30000.0), (recent[0], recent[^1]));
        Assert.Equal((30.0, 28.8), ((double?)items["MaxMs"], (double?)items["P95Ms"]));
    }

    [Fact]
    public void RuleNames_AreTheCountersAndTheCostsKeys()
    {
        Assert.Equal("Locks.AdminSurfaces", ServerHardening.RuleName(new ActionManifestRule { Kind = RuleKind.LockedReject, Lock = AccessRolesLock.AdminSurfaces }));
        Assert.Equal("ServerWide.SystemInfo", ServerHardening.RuleName(new ActionManifestRule { Kind = RuleKind.SystemInfoBlank }));
        Assert.Equal("Locks.UserDirectory", ServerHardening.RuleName(new ActionManifestRule { Kind = RuleKind.UserDirectory }));
        Assert.Equal("ServerWide.ConfigurationReads", ServerHardening.RuleName(new ActionManifestRule { Kind = RuleKind.ConfigurationReadGuard }));
        Assert.Equal("Hardening.GenericBackstop", ServerHardening.RuleName(new ActionManifestRule { Kind = RuleKind.GenericBackstop }));
        Assert.Equal("Hardening.SessionReport", ServerHardening.RuleName(new ActionManifestRule { Kind = RuleKind.HardenSessionReport }));
    }

    [Fact]
    public void AnActionWithSeveralRules_IsNamedForEveryRuleThatApplies_InTheTablesOrder_NotOnlyForItsFirst()
    {
        // The cost (and a failure's count) went under the first rule's name.
        static ActionRules Rules(params RuleKind[] kinds) => new() { Key = "X.Y", Hardening = [.. kinds.Select(k => new ActionManifestRule { Kind = k })] };
        var standard = ServerHardening.Settings(new Jellyfin.Plugin.AccessRoles.Configuration.PluginConfiguration());
        var off = ServerHardening.Settings(new Jellyfin.Plugin.AccessRoles.Configuration.PluginConfiguration { LibraryHardening = "Off" });

        Assert.Equal("Hardening.Items", ServerHardening.RuleNames(Rules(RuleKind.HardenItems), _viewer.Id, standard));
        Assert.Equal("Hardening.Latest+ServerWide.ConfigurationReads", ServerHardening.RuleNames(Rules(RuleKind.HardenLatest, RuleKind.ConfigurationReadGuard), _viewer.Id, standard));
        Assert.Equal("ServerWide.ConfigurationReads", ServerHardening.RuleNames(Rules(RuleKind.HardenLatest, RuleKind.ConfigurationReadGuard), _viewer.Id, off));
        Assert.Equal("ServerWide.Restart", ServerHardening.RuleNames(Rules(RuleKind.HardenLatest, RuleKind.RestartBlock), Guid.Empty, standard));
        Assert.Equal("Hardening.Items", ServerHardening.RuleNames(Rules(RuleKind.StrictStream, RuleKind.HardenItems), _viewer.Id, standard));
        Assert.Equal("ServerWide.SystemInfo", ServerHardening.RuleNames(Rules(RuleKind.SystemInfoBlank, RuleKind.SystemInfoBlank), _viewer.Id, standard));

        // Settings that couldn't be read name every rule of the action.
        Assert.Equal("Hardening.Latest+ServerWide.ConfigurationReads", ServerHardening.RuleNames(Rules(RuleKind.HardenLatest, RuleKind.ConfigurationReadGuard), _viewer.Id, null));
    }

    [Fact]
    public async Task TheCostOfAnActionWithTwoRules_GoesUnderBothNames_AndSoDoesItsFailure()
    {
        // The build's manifest with ItemsController.GetItems given the generic backstop beside its own rule.
        using var stream = typeof(ActionManifest).Assembly.GetManifestResourceStream(Assert.Single(ActionManifest.EmbeddedResources()))!;
        var json = (await System.Text.Json.Nodes.JsonNode.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken))!;
        var getItems = json["Actions"]!.AsArray().Single(a => a!["Controller"]!.GetValue<string>() == Ns + "ItemsController" && a["Action"]!.GetValue<string>() == "GetItems")!;
        getItems["Rules"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["Kind"] = "GenericBackstop" });
        var table = new ActionRuleTable(ActionManifest.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()))));
        Assert.Equal(2, table.For(Ns + "ItemsController", "GetItems").Hardening.Count);

        await _h.RunAsync(Descriptor("ItemsController.GetItems"), FilterHarness.NonAdmin(_viewer.Id), new() { ["ids"] = new[] { _visible.Id, _hiddenMovie.Id } }, filter: _h.Build(_h.UserManager.Object, table: table));

        var cost = _h.Cost.Describe();
        Assert.Equal(1L, ((IDictionary<string, object?>)cost["Hardening.Items+Hardening.GenericBackstop"]!)["Count"]);
        Assert.False(cost.ContainsKey("Hardening.Items"));

        // A lookup that throws counts the failure under the same name.
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Throws(new InvalidOperationException("The library is gone."));
        var broken = new ServerHardening(_h.UserManager.Object, new Jellyfin.Plugin.Kit.Library.LibraryAccess(library.Object, _ => []), _h.People, _h.Guard, _h.Counter);
        var run = await _h.RunAsync(Descriptor("ItemsController.GetItems"), FilterHarness.NonAdmin(_viewer.Id), new() { ["ids"] = new[] { _hiddenMovie.Id } }, filter: _h.Build(_h.UserManager.Object, hardening: broken, table: table));
        Assert.True(run.Locked);
        var open = (IDictionary<string, int>)_h.Counter.Describe()["OpenWindows"]!;
        Assert.Equal(1, open["Hardening.Items+Hardening.GenericBackstop FailedClosed"]);
    }

    public void Dispose() => _h.Dispose();

    // A Standard row each, with what the viewer sends: every one answers without running the action.
    private IEnumerable<(string Name, Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor Descriptor, Func<Dictionary<string, object?>> Arguments)> HardeningRows()
    {
        (string, Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor, Func<Dictionary<string, object?>>) Row(string action, Func<Dictionary<string, object?>> arguments) => (action, Descriptor(action), arguments);
        yield return Row("UserLibraryController.GetLatestMedia", () => new() { ["parentId"] = _hidden.Id });
        yield return Row("ItemsController.GetItems", () => new() { ["ids"] = new[] { _hiddenMovie.Id } });
        yield return Row("ItemsController.GetItemsByUserIdLegacy", () => new() { ["parentId"] = _series.Id });
        yield return Row("TrailersController.GetTrailers", () => new() { ["ids"] = new[] { _hiddenMovie.Id } });
        yield return Row("ItemsController.GetResumeItems", () => new() { ["parentId"] = _hidden.Id });
        yield return Row("TvShowsController.GetUpcomingEpisodes", () => new() { ["parentId"] = _series.Id });
        yield return Row("TvShowsController.GetNextUp", () => new() { ["seriesId"] = _series.Id });
        yield return Row("TvShowsController.GetEpisodes", () => new() { ["seriesId"] = _series.Id });
        yield return Row("GenresController.GetGenres", () => new() { ["parentId"] = _hidden.Id });
        yield return Row("SearchController.GetSearchHints", () => new() { ["parentId"] = _hidden.Id });
        yield return Row("PersonsController.GetPersons", () => new() { ["appearsInItemId"] = _hiddenMovie.Id });
        yield return Row("PlaystateController.OnPlaybackStart", () => new() { ["itemId"] = _hiddenMovie.Id });
        yield return Row("SessionController.ReportViewing", () => new() { ["itemId"] = _hiddenMovie.Id.ToString("N") });
        yield return Row("TrickplayController.GetTrickplayHlsPlaylist", () => new() { ["itemId"] = _hiddenMovie.Id });
        yield return Row("LiveTvController.GetLiveTvPrograms", () => new() { ["librarySeriesId"] = (Guid?)_series.Id });
        yield return Row("SessionController.Play", () => new() { ["itemIds"] = new[] { _hiddenMovie.Id } });
        yield return Row("TvShowsController.GetSeasons", () => new() { ["seriesId"] = _series.Id });
        yield return Row("MoviesController.GetMovieRecommendations", () => new() { ["parentId"] = _subView.Id });
    }

    private Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor Descriptor(string action)
        => _h.ManifestDescriptor(Ns + action[..action.IndexOf('.', StringComparison.Ordinal)], action[(action.IndexOf('.', StringComparison.Ordinal) + 1)..]);

    private Guid Id(string which) => which switch
    {
        "hidden" => _hidden.Id,
        "series" => _series.Id,
        "season" => _season.Id,
        "folder" => _folder.Id,
        "subView" => _subView.Id,
        "episode" => _episode.Id,
        _ => throw new ArgumentOutOfRangeException(nameof(which))
    };

    private IDictionary<string, long> Totals() => (IDictionary<string, long>)_h.Counter.Describe()["Totals"]!;

    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType(), JsonDefaults.Options);

    // Stands for Jellyfin.Api's GetProgramsDto, which the rule reads by its property's name.
    private sealed class ProgramsBody
    {
        public Guid? LibrarySeriesId { get; set; }
    }
}
