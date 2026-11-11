using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.TagCloud.Api;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Api;

/// <summary>
/// The kit's API checks: the schema prefix, controller names and routes, the only anonymous action.
/// </summary>
public sealed class ApiConventionTests
{
    private static readonly string[] SchemaIds =
    [
        "TagCloudCapabilitiesDto",
        "TagCloudLibraryDto",
        "TagCloudLookDto",
        "TagCloudSaveRequest",
        "TagCloudSavedCloudDto",
        "TagCloudScope",
        "TagCloudSource",
        "TagCloudStatusDto",
        "TagCloudTitleDto",
        "TagCloudTitleOrder",
        "TagCloudTitlesDto",
        "TagCloudWordDto",
        "TagCloudWordsDto"
    ];

    [Fact]
    public void KitOptions_AreValid()
    {
        Assert.Empty(TagCloudKit.Options.Validate());
        Assert.Same(typeof(Plugin).Assembly, TagCloudKit.Options.PluginAssembly);
    }

    [Fact]
    public void SchemaIds_CarryTheTagCloudPrefix_AndAreNotForeign()
    {
        AssertNoProblems(SchemaPrefixChecks.Check(typeof(Plugin).Assembly, TagCloudKit.Options.SchemaPrefix));
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
        AssertNoProblems(ControllerNameChecks.CheckNames(typeof(Plugin).Assembly, TagCloudKit.Options.RoutePrefix));
    }

    [Fact]
    public void Routes_AreUnderTheRoutePrefix()
    {
        AssertNoProblems(ControllerNameChecks.CheckRoutes(typeof(Plugin).Assembly, TagCloudKit.Options.RoutePrefix));
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
            [typeof(TagCloudAdminController), typeof(TagCloudCloudsController), typeof(TagCloudController), typeof(TagCloudDiagnosticsController), typeof(TagCloudPagesController)],
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
