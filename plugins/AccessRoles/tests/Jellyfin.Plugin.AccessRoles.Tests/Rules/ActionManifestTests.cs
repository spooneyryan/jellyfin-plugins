using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.Kit.Hosting;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// The action manifest of Jellyfin 10.11.5: every controller action of
/// Jellyfin.Api is in it, each with its rules or a reviewed reason for none, checked against a real <c>Jellyfin.Api.dll</c>
/// read from its metadata (<see cref="JellyfinApiDll"/>: CI's build of the tag, or the installed server's copy, never
/// executed), so a Jellyfin update that adds, renames or re-routes an action, changes its policy or renames a parameter,
/// fails here until it is reviewed.
/// </summary>
public sealed class ActionManifestTests
{
    private const string Ns = "Jellyfin.Api.Controllers.";

    /// <summary>
    /// The rule rows of the settings locks and the library leak hardening, with the corrections the 10.11.5 sandbox checks
    /// made and the rows a review added, written out here once more, so a changed rule in the JSON is a deliberate change in
    /// two places; and with each the arguments the rule reads. The rewrite rules pass through when their argument is
    /// missing, so a parameter Jellyfin
    /// renames must fail <see cref="TheArgumentsTheRulesRead_AreParametersOfTheirActions"/> (and the DLL comparison), never
    /// switch a rule off unseen. An empty list: the rule reads only the caller, its locks or the response.
    /// </summary>
    private static readonly Dictionary<string, (string Rules, string[] Arguments)> ExpectedRules = new(StringComparer.Ordinal)
    {
        // The settings locks.
        ["UserController.UpdateUserPassword"] = ("LockedReject(Profile)", []),
        ["UserController.UpdateUserPasswordLegacy"] = ("LockedReject(Profile)", []),
        ["ImageController.PostUserImage"] = ("LockedReject(Profile)", []),
        ["ImageController.PostUserImageLegacy"] = ("LockedReject(Profile)", []),
        ["ImageController.PostUserImageByIndexLegacy"] = ("LockedReject(Profile)", []),
        ["ImageController.DeleteUserImage"] = ("LockedReject(Profile)", []),
        ["ImageController.DeleteUserImageLegacy"] = ("LockedReject(Profile)", []),
        ["ImageController.DeleteUserImageByIndexLegacy"] = ("LockedReject(Profile)", []),
        ["UserController.UpdateUser"] = ("UserUpdateMerge, PerUserLock", ["updateUser", "userId"]),
        ["UserController.UpdateUserLegacy"] = ("UserUpdateMerge, PerUserLock", ["updateUser", "userId"]),
        ["UserController.UpdateUserConfiguration"] = ("UserConfigurationMerge", ["userConfig", "userId"]),
        ["UserController.UpdateUserConfigurationLegacy"] = ("UserConfigurationMerge", ["userConfig", "userId"]),
        ["DisplayPreferencesController.UpdateDisplayPreferences"] = ("DisplayPreferencesMerge", ["client", "displayPreferences", "displayPreferencesId", "userId"]),
        ["QuickConnectController.AuthorizeQuickConnect"] = ("LockedReject(QuickConnect)", []),

        // Server-wide hardening.
        ["ConfigurationController.GetConfiguration"] = ("ConfigurationReadGuard", []),
        ["ConfigurationController.GetNamedConfiguration"] = ("ConfigurationReadGuard", ["key"]),
        ["SystemController.RestartApplication"] = ("RestartBlock", []),
        ["UserController.GetPublicUsers"] = ("PublicUsersTrim", []),

        // AdminSurfaces and UserDirectory.
        ["LiveTvController.AddTunerHost"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.DeleteTunerHost"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.AddListingProvider"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.DeleteListingProvider"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.SetChannelMapping"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.DiscoverTuners"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.ResetTuner"] = ("LockedReject(AdminSurfaces)", []),
        ["LibraryController.PostUpdatedSeries"] = ("LockedReject(AdminSurfaces)", []),
        ["LibraryController.PostUpdatedMovies"] = ("LockedReject(AdminSurfaces)", []),
        ["LibraryController.PostUpdatedMedia"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetBookRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetBoxSetRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetMovieRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetMusicAlbumRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetMusicArtistRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetMusicVideoRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetSeriesRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["ItemLookupController.GetTrailerRemoteSearchResults"] = ("LockedReject(AdminSurfaces)", []),
        ["SystemController.GetSystemInfo"] = ("SystemInfoBlank", []),
        ["UserController.GetUsers"] = ("UserDirectory", []),
        ["UserController.GetUserById"] = ("UserDirectory", ["userId"]),

        // Admin and API-key callers, sign-ins, library structure (the library sync's triggers).
        ["UserController.UpdateUserPolicy"] = ("UserPolicyPreMerge", ["newPolicy", "userId"]),
        ["UserController.DeleteUser"] = ("PerUserLock", ["userId"]),
        ["UserController.AuthenticateUser"] = ("SignInHook", []),
        ["UserController.AuthenticateUserByName"] = ("SignInHook", []),
        ["UserController.AuthenticateWithQuickConnect"] = ("SignInHook", []),
        ["LibraryStructureController.AddVirtualFolder"] = ("LibraryStructureHook", ["name", "refreshLibrary"]),
        ["LibraryStructureController.RemoveVirtualFolder"] = ("LibraryStructureHook", ["name", "refreshLibrary"]),
        ["LibraryStructureController.RenameVirtualFolder"] = ("LibraryStructureHook", ["name", "newName", "refreshLibrary"]),
        ["LibraryStructureController.AddMediaPath"] = ("LibraryStructureHook", ["mediaPathDto", "refreshLibrary"]),
        ["LibraryStructureController.UpdateMediaPath"] = ("LibraryStructureHook", ["mediaPathRequestDto"]),
        ["LibraryStructureController.RemoveMediaPath"] = ("LibraryStructureHook", ["name", "path", "refreshLibrary"]),
        ["LibraryStructureController.UpdateLibraryOptions"] = ("LibraryStructureHook", ["request"]),

        // Library leak hardening, Standard, with the GetPlaylist row the 10.11.5 sandbox measurement added.
        ["UserLibraryController.GetLatestMedia"] = ("HardenLatest", ["parentId"]),
        ["UserLibraryController.GetLatestMediaLegacy"] = ("HardenLatest", ["parentId"]),
        ["ItemsController.GetItems"] = ("HardenItems", ["ids", "parentId"]),
        ["ItemsController.GetItemsByUserIdLegacy"] = ("HardenItems", ["ids", "parentId"]),
        ["TrailersController.GetTrailers"] = ("HardenItems", ["ids", "parentId"]),
        ["ItemsController.GetResumeItems"] = ("HardenParent", ["parentId"]),
        ["ItemsController.GetResumeItemsLegacy"] = ("HardenParent", ["parentId"]),
        ["TvShowsController.GetUpcomingEpisodes"] = ("HardenUpcoming", ["parentId"]),
        ["TvShowsController.GetNextUp"] = ("HardenNextUp", ["parentId", "seriesId"]),
        ["TvShowsController.GetEpisodes"] = ("HardenEpisodes", ["seasonId", "seriesId"]),
        ["PlaylistsController.GetPlaylistItems"] = ("HardenPlaylistItems", ["userId"]),
        ["PlaylistsController.GetPlaylist"] = ("HardenPlaylist", []),
        ["GenresController.GetGenres"] = ("HardenAggregates", ["parentId"]),
        ["StudiosController.GetStudios"] = ("HardenAggregates", ["parentId"]),
        ["MusicGenresController.GetMusicGenres"] = ("HardenAggregates", ["parentId"]),
        ["ArtistsController.GetArtists"] = ("HardenAggregates", ["parentId"]),
        ["ArtistsController.GetAlbumArtists"] = ("HardenAggregates", ["parentId"]),
        ["YearsController.GetYears"] = ("HardenAggregates", ["parentId"]),
        ["FilterController.GetQueryFilters"] = ("HardenAggregates", ["parentId"]),
        ["FilterController.GetQueryFiltersLegacy"] = ("HardenAggregates", ["parentId"]),
        ["SearchController.GetSearchHints"] = ("HardenAggregates", ["parentId"]),
        ["PersonsController.GetPersons"] = ("HardenPeople", ["appearsInItemId", "searchTerm"]),

        // Library leak hardening, Standard, the rows a review added: ids the core resolves without a user.
        ["PlaystateController.ReportPlaybackStart"] = ("HardenSessionReport", ["playbackStartInfo"]),
        ["PlaystateController.ReportPlaybackProgress"] = ("HardenSessionReport", ["playbackProgressInfo"]),
        ["PlaystateController.ReportPlaybackStopped"] = ("HardenSessionReport", ["playbackStopInfo"]),
        ["PlaystateController.OnPlaybackStart"] = ("HardenSessionReport", ["itemId"]),
        ["PlaystateController.OnPlaybackStartLegacy"] = ("HardenSessionReport", ["itemId"]),
        ["PlaystateController.OnPlaybackProgress"] = ("HardenSessionReport", ["itemId"]),
        ["PlaystateController.OnPlaybackProgressLegacy"] = ("HardenSessionReport", ["itemId"]),
        ["PlaystateController.OnPlaybackStopped"] = ("HardenSessionReport", ["itemId"]),
        ["PlaystateController.OnPlaybackStoppedLegacy"] = ("HardenSessionReport", ["itemId"]),
        ["SessionController.ReportViewing"] = ("HardenSessionReport", ["itemId"]),
        ["SessionController.GetSessions"] = ("HardenSessions", []),
        ["TrickplayController.GetTrickplayHlsPlaylist"] = ("HardenItem", ["itemId", "mediaSourceId"]),

        // The eleven actions the first review noted, each given its rule: Dashboard data and remote searches under
        // AdminSurfaces, the guide's library series and the remote play command's ids as hardening.
        ["LibraryController.GetLibraryOptionsInfo"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.GetChannelMappingOptions"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.GetDefaultListingProvider"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.GetLineups"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.GetSchedulesDirectCountries"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.GetTunerHostTypes"] = ("LockedReject(AdminSurfaces)", []),
        ["RemoteImageController.GetRemoteImageProviders"] = ("LockedReject(AdminSurfaces)", []),
        ["RemoteImageController.GetRemoteImages"] = ("LockedReject(AdminSurfaces)", []),
        ["LiveTvController.GetLiveTvPrograms"] = ("HardenLibrarySeries", ["librarySeriesId"]),
        ["LiveTvController.GetPrograms"] = ("HardenLibrarySeries", ["body"]),
        ["SessionController.Play"] = ("HardenPlayCommand", ["itemIds"]),

        // The generic backstop for the scope-binding actions without a row of their own.
        ["CollectionController.CreateCollection"] = ("GenericBackstop", ["parentId"]),
        ["MoviesController.GetMovieRecommendations"] = ("GenericBackstop", ["parentId"]),
        ["TvShowsController.GetSeasons"] = ("GenericBackstop", ["seriesId"]),

        // Strict hardening (planned): streams. The universal audio route 404s natively (a regression).
        ["DynamicHlsController.GetHlsAudioSegment"] = ("StrictStream", ["itemId"]),
        ["DynamicHlsController.GetHlsVideoSegment"] = ("StrictStream", ["itemId"]),
        ["DynamicHlsController.GetLiveHlsStream"] = ("StrictStream", ["itemId"]),
        ["DynamicHlsController.GetMasterHlsAudioPlaylist"] = ("StrictStream", ["itemId"]),
        ["DynamicHlsController.GetMasterHlsVideoPlaylist"] = ("StrictStream", ["itemId"]),
        ["DynamicHlsController.GetVariantHlsAudioPlaylist"] = ("StrictStream", ["itemId"]),
        ["DynamicHlsController.GetVariantHlsVideoPlaylist"] = ("StrictStream", ["itemId"]),
        ["VideosController.GetVideoStream"] = ("StrictStream", ["itemId"]),
        ["VideosController.GetVideoStreamByContainer"] = ("StrictStream", ["itemId"]),
        ["AudioController.GetAudioStream"] = ("StrictStream", ["itemId"]),
        ["AudioController.GetAudioStreamByContainer"] = ("StrictStream", ["itemId"]),
    };

    // The hardening kinds; an action that binds a scope parameter carries one of them.
    private static readonly HashSet<RuleKind> ScopeKinds =
    [
        RuleKind.HardenLatest, RuleKind.HardenItems, RuleKind.HardenParent, RuleKind.HardenUpcoming, RuleKind.HardenNextUp,
        RuleKind.HardenEpisodes, RuleKind.HardenAggregates, RuleKind.GenericBackstop, RuleKind.BackstopExempt
    ];

    private static readonly Lazy<ActionManifest> Manifest = new(ActionManifest.ForBuild);

    [Fact]
    public void TheBuild_EmbedsOneManifest_ForJellyfin1011_Reviewed_On1011Point5()
    {
        Assert.Equal(["ActionManifest.jellyfin-api-10.11.5.json"], ActionManifest.EmbeddedResources());
        var manifest = Manifest.Value;

        Assert.Equal(ActionManifest.CurrentFormat, manifest.Format);
        Assert.Equal(KitBuild.JellyfinTarget, manifest.JellyfinTarget);
        Assert.Equal("10.11", manifest.JellyfinTarget);
        Assert.Equal("10.11.5", manifest.JellyfinVersion);
        Assert.Equal("Jellyfin.Api", manifest.Assembly);
        Assert.Equal("1e27f460fe741429a1200b3ceb524d34e5d524ce", Commit(manifest));
    }

    [Fact]
    public void TheManifest_ListsExactlyTheActionsOfJellyfinApi_WithTheirRoutesPoliciesAndParameters()
    {
        var path = JellyfinApiDll.Locate();
        if (path is null)
        {
            SkipOrFailInCi($"No Jellyfin.Api.dll to check against: set {JellyfinApiDll.Variable} (CI builds it from the tag; plugins/AccessRoles/DEVELOPMENT.md).");
        }

        var dll = JellyfinApiMetadata.Read(path!);
        var manifest = Manifest.Value;

        Assert.Equal(manifest.Assembly, dll.Name);
        var commit = Commit(manifest);
        var isTheTag = dll.InformationalVersion?.Contains(commit, StringComparison.OrdinalIgnoreCase) == true;
        if (!isTheTag && Environment.GetEnvironmentVariable(JellyfinApiDll.Variable) is null)
        {
            // The installed server moved on: its copy is no longer the reviewed build. CI's (named by the variable) must be.
            SkipOrFailInCi($"The installed server's Jellyfin.Api.dll is {dll.InformationalVersion}, not v{manifest.JellyfinVersion} (commit {commit}); set {JellyfinApiDll.Variable} to a build of the tag.");
        }

        Assert.True(isTheTag, $"{path} is {dll.InformationalVersion}; the manifest reviews v{manifest.JellyfinVersion}, commit {commit}.");
        var comparison = manifest.Compare(dll.Actions);
        Assert.True(
            comparison.Matches,
            "The manifest and the DLL differ:" + Environment.NewLine
            + string.Join(Environment.NewLine, comparison.Missing.Select(k => "  missing from the DLL (renamed or removed): " + k)
                .Concat(comparison.Unreviewed.Select(k => "  not in the manifest (new, unreviewed): " + k))
                .Concat(comparison.Changed.Select(c => "  changed: " + c))
                .Concat(comparison.Duplicates.Select(k => "  two actions of one name: " + k))));
        Assert.Equal(390, comparison.Compared);
    }

    [Fact]
    public void EveryAction_IsReviewed()
    {
        var problems = Manifest.Value.Actions
            .Where(a => string.IsNullOrWhiteSpace(a.Review) || a.Review.Contains(ActionManifestWriter.UnreviewedMarker, StringComparison.Ordinal))
            .Select(a => a.Key)
            .ToList();

        Assert.True(problems.Count == 0, "Not reviewed: " + string.Join(", ", problems));
    }

    [Fact]
    public void TheRules_AreTheExpectedRuleRows()
    {
        var actual = Manifest.Value.Actions.Where(a => a.Rules.Count > 0)
            .ToDictionary(a => a.Key[Ns.Length..], a => string.Join(", ", a.Rules), StringComparer.Ordinal);

        Assert.Equal(
            ExpectedRules.Select(p => KeyValuePair.Create(p.Key, p.Value.Rules)).OrderBy(p => p.Key, StringComparer.Ordinal),
            actual.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(110, actual.Count);
        Assert.Equal(112, Manifest.Value.Actions.Sum(a => a.Rules.Count));
    }

    [Fact]
    public void TheArgumentsTheRulesRead_AreParametersOfTheirActions()
    {
        // Each argument a rule reads is a parameter its action binds, by the name MVC gives the argument; with the
        // DLL test's parameter comparison, a Jellyfin rename fails a test instead of a rule passing through for good.
        var problems = new List<string>();
        foreach (var (key, (_, arguments)) in ExpectedRules)
        {
            var entry = Manifest.Value.Find(Ns + key[..key.IndexOf('.', StringComparison.Ordinal)], key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..]);
            var names = entry?.Parameters.Select(ApiActionShape.ArgumentName).ToHashSet(StringComparer.Ordinal) ?? [];
            problems.AddRange(arguments.Where(a => !names.Contains(a)).Select(a => $"{key} has no parameter {a} (it has {string.Join(", ", names)})"));
            if (arguments.Distinct(StringComparer.Ordinal).Count() != arguments.Length || !arguments.SequenceEqual(arguments.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                problems.Add(key + ": list the arguments once each, in ordinal order");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void LockedReject_NamesItsLock_AndNoOtherRuleDoes()
    {
        var rules = Manifest.Value.Actions.SelectMany(a => a.Rules.Select(r => (a.Key, Rule: r))).ToList();

        Assert.All(rules, r => Assert.True(r.Rule.Kind == RuleKind.LockedReject == r.Rule.Lock.HasValue, r.Key + ": " + r.Rule));
        Assert.Equal(
            [AccessRolesLock.Profile, AccessRolesLock.QuickConnect, AccessRolesLock.AdminSurfaces],
            rules.Where(r => r.Rule.Lock.HasValue).Select(r => r.Rule.Lock!.Value).Distinct().Order());
        Assert.All(rules.GroupBy(r => r.Key), g => Assert.True(g.Select(r => r.Rule.Kind).Distinct().Count() == g.Count(), g.Key + " lists a rule twice."));
    }

    [Fact]
    public void EveryRuleKind_IsUsed_ButTheBackstopExemption()
    {
        var used = Manifest.Value.Actions.SelectMany(a => a.Rules).Select(r => r.Kind).ToHashSet();

        Assert.Equal(Enum.GetValues<RuleKind>().Where(k => k != RuleKind.BackstopExempt).Order(), used.Order());
    }

    [Fact]
    public void ScopeBindingActions_SayHowTheBackstopTreatsThem()
    {
        // An action that binds parentId, seriesId or seasonId has a hardening row, the generic backstop, or an
        // exemption with its reason (per-action exceptions are listed in the action manifest).
        var actions = Manifest.Value.Actions.Where(a => a.ToShape().ScopeParameters.Count > 0).ToList();
        var problems = actions.Where(a => !a.Rules.Any(r => ScopeKinds.Contains(r.Kind))).Select(a => a.Key).ToList();

        Assert.True(problems.Count == 0, "Scope parameters without a hardening rule: " + string.Join(", ", problems));
        Assert.Equal(22, actions.Count);
    }

    [Fact]
    public void TheLiveTvManagementActions_AreFourteen_SevenOfThemAdminSurfaces()
    {
        // The seven server-configuration actions are AdminSurfaces, the seven DVR actions stay native.
        var actions = Manifest.Value.Actions.Where(a => a.Policies.Contains("LiveTvManagement")).ToList();

        Assert.Equal(14, actions.Count);
        Assert.Equal(
            ["AddListingProvider", "AddTunerHost", "DeleteListingProvider", "DeleteTunerHost", "DiscoverTuners", "ResetTuner", "SetChannelMapping"],
            actions.Where(a => a.Rules.Count > 0).Select(a => a.Action).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["CancelSeriesTimer", "CancelTimer", "CreateSeriesTimer", "CreateTimer", "DeleteRecording", "UpdateSeriesTimer", "UpdateTimer"],
            actions.Where(a => a.Rules.Count == 0).Select(a => a.Action).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnonymousActions_AreReviewedAsSuch()
    {
        // Every action a caller without a token reaches (no [Authorize], or [AllowAnonymous]) is either a rule that
        // covers anonymous callers, a stream, or reviewed as anonymous by design or not closable.
        var anonymousRules = new[] { RuleKind.RestartBlock, RuleKind.PublicUsersTrim, RuleKind.SignInHook, RuleKind.StrictStream };
        var problems = Manifest.Value.Actions
            .Where(a => a.ToShape().IsAnonymous)
            .Where(a => !a.Rules.Any(r => anonymousRules.Contains(r.Kind)) && !a.Review.Contains("nonymous", StringComparison.Ordinal))
            .Select(a => a.Key)
            .ToList();

        Assert.True(problems.Count == 0, "Anonymous actions whose review doesn't say so: " + string.Join(", ", problems));
    }

    [Fact]
    public void TheElevenActionsTheFirstReviewNoted_AreReviewed_EachWithItsRule()
    {
        // The manifest's first review noted these for a later review, without a rule (its wording "noted for a later
        // review"); that review gave each one a rule, and its note marks them.
        string[] eleven =
        [
            "LibraryController.GetLibraryOptionsInfo", "LiveTvController.GetChannelMappingOptions", "LiveTvController.GetDefaultListingProvider",
            "LiveTvController.GetLineups", "LiveTvController.GetLiveTvPrograms", "LiveTvController.GetPrograms", "LiveTvController.GetSchedulesDirectCountries",
            "LiveTvController.GetTunerHostTypes", "RemoteImageController.GetRemoteImageProviders", "RemoteImageController.GetRemoteImages", "SessionController.Play"
        ];

        Assert.DoesNotContain(Manifest.Value.Actions, a => a.Review.Contains("noted for a later review", StringComparison.Ordinal));
        Assert.Equal(
            eleven,
            Manifest.Value.Actions.Where(a => a.Review.Contains("one of the eleven actions the first review noted", StringComparison.Ordinal)).Select(a => a.Key[Ns.Length..]).Order(StringComparer.Ordinal));
        Assert.All(eleven, key => Assert.Equal(ExpectedRules[key].Rules, string.Join(", ", Manifest.Value.Find(Ns + key[..key.IndexOf('.', StringComparison.Ordinal)], key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..])!.Rules)));
    }

    [Fact]
    public void TheEmbeddedFile_IsInItsCanonicalForm()
    {
        // One action per line, so the candidate after a Jellyfin update (ActionManifestCandidateTests) diffs line by line.
        using var stream = typeof(ActionManifest).Assembly.GetManifestResourceStream("ActionManifest.jellyfin-api-10.11.5.json")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();

        Assert.Equal(ActionManifestWriter.Write(Manifest.Value, Manifest.Value.Actions), text);
    }

    [Fact]
    public void Compare_TellsMissingUnreviewedChangedAndDuplicateActionsApart()
    {
        var manifest = Manifest.Value;
        var shapes = manifest.Actions.Select(a => a.ToShape()).ToList();
        Assert.True(manifest.Compare(shapes).Matches);

        var first = shapes[0];
        var renamed = shapes.Skip(1).Append(new ApiActionShape(first.Controller, first.Action + "Renamed", first.Routes, first.Policies, first.AllowAnonymous, first.Parameters)).ToList();
        var opened = shapes.Select(s => s.Key == first.Key ? new ApiActionShape(s.Controller, s.Action, s.Routes, [], allowAnonymous: true, s.Parameters) : s).ToList();
        var twice = shapes.Append(first).ToList();

        var renamedResult = manifest.Compare(renamed);
        Assert.Equal([first.Key], renamedResult.Missing);
        Assert.Equal([first.Key + "Renamed"], renamedResult.Unreviewed);
        var openedResult = manifest.Compare(opened);
        var opening = Assert.Single(openedResult.Changed);
        Assert.Contains("policies: reviewed [RequiresElevation], now []", opening, StringComparison.Ordinal);
        Assert.Contains("[AllowAnonymous]: reviewed False, now True", opening, StringComparison.Ordinal);
        Assert.False(openedResult.Matches);
        Assert.Equal([first.Key], manifest.Compare(twice).Duplicates);
    }

    [Fact]
    public void Compare_TellsARenamedParameter_AsAChangedAction()
    {
        // Regression: UserConfigurationMerge reads userConfig; were it renamed, the rule would pass through (a rewrite
        // rule passes through when its argument is missing), so the rename must show as a changed action, in the tests and
        // in Diagnostics.
        var manifest = Manifest.Value;
        var key = Ns + "UserController.UpdateUserConfiguration";
        var shapes = manifest.Actions
            .Select(a => a.ToShape())
            .Select(s => s.Key == key ? new ApiActionShape(s.Controller, s.Action, s.Routes, s.Policies, s.AllowAnonymous, ["configuration", "userId"]) : s)
            .ToList();

        var result = manifest.Compare(shapes);

        Assert.Equal(key + ": parameters: reviewed [userConfig; userId], now [configuration; userId]", Assert.Single(result.Changed));
        Assert.Empty(result.Missing);
        Assert.Empty(result.Unreviewed);
    }

    [Fact]
    public void AParameter_IsWrittenWithTheNameItIsBoundFrom_WhenThatDiffers()
    {
        Assert.Equal("parentId", ApiActionShape.FormatParameter("parentId", null));
        Assert.Equal("parentId", ApiActionShape.FormatParameter("parentId", string.Empty));
        Assert.Equal("parentId", ApiActionShape.FormatParameter("parentId", "parentId"));
        Assert.Equal("series=SeriesId", ApiActionShape.FormatParameter("series", "SeriesId"));
        Assert.Equal(("series", "SeriesId"), (ApiActionShape.ArgumentName("series=SeriesId"), ApiActionShape.BoundName("series=SeriesId")));
        Assert.Equal(("parentId", "parentId"), (ApiActionShape.ArgumentName("parentId"), ApiActionShape.BoundName("parentId")));
        Assert.Equal(["SeriesId", "parentId"], ApiActionShape.ScopeParametersOf(["series=SeriesId", "parentId", "parentIdNot=other", "seasonIds", "limit"]));
    }

    [Fact]
    public void ALoad_RefusesUnknownRuleKindsLocksAndFields()
    {
        static ActionManifest LoadText(string actions) => ActionManifest.Load(new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"Format\":\"accessroles-action-manifest/1\",\"JellyfinTarget\":\"10.11\",\"JellyfinVersion\":\"10.11.5\",\"Source\":\"s\",\"Assembly\":\"Jellyfin.Api\",\"Actions\":[" + actions + "]}")));

        Assert.Single(LoadText("{\"Controller\":\"C\",\"Action\":\"A\",\"Routes\":[],\"Policies\":[],\"Parameters\":[\"id\"],\"Rules\":[{\"Kind\":\"LockedReject\",\"Lock\":\"Profile\"}],\"Review\":\"r\"}").Actions);
        Assert.Throws<InvalidDataException>(() => LoadText("{\"Controller\":\"C\",\"Action\":\"A\",\"Rules\":[{\"Kind\":\"Nonsense\"}],\"Review\":\"r\"}"));
        Assert.Throws<InvalidDataException>(() => LoadText("{\"Controller\":\"C\",\"Action\":\"A\",\"Rules\":[{\"Kind\":\"LockedReject\",\"Lock\":\"Garden\"}],\"Review\":\"r\"}"));
        Assert.Throws<InvalidDataException>(() => LoadText("{\"Controller\":\"C\",\"Action\":\"A\",\"Rules\":[{\"Kind\":0}],\"Review\":\"r\"}"));
        Assert.Throws<InvalidDataException>(() => LoadText("{\"Controller\":\"C\",\"Action\":\"A\",\"Typo\":1,\"Review\":\"r\"}"));
        Assert.Throws<InvalidDataException>(() => LoadText("{\"Controller\":\"C\",\"Action\":\"A\",\"ScopeParameters\":[\"parentId\"],\"Review\":\"r\"}"));
        Assert.Throws<InvalidDataException>(() => LoadText("{\"Controller\":\"C\",\"Action\":\"A\",\"Review\":\"r\"},{\"Controller\":\"C\",\"Action\":\"A\",\"Review\":\"r\"}"));
        Assert.Throws<InvalidDataException>(() => ActionManifest.Load(new MemoryStream(Encoding.UTF8.GetBytes("{\"Format\":\"other/1\",\"JellyfinTarget\":\"10.11\",\"JellyfinVersion\":\"v\",\"Source\":\"s\",\"Assembly\":\"a\",\"Actions\":[]}"))));
        Assert.Throws<InvalidDataException>(() => ActionManifest.ForTarget("12"));
    }

    // A local run without the reviewed DLL skips; CI must check, so there it fails.
    private static void SkipOrFailInCi(string message)
    {
        if (JellyfinApiDll.InCi())
        {
            Assert.Fail($"{message} {JellyfinApiDll.CiVariable} is set, so the manifest check can't be skipped.");
        }

        Assert.Skip(message);
    }

    private static string Commit(ActionManifest manifest)
        => Regex.Match(manifest.Source, "commit ([0-9a-f]{40})").Groups[1].Value;
}
