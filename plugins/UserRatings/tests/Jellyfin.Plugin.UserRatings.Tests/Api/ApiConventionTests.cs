using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.UserRatings.Api;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Api;

/// <summary>
/// The kit's API checks on User Ratings: the
/// DTO prefix (<c>UserRatings</c>), controller names and routes under <c>/UserRatings</c>, and no anonymous action but the
/// script.
/// </summary>
public sealed class ApiConventionTests
{
    /// <summary>
    /// Every OpenAPI schema id the plugin's actions reach (the member API brought the first), sorted ordinally. A new
    /// schema-visible type fails <see cref="SchemaIds_AreThePinnedSet"/> until it is added here.
    /// </summary>
    private static readonly string[] SchemaIds =
    [
        "UserRatingsAdminEntryDto",
        "UserRatingsAdminEntryDtoQueryResult",
        "UserRatingsApplyPolicyRequest",
        "UserRatingsCapabilitiesDto",
        "UserRatingsDetachedCandidateDto",
        "UserRatingsDetachedDto",
        "UserRatingsDetachedDtoQueryResult",
        "UserRatingsDisplayDto",
        "UserRatingsHeldDetachDto",
        "UserRatingsHeldDetachRequest",
        "UserRatingsItemCanDto",
        "UserRatingsItemDto",
        "UserRatingsItemSummaryDto",
        "UserRatingsJeImportRequest",
        "UserRatingsJobDto",
        "UserRatingsMineDto",
        "UserRatingsModerationDto",
        "UserRatingsModerationLogDto",
        "UserRatingsModerationLogDtoQueryResult",
        "UserRatingsModerationReason",
        "UserRatingsModerationState",
        "UserRatingsOtherCopyDto",
        "UserRatingsOwnRowDto",
        "UserRatingsOwnRowDtoQueryResult",
        "UserRatingsPostersDto",
        "UserRatingsRatingRequest",
        "UserRatingsRelinkRequest",
        "UserRatingsReviewDto",
        "UserRatingsReviewDtoQueryResult",
        "UserRatingsReviewRequest",
        "UserRatingsReviewSort",
        "UserRatingsSettingsChangeDto",
        "UserRatingsSettingsDto",
        "UserRatingsSettingsReportDto",
        "UserRatingsSettingsUserDto",
        "UserRatingsSettingsViewDto"
    ];

    /// <summary>
    /// The server's generic types built from the plugin's DTOs: Jellyfin's <c>QueryResult&lt;T&gt;</c>, named after the DTO
    /// by Swashbuckle, so they carry the prefix without being the plugin's own types.
    /// </summary>
    private static readonly string[] ServerGenerics = ["UserRatingsAdminEntryDtoQueryResult", "UserRatingsDetachedDtoQueryResult", "UserRatingsModerationLogDtoQueryResult", "UserRatingsOwnRowDtoQueryResult", "UserRatingsReviewDtoQueryResult"];

    [Fact]
    public void KitOptions_AreValid_AndAreThePluginsIdentity()
    {
        var options = UserRatingsKit.Options;

        Assert.Empty(options.Validate());
        Assert.Same(typeof(Plugin).Assembly, options.PluginAssembly);
        Assert.Equal(
            ("User Ratings", "UserRatings", "jfur", "UserRatings", "userratings", new Guid("f53c7c34-8cba-4846-b43b-956816b2f6ae")),
            (options.PluginName, options.RoutePrefix, options.Prefix, options.SchemaPrefix, options.DataFolder, options.PluginId));

        // 0.3.0 has no user page yet (Top Rated is 1.1's) and so no head fragment.
        Assert.Empty(options.Pages);
        Assert.Null(options.HeadFragment);
    }

    [Fact]
    public void SchemaIds_CarryTheUserRatingsPrefix_AndAreNotForeign()
    {
        AssertNoProblems(SchemaPrefixChecks.Check(typeof(Plugin).Assembly, UserRatingsKit.Options.SchemaPrefix));
    }

    [Fact]
    public void SchemaIds_AreThePinnedSet()
    {
        // Diagnostics answers a dictionary (no schema); the user API brings its DTOs, the requests, the
        // sort and Jellyfin's QueryResult of two of them. None is in the pinned list of other plugins' ids.
        var assembly = typeof(Plugin).Assembly;
        var reached = SchemaPrefixChecks.FindReachedSchemas(ControllerDiscovery.FindControllers(assembly), assembly);

        Assert.Equal(SchemaIds, reached.Select(schema => schema.SchemaId).Distinct().Order(StringComparer.Ordinal));
        Assert.All(reached, schema => Assert.True(schema.IsOwnType != ServerGenerics.Contains(schema.SchemaId, StringComparer.Ordinal), schema.SchemaId));
        Assert.All(
            reached.Where(schema => !schema.IsOwnType),
            schema => Assert.Equal(typeof(MediaBrowser.Model.Querying.QueryResult<>), schema.Type.GetGenericTypeDefinition()));
        Assert.Empty(SchemaIds.Intersect(SchemaPrefixChecks.ForeignSchemaIds, StringComparer.Ordinal));
    }

    [Fact]
    public void ControllerNames_StartWithTheRoutePrefix_AndAreNotCoreNames()
    {
        AssertNoProblems(ControllerNameChecks.CheckNames(typeof(Plugin).Assembly, UserRatingsKit.Options.RoutePrefix));
    }

    [Fact]
    public void Routes_AreUnderTheRoutePrefix()
    {
        AssertNoProblems(ControllerNameChecks.CheckRoutes(typeof(Plugin).Assembly, UserRatingsKit.Options.RoutePrefix));
    }

    [Fact]
    public void OnlyTheClientScript_IsAnonymous()
    {
        AssertNoProblems(AnonymousActionChecks.Check(typeof(Plugin).Assembly));
    }

    [Fact]
    public void Checks_SeeEveryController_AndEveryAction()
    {
        // Guards the checks above against passing vacuously.
        Assert.Equal(
            [typeof(UserRatingsAdminController), typeof(UserRatingsController), typeof(UserRatingsDiagnosticsController)],
            ControllerDiscovery.FindControllers(typeof(Plugin).Assembly).OrderBy(t => t.Name, StringComparer.Ordinal));
        Assert.Equal(
            [
                "UserRatingsAdminController.DeleteUserRatingsDetached",
                "UserRatingsAdminController.DeleteUserRatingsJob",
                "UserRatingsAdminController.DeleteUserRatingsModerationReview",
                "UserRatingsAdminController.GetUserRatingsDetached",
                "UserRatingsAdminController.GetUserRatingsDiagnostics",
                "UserRatingsAdminController.GetUserRatingsExport",
                "UserRatingsAdminController.GetUserRatingsJellyfinEnhancedExport",
                "UserRatingsAdminController.GetUserRatingsJob",
                "UserRatingsAdminController.GetUserRatingsJobFile",
                "UserRatingsAdminController.GetUserRatingsJobs",
                "UserRatingsAdminController.GetUserRatingsModerationLog",
                "UserRatingsAdminController.GetUserRatingsModerationReviews",
                "UserRatingsAdminController.GetUserRatingsSettings",
                "UserRatingsAdminController.GetUserRatingsSnapshots",
                "UserRatingsAdminController.PostUserRatingsDataStartEmpty",
                "UserRatingsAdminController.PostUserRatingsDetachedRelink",
                "UserRatingsAdminController.PostUserRatingsDetachedSeen",
                "UserRatingsAdminController.PostUserRatingsExport",
                "UserRatingsAdminController.PostUserRatingsHeldDetach",
                "UserRatingsAdminController.PostUserRatingsImport",
                "UserRatingsAdminController.PostUserRatingsJellyfinEnhancedImport",
                "UserRatingsAdminController.PostUserRatingsSnapshotsRestore",
                "UserRatingsAdminController.PostUserRatingsUsersApplyPolicy",
                "UserRatingsAdminController.PostUserRatingsUsersRemap",
                "UserRatingsAdminController.UpdateUserRatingsSettings",
                "UserRatingsController.DeleteUserRatingsMine",
                "UserRatingsController.DeleteUserRatingsMyRating",
                "UserRatingsController.DeleteUserRatingsMyReview",
                "UserRatingsController.DeleteUserRatingsOwnRow",
                "UserRatingsController.GetUserRatingsAdminScript",
                "UserRatingsController.GetUserRatingsCapabilities",
                "UserRatingsController.GetUserRatingsClientScript",
                "UserRatingsController.GetUserRatingsInaccessible",
                "UserRatingsController.GetUserRatingsItem",
                "UserRatingsController.GetUserRatingsItemReviews",
                "UserRatingsController.SetUserRatingsMyRating",
                "UserRatingsController.SetUserRatingsMyReview",
                "UserRatingsDiagnosticsController.PostUserRatingsClientHealth"
            ],
            ControllerDiscovery.FindControllers(typeof(Plugin).Assembly).SelectMany(c => ControllerDiscovery.FindActions(c).Select(a => ControllerDiscovery.Describe(c, a))).Order(StringComparer.Ordinal));
        Assert.True(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsController), typeof(UserRatingsController).GetMethod(nameof(UserRatingsController.GetUserRatingsClientScript))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.GetUserRatingsDiagnostics))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.PostUserRatingsDetachedSeen))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.PostUserRatingsHeldDetach))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.PostUserRatingsUsersRemap))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.PostUserRatingsUsersApplyPolicy))!));
        Assert.True(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsController), typeof(UserRatingsController).GetMethod(nameof(UserRatingsController.GetUserRatingsAdminScript))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.GetUserRatingsModerationReviews))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.DeleteUserRatingsModerationReview))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(UserRatingsAdminController), typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.GetUserRatingsModerationLog))!));
    }

    private static void AssertNoProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, problems));
        }
    }
}
