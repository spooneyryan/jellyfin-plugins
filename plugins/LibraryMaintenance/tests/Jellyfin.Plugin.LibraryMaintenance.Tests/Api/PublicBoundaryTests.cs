using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Api;

/// <summary>
/// The public/internal boundary (the suite's pattern): only controllers, the parts Jellyfin
/// creates, the configuration, the service interfaces the controllers take and DTOs are public; everything else is
/// internal.
/// </summary>
public sealed class PublicBoundaryTests
{
    private static readonly System.Reflection.Assembly Assembly = typeof(Plugin).Assembly;

    [Fact]
    public void Boundary_HasNoProblems()
    {
        var problems = PublicBoundaryChecks.Check(Assembly, LibraryMaintenanceKit.Options.SchemaPrefix);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PublicSurface_IsThePinnedSet()
    {
        // A new public type fails here until it is added on purpose, with its role.
        var surface = PublicBoundaryChecks.Describe(Assembly, LibraryMaintenanceKit.Options.SchemaPrefix, "Jellyfin.Plugin.LibraryMaintenance");

        AssertTypes([typeof(LibraryMaintenanceAdminController), typeof(LibraryMaintenanceController), typeof(LibraryMaintenanceDiagnosticsController), typeof(LibraryMaintenanceDuplicatesController), typeof(LibraryMaintenanceMoveController)], surface.OfRole(PublicRole.Controller));
        AssertTypes([typeof(Plugin), typeof(PluginServiceRegistrator), typeof(Library.LibraryMaintenancePostScanTask)], surface.OfRole(PublicRole.JellyfinPart));
        AssertTypes([typeof(PluginConfiguration)], surface.OfRole(PublicRole.Configuration));
        AssertTypes([typeof(IDuplicatesIndex), typeof(LibraryMaintenance.Duplicates.Groups.IDuplicateGroups), typeof(LibraryMaintenance.Settings.ILibraryMaintenanceSettings), typeof(LibraryMaintenance.Move.IMoveMedia)], surface.OfRole(PublicRole.ServiceInterface));
        AssertTypes(
            [
                typeof(LibraryMaintenanceDuplicateIndexDto), typeof(LibraryMaintenanceDuplicateIndexLibraryDto), typeof(LibraryMaintenanceDuplicateIndexStatesDto),
                typeof(LibraryMaintenanceDuplicateGroupsResultDto), typeof(LibraryMaintenanceDuplicateGroupsIndexDto), typeof(LibraryMaintenanceDuplicateGroupsPendingDto),
                typeof(LibraryMaintenanceDuplicateGroupDto), typeof(LibraryMaintenanceDuplicateGroupEntryDto), typeof(LibraryMaintenanceDuplicateGroupLibraryDto),
                typeof(LibraryMaintenanceDuplicateProviderIdsDto), typeof(LibraryMaintenanceSettingsDto), typeof(LibraryMaintenanceSettingsViewDto),
                typeof(LibraryMaintenanceSettingsLibraryDto), typeof(LibraryMaintenanceSettingsReportDto), typeof(LibraryMaintenanceSettingsChangeDto),
                typeof(LibraryMaintenanceSettingsPreviewDto), typeof(LibraryMaintenanceSettingsPreviewLibraryDto), typeof(LibraryMaintenanceSettingsPreviewFolderDto),
                typeof(LibraryMaintenanceSettingsSourceDto), typeof(LibraryMaintenanceCapabilitiesDto), typeof(LibraryMaintenanceMoveEligibilityDto),
                typeof(LibraryMaintenanceMoveDestinationLibraryDto), typeof(LibraryMaintenanceMoveDestinationLocationDto), typeof(LibraryMaintenanceMoveFileDto),
                typeof(LibraryMaintenanceMoveFolderDto), typeof(LibraryMaintenanceMoveFoldersDto), typeof(LibraryMaintenanceMoveIssueDto),
                typeof(LibraryMaintenanceMoveItemDto), typeof(LibraryMaintenanceMoveLibraryDto), typeof(LibraryMaintenanceMovePlanDestinationDto),
                typeof(LibraryMaintenanceMovePlanDto), typeof(LibraryMaintenanceMovePlanItemDto), typeof(LibraryMaintenanceMovePlanRequestDto),
                typeof(LibraryMaintenanceMovePlanUnitDto), typeof(LibraryMaintenanceMovePlanVolumeDto), typeof(LibraryMaintenanceMovePreservedDto),
                typeof(LibraryMaintenanceMoveSourceDto), typeof(LibraryMaintenanceMoveSourceUnitDto),
            ],
            surface.OfRole(PublicRole.Dto));
        AssertTypes([], surface.OfRole(PublicRole.Facade));
        Assert.Empty(surface.Unclassified);
        Assert.Empty(surface.ReachedOutsideModels);

        // The kit's public types are the interfaces the controllers take and the abstract bases of the post-scan and
        // maintenance tasks, compiled in with the whole kit whether or not the plugin has such a task.
        AssertTypes([typeof(IKitDiagnostics), typeof(KitMaintenanceTaskBase), typeof(KitPostScanTaskBase), typeof(IEmbeddedScript), typeof(Kit.Web.Pages.IKitPages)], surface.KitPublicTypes);
    }

    [Fact]
    public void TheConfigurationProvider_AndTheKitOptions_AreInternal()
    {
        Type[] internals = [typeof(IPluginConfigurationProvider), typeof(PluginConfigurationProvider), typeof(LibraryMaintenanceKit)];

        Assert.All(internals, type => Assert.False(type.IsVisible, type.FullName));
    }

    private static void AssertTypes(IEnumerable<Type> expected, IEnumerable<Type> actual)
        => Assert.Equal(expected.Select(t => t.FullName).Order(StringComparer.Ordinal), actual.Select(t => t.FullName));
}
