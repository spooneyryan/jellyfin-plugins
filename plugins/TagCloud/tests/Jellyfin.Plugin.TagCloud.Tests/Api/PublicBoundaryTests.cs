using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Jellyfin.Plugin.TagCloud.Api;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Clouds;
using Jellyfin.Plugin.TagCloud.Configuration;
using Jellyfin.Plugin.TagCloud.Index;
using Jellyfin.Plugin.TagCloud.Library;
using Jellyfin.Plugin.TagCloud.Services;
using Jellyfin.Plugin.TagCloud.Tasks;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Api;

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
        var problems = PublicBoundaryChecks.Check(Assembly, TagCloudKit.Options.SchemaPrefix);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PublicSurface_IsThePinnedSet()
    {
        var surface = PublicBoundaryChecks.Describe(Assembly, TagCloudKit.Options.SchemaPrefix, "Jellyfin.Plugin.TagCloud");

        AssertTypes(
            [typeof(TagCloudAdminController), typeof(TagCloudCloudsController), typeof(TagCloudController), typeof(TagCloudDiagnosticsController), typeof(TagCloudPagesController)],
            surface.OfRole(PublicRole.Controller));
        AssertTypes([typeof(TagCloudPostScanTask), typeof(TagCloudMaintenanceTask), typeof(Plugin), typeof(PluginServiceRegistrator)], surface.OfRole(PublicRole.JellyfinPart));
        AssertTypes([typeof(PluginConfiguration)], surface.OfRole(PublicRole.Configuration));
        AssertTypes([typeof(ITagCloudQuery), typeof(ITagCloudClouds), typeof(ITagCloudAdmin)], surface.OfRole(PublicRole.ServiceInterface));
        AssertTypes(
            [
                typeof(TagCloudCapabilitiesDto), typeof(TagCloudLibraryDto), typeof(TagCloudLookDto), typeof(TagCloudSaveRequest), typeof(TagCloudSavedCloudDto),
                typeof(TagCloudScope), typeof(TagCloudSource), typeof(TagCloudStatusDto), typeof(TagCloudTitleDto), typeof(TagCloudTitleOrder),
                typeof(TagCloudTitlesDto), typeof(TagCloudWordDto), typeof(TagCloudWordsDto)
            ],
            surface.OfRole(PublicRole.Dto));
        Assert.Empty(surface.OfRole(PublicRole.Facade));
        Assert.Empty(surface.Unclassified);
        Assert.Empty(surface.ReachedOutsideModels);
        AssertTypes([typeof(IKitDiagnostics), typeof(KitMaintenanceTaskBase), typeof(KitPostScanTaskBase), typeof(IEmbeddedScript), typeof(IKitPages)], surface.KitPublicTypes);
    }

    [Fact]
    public void TheIndexStoreAndImplementations_AreInternal()
    {
        Type[] internals =
        [
            typeof(TagCloudKit), typeof(IPluginConfigurationProvider), typeof(PluginConfigurationProvider), typeof(TagCloudIndex), typeof(TagCloudIndexSnapshot),
            typeof(IndexState), typeof(IndexedWord), typeof(TitleRecord), typeof(TitleReader), typeof(PeopleReader), typeof(TagCloudIndexWorker),
            typeof(TagCloudLibraryConsumer), typeof(SavedCloudOwner), typeof(SavedCloudDocument), typeof(SavedCloudRecord), typeof(CloudLook),
            typeof(TagCloudQueryService), typeof(TagCloudCloudService), typeof(TagCloudAdminService), typeof(TagCloudMaintenanceSteps),
            typeof(LibraryAccess), typeof(ILibraryChangeConsumer)
        ];

        Assert.All(internals, type => Assert.False(type.IsVisible, type.FullName));
    }

    private static void AssertTypes(IEnumerable<Type> expected, IEnumerable<Type> actual)
        => Assert.Equal(expected.Select(t => t.FullName).Order(StringComparer.Ordinal), actual.Select(t => t.FullName).Order(StringComparer.Ordinal));
}
