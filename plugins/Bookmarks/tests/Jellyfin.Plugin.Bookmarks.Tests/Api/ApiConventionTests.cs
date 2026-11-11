using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The kit's API checks on Bookmarks.
/// </summary>
public sealed class ApiConventionTests
{
    /// <summary>
    /// Every schema id Bookmarks publishes. 1.1.0 renamed 1.0's unprefixed DTOs and enums; the JSON on the wire didn't
    /// change (<see cref="WireFormatTests"/>). 1.1.0 also added the admin's <c>BookmarkApplyPolicyRequest</c>, the My
    /// Bookmarks page's lists (<c>BookmarkGroupDto</c>, the two <c>QueryResult</c>s, the sort and filter enums;
    /// <c>BookmarkOrigin</c> is reached through <c>BookmarkDto.Origin</c>), the bulk delete, the preferences, the
    /// capabilities' shortcuts and pages, and the Jellyfin Enhanced import's request, report, user rows, samples, outcome and
    /// checklist items; 1.2.0 the My Clips page's list (<c>BookmarkClipItemDto</c> and its <c>QueryResult</c>, the state and
    /// sort enums), its summary, the clips' status, the clips' bulk delete, the clip's rename
    /// (<c>BookmarkClipRenameRequest</c>) and Rename file's optional body (<c>BookmarkClipRenameFileRequest</c>, its
    /// IfUpdatedAt), and the admins' clip rows (<c>BookmarkAdminClipItemDto</c> and its
    /// <c>QueryResult</c>) and per-user totals (<c>BookmarkAdminClipSummaryDto</c>, <c>BookmarkAdminClipUserDto</c>); Bookmarks
    /// 1.2.1 the settings page's clip folder check (<c>BookmarkClipFolderCheckRequest</c>). A new
    /// schema-visible type fails <see cref="SchemaIds_AreThePinnedSet_NoneOfThemForeign"/> until it is added here.
    /// </summary>
    private static readonly string[] SchemaIds =
    [
        "BookmarkAdminClipItemDto",
        "BookmarkAdminClipItemDtoQueryResult",
        "BookmarkAdminClipSummaryDto",
        "BookmarkAdminClipUserDto",
        "BookmarkApplyPolicyRequest",
        "BookmarkAvailability",
        "BookmarkBulkDeleteRequest",
        "BookmarkBulkDeleteResultDto",
        "BookmarkCapabilitiesDto",
        "BookmarkClipBulkDeleteRequest",
        "BookmarkClipBulkDeleteResultDto",
        "BookmarkClipDto",
        "BookmarkClipFolderCheckRequest",
        "BookmarkClipItemDto",
        "BookmarkClipItemDtoQueryResult",
        "BookmarkClipMode",
        "BookmarkClipRenameFileRequest",
        "BookmarkClipRenameRequest",
        "BookmarkClipRequest",
        "BookmarkClipSortBy",
        "BookmarkClipState",
        "BookmarkClipStatus",
        "BookmarkClipStatusDto",
        "BookmarkClipSummaryDto",
        "BookmarkCreateRequest",
        "BookmarkDto",
        "BookmarkDtoQueryResult",
        "BookmarkGroupDto",
        "BookmarkGroupDtoQueryResult",
        "BookmarkGroupSortBy",
        "BookmarkItemType",
        "BookmarkJeChecklistItemDto",
        "BookmarkJeImportOutcome",
        "BookmarkJeImportReportDto",
        "BookmarkJeImportRequest",
        "BookmarkJeImportSampleDto",
        "BookmarkJeImportUserDto",
        "BookmarkOrigin",
        "BookmarkPagesDto",
        "BookmarkPreferencesDto",
        "BookmarkRenameRequest",
        "BookmarkShortcutsDto",
        "BookmarkSortBy",
        "BookmarkStoreReadOnlyReason",
        "BookmarkUpdateRequest"
    ];

    /// <summary>
    /// The server's generic types built from Bookmarks' DTOs: Jellyfin's <c>QueryResult&lt;T&gt;</c>, named
    /// after the DTO by Swashbuckle, so they carry the prefix without being Bookmarks' own types.
    /// </summary>
    private static readonly string[] ServerGenerics = ["BookmarkAdminClipItemDtoQueryResult", "BookmarkClipItemDtoQueryResult", "BookmarkDtoQueryResult", "BookmarkGroupDtoQueryResult"];

    /// <summary>Bookmarks 1.0's names of the renamed types: <c>ClipDto</c> is now <c>BookmarkClipDto</c>, and so on.</summary>
    private static readonly string[] Names10 = ["ClipDto", "ClipMode", "ClipStatus", "CreateBookmarkRequest", "CreateClipRequest", "UpdateBookmarkRequest"];

    [Fact]
    public void KitOptions_AreValid()
    {
        Assert.Empty(BookmarksKit.Options.Validate());
        Assert.Same(typeof(Plugin).Assembly, BookmarksKit.Options.PluginAssembly);
    }

    [Fact]
    public void SchemaIds_CarryTheBookmarkPrefix_AndAreNotForeign()
    {
        // No exemptions since 1.1.0: every schema-visible type carries the prefix, and none is in the foreign-id list.
        AssertNoProblems(SchemaPrefixChecks.Check(typeof(Plugin).Assembly, BookmarksKit.Options.SchemaPrefix));
    }

    [Fact]
    public void SchemaIds_AreThePinnedSet_NoneOfThemForeign()
    {
        var assembly = typeof(Plugin).Assembly;
        var reached = SchemaPrefixChecks.FindReachedSchemas(ControllerDiscovery.FindControllers(assembly), assembly);

        Assert.Equal(SchemaIds, reached.Select(schema => schema.SchemaId).Distinct().Order(StringComparer.Ordinal));
        Assert.All(reached, schema => Assert.True(schema.IsOwnType != ServerGenerics.Contains(schema.SchemaId, StringComparer.Ordinal), schema.SchemaId));
        Assert.All(
            reached.Where(schema => !schema.IsOwnType),
            schema => Assert.Equal(typeof(MediaBrowser.Model.Querying.QueryResult<>), schema.Type.GetGenericTypeDefinition()));

        // No Bookmarks schema id is in kit/testdata/openapi-schema-ids.txt. JE 12.8's BookmarkItem,
        // UserBookmark and AddBookmarkPayload are, so the prefix alone doesn't keep us apart from them.
        Assert.Empty(SchemaIds.Intersect(SchemaPrefixChecks.ForeignSchemaIds, StringComparer.Ordinal));
        Assert.Contains("BookmarkItem", SchemaPrefixChecks.ForeignSchemaIds);
    }

    [Fact]
    public void The10Names_AreGone()
    {
        Assert.Empty(typeof(Plugin).Assembly.GetTypes().Where(type => Names10.Contains(type.Name, StringComparer.Ordinal)).Select(type => type.FullName));
    }

    [Fact]
    public void ControllerNames_StartWithTheRoutePrefix_AndAreNotCoreNames()
    {
        AssertNoProblems(ControllerNameChecks.CheckNames(typeof(Plugin).Assembly, BookmarksKit.Options.RoutePrefix));
    }

    [Fact]
    public void Routes_AreUnderTheRoutePrefix()
    {
        AssertNoProblems(ControllerNameChecks.CheckRoutes(typeof(Plugin).Assembly, BookmarksKit.Options.RoutePrefix));
    }

    [Fact]
    public void OnlyClientScript_IsAnonymous()
    {
        AssertNoProblems(AnonymousActionChecks.Check(typeof(Plugin).Assembly));
    }

    [Fact]
    public void Checks_SeeEveryController()
    {
        // Guards the checks above against passing vacuously: the API, the admin Diagnostics and the ClientHealth reports,
        // the page shells, the developer identity demo, the developer user data demo and the My Clips page's clips.
        Assert.Equal(
            [typeof(Bookmarks.Api.BookmarksAdminClipsController), typeof(Bookmarks.Api.BookmarksAdminController), typeof(Bookmarks.Api.BookmarksClipsController), typeof(Bookmarks.Api.BookmarksController), typeof(Bookmarks.Api.BookmarksDiagnosticsController), typeof(Bookmarks.Api.BookmarksIdentityDemoController), typeof(Bookmarks.Api.BookmarksLibraryChangeDemoController), typeof(Bookmarks.Api.BookmarksLibraryDemoController), typeof(Bookmarks.Api.BookmarksPagesController), typeof(Bookmarks.Api.BookmarksUserDataDemoController)],
            ControllerDiscovery.FindControllers(typeof(Plugin).Assembly).OrderBy(t => t.Name, System.StringComparer.Ordinal));
    }

    private static void AssertNoProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(System.Environment.NewLine, problems));
        }
    }
}
