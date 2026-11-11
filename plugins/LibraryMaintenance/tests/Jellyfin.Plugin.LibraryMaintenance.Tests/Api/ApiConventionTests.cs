using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Api;

/// <summary>
/// The kit's API checks on Library Maintenance.
/// </summary>
public sealed class ApiConventionTests
{
    /// <summary>
    /// Every schema id Library Maintenance publishes (DTO names start with <c>LibraryMaintenance</c>). A new
    /// schema-visible type fails <see cref="SchemaIds_AreThePinnedSet_NoneOfThemForeign"/> until it is added here.
    /// </summary>
    private static readonly string[] SchemaIds =
    [
        "LibraryMaintenanceCapabilitiesDto",
        "LibraryMaintenanceDuplicateGroupDto",
        "LibraryMaintenanceDuplicateGroupEntryDto",
        "LibraryMaintenanceDuplicateGroupLibraryDto",
        "LibraryMaintenanceDuplicateGroupsIndexDto",
        "LibraryMaintenanceDuplicateGroupsPendingDto",
        "LibraryMaintenanceDuplicateGroupsResultDto",
        "LibraryMaintenanceDuplicateIndexDto",
        "LibraryMaintenanceDuplicateIndexLibraryDto",
        "LibraryMaintenanceDuplicateIndexStatesDto",
        "LibraryMaintenanceDuplicateProviderIdsDto",
        "LibraryMaintenanceMoveDestinationLibraryDto",
        "LibraryMaintenanceMoveDestinationLocationDto",
        "LibraryMaintenanceMoveEligibilityDto",
        "LibraryMaintenanceMoveFileDto",
        "LibraryMaintenanceMoveFolderDto",
        "LibraryMaintenanceMoveFoldersDto",
        "LibraryMaintenanceMoveIssueDto",
        "LibraryMaintenanceMoveItemDto",
        "LibraryMaintenanceMoveLibraryDto",
        "LibraryMaintenanceMovePlanDestinationDto",
        "LibraryMaintenanceMovePlanDto",
        "LibraryMaintenanceMovePlanItemDto",
        "LibraryMaintenanceMovePlanRequestDto",
        "LibraryMaintenanceMovePlanUnitDto",
        "LibraryMaintenanceMovePlanVolumeDto",
        "LibraryMaintenanceMovePreservedDto",
        "LibraryMaintenanceMoveSourceDto",
        "LibraryMaintenanceMoveSourceUnitDto",
        "LibraryMaintenanceSettingsChangeDto",
        "LibraryMaintenanceSettingsDto",
        "LibraryMaintenanceSettingsLibraryDto",
        "LibraryMaintenanceSettingsPreviewDto",
        "LibraryMaintenanceSettingsPreviewFolderDto",
        "LibraryMaintenanceSettingsPreviewLibraryDto",
        "LibraryMaintenanceSettingsReportDto",
        "LibraryMaintenanceSettingsSourceDto",
        "LibraryMaintenanceSettingsViewDto",
    ];

    [Fact]
    public void KitOptions_AreValid()
    {
        Assert.Empty(LibraryMaintenanceKit.Options.Validate());
        Assert.Same(typeof(Plugin).Assembly, LibraryMaintenanceKit.Options.PluginAssembly);
    }

    [Fact]
    public void SchemaIds_CarryTheLibraryMaintenancePrefix_AndAreNotForeign()
    {
        AssertNoProblems(SchemaPrefixChecks.Check(typeof(Plugin).Assembly, LibraryMaintenanceKit.Options.SchemaPrefix));
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
        AssertNoProblems(ControllerNameChecks.CheckNames(typeof(Plugin).Assembly, LibraryMaintenanceKit.Options.RoutePrefix));
    }

    [Fact]
    public void Routes_AreUnderTheRoutePrefix()
    {
        AssertNoProblems(ControllerNameChecks.CheckRoutes(typeof(Plugin).Assembly, LibraryMaintenanceKit.Options.RoutePrefix));
    }

    [Fact]
    public void OnlyAdminScript_IsAnonymous()
    {
        AssertNoProblems(AnonymousActionChecks.Check(typeof(Plugin).Assembly));
    }

    [Fact]
    public void Checks_SeeEveryController()
    {
        // Guards the checks above against passing vacuously: the AdminScript and ClientScript, the admin Diagnostics, the
        // ClientHealth reports and the Duplicates index.
        Assert.Equal(
            [
                typeof(LibraryMaintenance.Api.LibraryMaintenanceAdminController),
                typeof(LibraryMaintenance.Api.LibraryMaintenanceController),
                typeof(LibraryMaintenance.Api.LibraryMaintenanceDiagnosticsController),
                typeof(LibraryMaintenance.Api.LibraryMaintenanceDuplicatesController),
                typeof(LibraryMaintenance.Api.LibraryMaintenanceMoveController),
            ],
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
