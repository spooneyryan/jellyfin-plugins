using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// The rule table's self-check (<c>RuleTableSelfCheck</c>): the filter's registration, the
/// manifest's rule rows against the actions the server routes, Jellyfin Enhanced's saves (a new one listed unreviewed),
/// plugin controllers counted, and those that take a Jellyfin controller flagged. Against a running server the sandbox
/// probe runs it (all 99 rule rows bound on 10.11.5).
/// </summary>
public sealed class RuleTableSelfCheckTests
{
    private readonly ActionRuleTable _table = new(ActionManifest.ForBuild());

    [Fact]
    public void WithoutJellyfinApi_EveryRuleRowIsMissing_AndEnforcementIsNotActive()
    {
        var result = RuleTableSelfCheck.Check(_table, [], filterRegistered: true, DateTime.UnixEpoch);

        Assert.Equal((false, 0, 110), (result["EnforcementActive"], result["Bound"], result["WithRules"]));
        Assert.Equal(110, ((IReadOnlyCollection<string>)result["Missing"]!).Count);
    }

    [Fact]
    public void JellyfinEnhancedsSaves_AreListed_ANewOneAsUnreviewed_AndPluginControllersAreCounted()
    {
        const string Je = ActionRuleTable.JellyfinEnhancedController;
        ControllerActionDescriptor[] descriptors =
        [
            FilterHarness.Descriptor(Je, "SaveUserSettingsSettings", "Jellyfin.Plugin.JellyfinEnhanced", "POST", "JellyfinEnhanced/user-settings/{userId}/settings.json"),
            FilterHarness.Descriptor(Je, "SaveUserBookmark", "Jellyfin.Plugin.JellyfinEnhanced", "POST", "JellyfinEnhanced/user-settings/{userId}/bookmark.json"),
            FilterHarness.Descriptor(Je, "SaveUserTheme", "Jellyfin.Plugin.JellyfinEnhanced", "POST", "JellyfinEnhanced/user-settings/{userId}/theme.json"),
            FilterHarness.Descriptor(Je, "GetUserSettings", "Jellyfin.Plugin.JellyfinEnhanced", "GET", "JellyfinEnhanced/user-settings/{userId}/settings.json"),
            FilterHarness.Descriptor("Jellyfin.Plugin.Other.OtherController", "Get", "Jellyfin.Plugin.Other", "GET", "Other")
        ];

        var result = RuleTableSelfCheck.Check(_table, descriptors, filterRegistered: false, DateTime.UnixEpoch);

        var je = (IDictionary<string, object?>)result["JellyfinEnhanced"]!;
        Assert.Equal(true, je["Installed"]);
        Assert.Equal(["SaveUserSettingsSettings", "SaveUserTheme"], (IEnumerable<string>)je["Locked"]!);
        Assert.Equal(["SaveUserTheme"], (IEnumerable<string>)je["Unreviewed"]!);
        var plugins = (IDictionary<string, int>)result["PluginActions"]!;
        Assert.Equal((4, 1), (plugins["Jellyfin.Plugin.JellyfinEnhanced"], plugins["Jellyfin.Plugin.Other"]));
        Assert.Equal(false, result["FilterRegistered"]);
        Assert.Equal(false, result["EnforcementActive"]);
        Assert.NotNull(result["Classification"]);
    }

    [Fact]
    public void JellyfinEnhancedsPostsThatNoRuleCovers_AreListed_TheUnreviewedOnesApart()
    {
        // 12.14's spoiler-blur/user-prefs locked, and every other authenticated JE POST no rule covers
        // listed, so a new one is seen; 12.14's ddd-triggers.json save reviewed.
        const string Je = ActionRuleTable.JellyfinEnhancedController;
        const string Assembly = "Jellyfin.Plugin.JellyfinEnhanced";
        var anonymous = FilterHarness.Descriptor(Je, "PublicPost", Assembly, "POST", "JellyfinEnhanced/public/thing");
        anonymous.EndpointMetadata = [new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute()];
        ControllerActionDescriptor[] descriptors =
        [
            FilterHarness.Descriptor(Je, "SaveUserSettingsDdd", Assembly, "POST", "JellyfinEnhanced/user-settings/{userId}/ddd-triggers.json"),
            FilterHarness.Descriptor(Je, "SetSpoilerBlurUserPrefs", Assembly, "POST", "JellyfinEnhanced/spoiler-blur/user-prefs"),
            FilterHarness.Descriptor(Je, "SaveUserBookmark", Assembly, "POST", "JellyfinEnhanced/user-settings/{userId}/bookmark.json"),
            FilterHarness.Descriptor(Je, "HideFromContinueWatching", Assembly, "POST", "JellyfinEnhanced/continue-watching/hide/{itemId}"),
            FilterHarness.Descriptor(Je, "DismissWhatsNew", Assembly, "POST", "JellyfinEnhanced/whats-new/dismiss"),
            FilterHarness.Descriptor(Je, "EnableSpoilerBlurForSeries", Assembly, "POST", "JellyfinEnhanced/spoiler-blur/series/{seriesId}"),
            FilterHarness.Descriptor(Je, "DisableSpoilerBlurForSeries", Assembly, "DELETE", "JellyfinEnhanced/spoiler-blur/series/{seriesId}"),
            FilterHarness.Descriptor(Je, "GetSpoilerBlurUserPrefs", Assembly, "GET", "JellyfinEnhanced/spoiler-blur/user-prefs"),
            anonymous
        ];

        var je = (IDictionary<string, object?>)RuleTableSelfCheck.Check(_table, descriptors, filterRegistered: true, DateTime.UnixEpoch)["JellyfinEnhanced"]!;

        Assert.Equal(["SaveUserSettingsDdd", "SetSpoilerBlurUserPrefs"], (IEnumerable<string>)je["Locked"]!);
        Assert.Empty((IEnumerable<string>)je["Unreviewed"]!);
        Assert.Equal(
            [
                "DisableSpoilerBlurForSeries (DELETE JellyfinEnhanced/spoiler-blur/series/{seriesId})",
                "DismissWhatsNew (POST JellyfinEnhanced/whats-new/dismiss)",
                "EnableSpoilerBlurForSeries (POST JellyfinEnhanced/spoiler-blur/series/{seriesId})",
                "HideFromContinueWatching (POST JellyfinEnhanced/continue-watching/hide/{itemId})",
                "SaveUserBookmark (POST JellyfinEnhanced/user-settings/{userId}/bookmark.json)"
            ],
            (IEnumerable<string>)je["Uncovered"]!);

        // The per-item Spoiler Guard marks are reviewed user state; DismissWhatsNew answers only
        // administrators (JE's own check, read from 12.14's IL).
        Assert.Equal(["DismissWhatsNew"], (IEnumerable<string>)je["AdminOnly"]!);
        Assert.Empty((IEnumerable<string>)je["UncoveredUnreviewed"]!);
    }

    [Fact]
    public void JellyfinEnhancedsWritesOfEveryMethod_AreListed_AdminOnlyOnesApart_AndANewOneUnreviewed()
    {
        // The self-check sees every authenticated non-GET JE action (POST, PUT, PATCH, DELETE, or
        // any method) on every controller of JE's assembly, leaves the ones only an administrator gets past (a policy, or
        // JE's own check, reviewed) out of UncoveredUnreviewed, and lists a new one there.
        const string Je = ActionRuleTable.JellyfinEnhancedController;
        const string Assembly = "Jellyfin.Plugin.JellyfinEnhanced";
        var elevated = FilterHarness.Descriptor(Je, "NewAdminThing", Assembly, "PUT", "JellyfinEnhanced/admin/new-thing");
        elevated.EndpointMetadata = [new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Policy = "RequiresElevation" }];
        var local = FilterHarness.Descriptor(Je, "NewLocalThing", Assembly, "POST", "JellyfinEnhanced/local-thing");
        local.EndpointMetadata = [new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Policy = "LocalAccessOrRequiresElevation" }];
        ControllerActionDescriptor[] descriptors =
        [
            FilterHarness.Descriptor(Je, "ForgetEverything", Assembly, "PATCH", "JellyfinEnhanced/forget/{itemId}"),
            FilterHarness.Descriptor(Je, "AnyMethod", Assembly, null, "JellyfinEnhanced/any"),
            FilterHarness.Descriptor(Je, "AdminHideForUser", Assembly, "POST", "JellyfinEnhanced/admin/hidden-content/{userId}/hide"),
            FilterHarness.Descriptor("Jellyfin.Plugin.JellyfinEnhanced.Controllers.CatchUpController", "PostEvent", Assembly, "POST", "JellyfinEnhanced/catchup/event"),
            FilterHarness.Descriptor("Jellyfin.Plugin.JellyfinEnhanced.Controllers.NewController", "Write", Assembly, "DELETE", "JellyfinEnhanced/new/{id}"),
            FilterHarness.Descriptor(Je, "ReadIt", Assembly, "HEAD", "JellyfinEnhanced/read"),
            elevated,
            local
        ];

        var je = (IDictionary<string, object?>)RuleTableSelfCheck.Check(_table, descriptors, filterRegistered: true, DateTime.UnixEpoch)["JellyfinEnhanced"]!;

        Assert.Equal(
            [
                "AdminHideForUser (POST JellyfinEnhanced/admin/hidden-content/{userId}/hide)",
                "AnyMethod (ANY JellyfinEnhanced/any)",
                "CatchUpController.PostEvent (POST JellyfinEnhanced/catchup/event)",
                "ForgetEverything (PATCH JellyfinEnhanced/forget/{itemId})",
                "NewAdminThing (PUT JellyfinEnhanced/admin/new-thing)",
                "NewController.Write (DELETE JellyfinEnhanced/new/{id})",
                "NewLocalThing (POST JellyfinEnhanced/local-thing)"
            ],
            (IEnumerable<string>)je["Uncovered"]!);
        Assert.Equal(["AdminHideForUser", "NewAdminThing"], (IEnumerable<string>)je["AdminOnly"]!);
        Assert.Equal(["AnyMethod", "ForgetEverything", "NewController.Write", "NewLocalThing"], (IEnumerable<string>)je["UncoveredUnreviewed"]!);
    }

    [Fact]
    public void JellyfinEnhanced1214sWrites_AreAllLockedOrReviewed()
    {
        // JE 12.14.0.0's every non-GET action (its controller and CatchUpController, read from the DLL's metadata and IL by
        // review): seven settings saves locked, every other one reviewed (the user's own state, no settings at all,
        // or administrators only), so UncoveredUnreviewed is a signal for the next JE.
        const string Assembly = "Jellyfin.Plugin.JellyfinEnhanced";
        var descriptors = Je1214Writes
            .Select(w => FilterHarness.Descriptor(w.Action == "PostEvent" ? "Jellyfin.Plugin.JellyfinEnhanced.Controllers.CatchUpController" : ActionRuleTable.JellyfinEnhancedController, w.Action, Assembly, w.Method, w.Route))
            .ToArray();

        var je = (IDictionary<string, object?>)RuleTableSelfCheck.Check(_table, descriptors, filterRegistered: true, DateTime.UnixEpoch)["JellyfinEnhanced"]!;

        Assert.Equal(
            ["SaveUserHiddenContent", "SaveUserSettingsDdd", "SaveUserSettingsElsewhere", "SaveUserSettingsSettings", "SaveUserSettingsShortcuts", "SaveUserSpoilerBlur", "SetSpoilerBlurUserPrefs"],
            (IEnumerable<string>)je["Locked"]!);
        Assert.Empty((IEnumerable<string>)je["Unreviewed"]!);
        Assert.Equal(46, ((IEnumerable<string>)je["Uncovered"]!).Count());
        Assert.Equal(ActionRuleTable.ReviewedJellyfinEnhancedAdminOnly.Order(StringComparer.Ordinal), ((IEnumerable<string>)je["AdminOnly"]!).Order(StringComparer.Ordinal));
        Assert.Empty((IEnumerable<string>)je["UncoveredUnreviewed"]!);

        // Every reviewed name is one of 12.14's actions, and in one set only.
        var names = descriptors.Select(ActionRuleTable.JellyfinEnhancedName).ToHashSet(StringComparer.Ordinal);
        string[][] sets = [[.. ActionRuleTable.ReviewedJellyfinEnhancedSaves], [.. ActionRuleTable.ReviewedJellyfinEnhancedUnlocked], [.. ActionRuleTable.ReviewedJellyfinEnhancedOutsideSettings], [.. ActionRuleTable.ReviewedJellyfinEnhancedAdminOnly]];
        Assert.All(sets.SelectMany(s => s), n => Assert.Contains(n, names));
        Assert.Equal(sets.Sum(s => s.Length), sets.SelectMany(s => s).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(names.Count, sets.Sum(s => s.Length));
        Assert.True(ActionRuleTable.ReviewedJellyfinEnhancedBookmarkSaves.IsSubsetOf(ActionRuleTable.ReviewedJellyfinEnhancedUnlocked));
    }

    // JE 12.14.0.0's non-GET actions: name, method, route (Jellyfin.Plugin.JellyfinEnhanced.dll's
    // attributes, the controller's prefix JellyfinEnhanced, CatchUpController's JellyfinEnhanced/catchup).
    private static readonly (string Action, string Method, string Route)[] Je1214Writes =
    [
        ("TriggerJellyseerrRecentlyAddedScan", "POST", "JellyfinEnhanced/jellyseerr/trigger-recently-added-scan"),
        ("JellyseerrRequest", "POST", "JellyfinEnhanced/jellyseerr/request"),
        ("RequestTvSeasons", "POST", "JellyfinEnhanced/jellyseerr/request/tv/{tmdbId}/seasons"),
        ("SyncJellyseerrWatchlist", "POST", "JellyfinEnhanced/jellyseerr/sync-watchlist"),
        ("ImportJellyseerrUsers", "POST", "JellyfinEnhanced/jellyseerr/import-users"),
        ("TrackUsage", "POST", "JellyfinEnhanced/usage/track"),
        ("PreviewUsageReport", "POST", "JellyfinEnhanced/usage/preview"),
        ("TranslateReview", "POST", "JellyfinEnhanced/translate"),
        ("SaveUserSettingsDdd", "POST", "JellyfinEnhanced/user-settings/{userId}/ddd-triggers.json"),
        ("SaveUserSettingsSettings", "POST", "JellyfinEnhanced/user-settings/{userId}/settings.json"),
        ("SaveUserSettingsShortcuts", "POST", "JellyfinEnhanced/user-settings/{userId}/shortcuts.json"),
        ("SaveUserBookmark", "POST", "JellyfinEnhanced/user-settings/{userId}/bookmark.json"),
        ("AddUserBookmark", "POST", "JellyfinEnhanced/user-settings/{userId}/bookmark.json/add"),
        ("RemoveUserBookmark", "DELETE", "JellyfinEnhanced/user-settings/{userId}/bookmark.json/{bookmarkId}"),
        ("SaveUserSettingsElsewhere", "POST", "JellyfinEnhanced/user-settings/{userId}/elsewhere.json"),
        ("SaveUserHiddenContent", "POST", "JellyfinEnhanced/user-settings/{userId}/hidden-content.json"),
        ("SaveUserSpoilerBlur", "POST", "JellyfinEnhanced/user-settings/{userId}/spoilerblur.json"),
        ("AdminUnhideForUser", "POST", "JellyfinEnhanced/admin/hidden-content/{userId}/unhide"),
        ("AdminHideForUser", "POST", "JellyfinEnhanced/admin/hidden-content/{userId}/hide"),
        ("AckSpoilerBlurCorruption", "DELETE", "JellyfinEnhanced/spoiler-blur/health/{targetUserId}"),
        ("StartSpoilerApplyExisting", "POST", "JellyfinEnhanced/spoiler-blur/apply-existing"),
        ("SetSpoilerBlurUserPrefs", "POST", "JellyfinEnhanced/spoiler-blur/user-prefs"),
        ("EnableSpoilerBlurForSeries", "POST", "JellyfinEnhanced/spoiler-blur/series/{seriesId}"),
        ("DisableSpoilerBlurForSeries", "DELETE", "JellyfinEnhanced/spoiler-blur/series/{seriesId}"),
        ("EnableSpoilerBlurForMovie", "POST", "JellyfinEnhanced/spoiler-blur/movies/{movieId}"),
        ("DisableSpoilerBlurForMovie", "DELETE", "JellyfinEnhanced/spoiler-blur/movies/{movieId}"),
        ("EnableSpoilerBlurForCollection", "POST", "JellyfinEnhanced/spoiler-blur/collections/{collectionId}"),
        ("DisableSpoilerBlurForCollection", "DELETE", "JellyfinEnhanced/spoiler-blur/collections/{collectionId}"),
        ("EnableSpoilerBlurPending", "POST", "JellyfinEnhanced/spoiler-blur/pending/{mediaType}/{tmdbId}"),
        ("DisableSpoilerBlurPending", "DELETE", "JellyfinEnhanced/spoiler-blur/pending/{mediaType}/{tmdbId}"),
        ("HideFromContinueWatching", "POST", "JellyfinEnhanced/continue-watching/hide/{itemId}"),
        ("HideFromNextUp", "POST", "JellyfinEnhanced/next-up/hide/{itemId}"),
        ("UnhideFromContinueWatching", "DELETE", "JellyfinEnhanced/continue-watching/hide/{itemId}"),
        ("UnhideFromNextUp", "DELETE", "JellyfinEnhanced/next-up/hide/{itemId}"),
        ("UpsertReview", "POST", "JellyfinEnhanced/reviews/{mediaType}/{tmdbId}"),
        ("AdminUpsertReview", "POST", "JellyfinEnhanced/reviews/admin/{userIdN}/{mediaType}/{tmdbId}"),
        ("DeleteReview", "DELETE", "JellyfinEnhanced/reviews/{mediaType}/{tmdbId}"),
        ("AdminDeleteReview", "DELETE", "JellyfinEnhanced/reviews/admin/{userIdN}/{mediaType}/{tmdbId}"),
        ("ResetAllUsersSettings", "POST", "JellyfinEnhanced/reset-all-users-settings"),
        ("RebuildTagCache", "POST", "JellyfinEnhanced/tag-cache/rebuild"),
        ("DismissWhatsNew", "POST", "JellyfinEnhanced/whats-new/dismiss"),
        ("GetTagData", "POST", "JellyfinEnhanced/tag-data/{userId}"),
        ("ReportJellyseerrIssue", "POST", "JellyfinEnhanced/jellyseerr/issue"),
        ("UploadBrandingImage", "POST", "JellyfinEnhanced/UploadBrandingImage"),
        ("DeleteBrandingImage", "POST", "JellyfinEnhanced/DeleteBrandingImage"),
        ("BroadcastMessage", "POST", "JellyfinEnhanced/active-streams/broadcast"),
        ("ActOnRequest", "POST", "JellyfinEnhanced/arr/requests/{requestId}/approve"),
        ("ActOnRequest", "POST", "JellyfinEnhanced/arr/requests/{requestId}/decline"),
        ("GetCalendarUserDataForEvents", "POST", "JellyfinEnhanced/arr/calendar/user-data"),
        ("EnableMaintenanceMode", "POST", "JellyfinEnhanced/MaintenanceMode/Enable"),
        ("DisableMaintenanceMode", "POST", "JellyfinEnhanced/MaintenanceMode/Disable"),
        ("BroadcastMaintenanceMessage", "POST", "JellyfinEnhanced/MaintenanceMode/Broadcast"),
        ("PostEvent", "POST", "JellyfinEnhanced/catchup/event")
    ];

    [Fact]
    public void APluginControllerThatTakesAJellyfinController_IsFlagged()
    {
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(Jellyfin.Plugin.TrailersFixture.TrailersLikeController).GetTypeInfo(),
            ActionName = "Get"
        };

        var result = RuleTableSelfCheck.Check(_table, [descriptor], filterRegistered: true, DateTime.UnixEpoch);

        Assert.Equal([typeof(Jellyfin.Plugin.TrailersFixture.TrailersLikeController).FullName], (IEnumerable<string>)result["Flagged"]!);
    }
}
