using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Configuration;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// The public/internal boundary: only controllers, the parts Jellyfin creates, the
/// configuration, the service interfaces and the DTOs are public, and that surface is pinned.
/// </summary>
public sealed class PublicBoundaryTests
{
    private static readonly System.Reflection.Assembly Assembly = typeof(Plugin).Assembly;

    [Fact]
    public void Boundary_HasNoProblems()
    {
        var problems = PublicBoundaryChecks.Check(Assembly, SmartSearchKit.Options.SchemaPrefix);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PublicSurface_IsThePinnedSet()
    {
        var surface = PublicBoundaryChecks.Describe(Assembly, SmartSearchKit.Options.SchemaPrefix, "Jellyfin.Plugin.SmartSearch");

        AssertTypes(
            [typeof(SmartSearchAdminController), typeof(SmartSearchAutoCollectionsController), typeof(SmartSearchController), typeof(SmartSearchDiagnosticsController), typeof(SmartSearchLookupController), typeof(SmartSearchPagesController), typeof(SmartSearchQueriesController)],
            surface.OfRole(PublicRole.Controller));
        AssertTypes([typeof(Plugin), typeof(PluginServiceRegistrator), typeof(SmartSearchPostScanTask), typeof(SmartSearchMaintenanceTask), typeof(RefreshAutoCollectionsTask)], surface.OfRole(PublicRole.JellyfinPart));
        AssertTypes([typeof(PluginConfiguration)], surface.OfRole(PublicRole.Configuration));
        AssertTypes([typeof(ISmartSearchAutoCollections), typeof(ISmartSearchFields), typeof(ISmartSearchLookups), typeof(ISmartSearchSavedQueries), typeof(ISmartSearchSavedQueriesAdmin), typeof(ISmartSearchService)], surface.OfRole(PublicRole.ServiceInterface));
        AssertTypes(
            [
                typeof(SmartSearchFieldCatalogDto), typeof(SmartSearchFieldDto), typeof(SmartSearchFieldOptionDto), typeof(SmartSearchLibraryDto),
                typeof(SmartSearchMatchMode), typeof(SmartSearchQueryDefinition), typeof(SmartSearchQueryNode), typeof(SmartSearchQueryOperator),
                typeof(SmartSearchQueryScope), typeof(SmartSearchQuerySort), typeof(SmartSearchRelativeUnit), typeof(SmartSearchRequestDto),
                typeof(SmartSearchResultDto), typeof(SmartSearchSeriesDto), typeof(SmartSearchValidateRequestDto), typeof(SmartSearchValidationDto),
                typeof(SmartSearchValidationErrorDto), typeof(SmartSearchValueDto),
                typeof(SmartSearchApplyPolicyDto), typeof(SmartSearchAutoCollectionCheckNameRequestDto), typeof(SmartSearchAutoCollectionNameCheckDto), typeof(SmartSearchAutoCollectionNameConflictDto), typeof(SmartSearchAutoCollectionPreviewDto),
                typeof(SmartSearchAutoCollectionPreviewRequestDto), typeof(SmartSearchAutoCollectionDto), typeof(SmartSearchAutoCollectionRequestDto), typeof(SmartSearchAutoCollectionStateDto),
                typeof(SmartSearchAutoCollectionRefDto), typeof(SmartSearchAutoCollectionDefinitionDto), typeof(SmartSearchAutoCollectionSourceDto), typeof(SmartSearchAutoCollectionPresentationDto),
                typeof(SmartSearchAutoCollectionManualEditsDto), typeof(SmartSearchAutoCollectionItemDto), typeof(SmartSearchAutoCollectionSafetyDto), typeof(SmartSearchAutoCollectionRecreateRequestDto),
                typeof(SmartSearchAutoCollectionPendingGuardDto), typeof(SmartSearchAutoCollectionGuardPreviewDto), typeof(SmartSearchAutoCollectionGuardRequestDto), typeof(SmartSearchAutoCollectionHistoryDto), typeof(SmartSearchAutoCollectionAccessDto), typeof(SmartSearchAutoCollectionNoteDto), typeof(SmartSearchSavedQueryDto), typeof(SmartSearchSavedQueryDuplicateDto), typeof(SmartSearchSavedQueryListDto),
                typeof(SmartSearchSavedQueryOwnerDto), typeof(SmartSearchSavedQueryRequestDto), typeof(SmartSearchSavedQueryRunDto), typeof(SmartSearchSavedQuerySummaryDto)
            ],
            surface.OfRole(PublicRole.Dto));
        AssertTypes([typeof(Integration.ItemMovedHookV1)], surface.OfRole(PublicRole.Facade));
        Assert.Empty(surface.Unclassified);
        Assert.Empty(surface.ReachedOutsideModels);
        AssertTypes([typeof(IEmbeddedScript), typeof(IKitDiagnostics), typeof(IKitPages), typeof(KitMaintenanceTaskBase), typeof(KitPostScanTaskBase)], surface.KitPublicTypes);
    }

    [Fact]
    public void TheKitOptionsAndConfigurationProvider_AreInternal()
    {
        Type[] internals = [typeof(SmartSearchKit), typeof(IPluginConfigurationProvider), typeof(PluginConfigurationProvider)];

        Assert.All(internals, type => Assert.False(type.IsVisible, type.FullName));
    }

    private static void AssertTypes(IEnumerable<Type> expected, IEnumerable<Type> actual)
        => Assert.Equal(expected.Select(t => t.FullName).Order(StringComparer.Ordinal), actual.Select(t => t.FullName).Order(StringComparer.Ordinal));
}
