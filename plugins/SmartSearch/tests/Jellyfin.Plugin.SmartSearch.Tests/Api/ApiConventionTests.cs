using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.SmartSearch.Api;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// The kit's API checks: the schema prefix, controller names and routes, the only anonymous action.
/// </summary>
public sealed class ApiConventionTests
{
    private static readonly string[] SchemaIds =
    [
        "SmartSearchApplyPolicyDto",
        "SmartSearchAutoCollectionAccessDto",
        "SmartSearchAutoCollectionCheckNameRequestDto",
        "SmartSearchAutoCollectionDefinitionDto",
        "SmartSearchAutoCollectionDto",
        "SmartSearchAutoCollectionGuardPreviewDto",
        "SmartSearchAutoCollectionGuardRequestDto",
        "SmartSearchAutoCollectionHistoryDto",
        "SmartSearchAutoCollectionItemDto",
        "SmartSearchAutoCollectionManualEditsDto",
        "SmartSearchAutoCollectionNameCheckDto",
        "SmartSearchAutoCollectionNameConflictDto",
        "SmartSearchAutoCollectionNoteDto",
        "SmartSearchAutoCollectionPendingGuardDto",
        "SmartSearchAutoCollectionPresentationDto",
        "SmartSearchAutoCollectionPreviewDto",
        "SmartSearchAutoCollectionPreviewRequestDto",
        "SmartSearchAutoCollectionRecreateRequestDto",
        "SmartSearchAutoCollectionRefDto",
        "SmartSearchAutoCollectionRequestDto",
        "SmartSearchAutoCollectionSafetyDto",
        "SmartSearchAutoCollectionSourceDto",
        "SmartSearchAutoCollectionStateDto",
        "SmartSearchFieldCatalogDto",
        "SmartSearchFieldDto",
        "SmartSearchFieldOptionDto",
        "SmartSearchLibraryDto",
        "SmartSearchMatchMode",
        "SmartSearchPersonDto",
        "SmartSearchQueryDefinition",
        "SmartSearchQueryNode",
        "SmartSearchQueryOperator",
        "SmartSearchQueryScope",
        "SmartSearchQuerySort",
        "SmartSearchRelativeUnit",
        "SmartSearchRequestDto",
        "SmartSearchResultDto",
        "SmartSearchSavedQueryDto",
        "SmartSearchSavedQueryDuplicateDto",
        "SmartSearchSavedQueryListDto",
        "SmartSearchSavedQueryOwnerDto",
        "SmartSearchSavedQueryRequestDto",
        "SmartSearchSavedQueryRunDto",
        "SmartSearchSavedQuerySummaryDto",
        "SmartSearchSeriesDto",
        "SmartSearchValidateRequestDto",
        "SmartSearchValidationDto",
        "SmartSearchValidationErrorDto",
        "SmartSearchValueDto"
    ];

    [Fact]
    public void KitOptions_AreValid()
    {
        Assert.Empty(SmartSearchKit.Options.Validate());
        Assert.Same(typeof(Plugin).Assembly, SmartSearchKit.Options.PluginAssembly);
    }

    [Fact]
    public void SchemaIds_CarryTheSmartSearchPrefix_AndAreNotForeign()
    {
        AssertNoProblems(SchemaPrefixChecks.Check(typeof(Plugin).Assembly, SmartSearchKit.Options.SchemaPrefix));
    }

    [Fact]
    public void SchemaIds_AreThePinnedSet_NoneOfThemForeign()
    {
        var assembly = typeof(Plugin).Assembly;
        var reached = SchemaPrefixChecks.FindReachedSchemas(ControllerDiscovery.FindControllers(assembly), assembly);

        Assert.Equal(SchemaIds, reached.Select(schema => schema.SchemaId).Distinct().Order(StringComparer.Ordinal));
        Assert.All(reached, schema => Assert.True(schema.IsOwnType, schema.SchemaId));
        Assert.Empty(SchemaIds.Intersect(SchemaPrefixChecks.ForeignSchemaIds, StringComparer.Ordinal));
    }

    [Fact]
    public void ControllerNames_StartWithTheRoutePrefix_AndAreNotCoreNames()
    {
        AssertNoProblems(ControllerNameChecks.CheckNames(typeof(Plugin).Assembly, SmartSearchKit.Options.RoutePrefix));
    }

    [Fact]
    public void Routes_AreUnderTheRoutePrefix()
    {
        AssertNoProblems(ControllerNameChecks.CheckRoutes(typeof(Plugin).Assembly, SmartSearchKit.Options.RoutePrefix));
    }

    [Fact]
    public void OnlyClientScript_IsAnonymous()
    {
        AssertNoProblems(AnonymousActionChecks.Check(typeof(Plugin).Assembly));
    }

    [Fact]
    public void Checks_SeeEveryController()
    {
        Assert.Equal(
            [typeof(SmartSearchAdminController), typeof(SmartSearchAutoCollectionsController), typeof(SmartSearchController), typeof(SmartSearchDiagnosticsController), typeof(SmartSearchLookupController), typeof(SmartSearchPagesController), typeof(SmartSearchQueriesController)],
            ControllerDiscovery.FindControllers(typeof(Plugin).Assembly).OrderBy(t => t.Name, StringComparer.Ordinal));
    }

    private static void AssertNoProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, problems));
        }
    }
}
