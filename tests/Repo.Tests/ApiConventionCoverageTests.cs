using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Every plugin enables the kit's API checks from its first task (also the public/internal boundary check, the kit's
/// layout rule applied to plugin types, and a test pinning the public surface it describes), reads the caller only
/// through the kit's CurrentUser, where the claim literals are pinned, and runs the kit's store conformance suite on each
/// of its JSON documents.
/// </summary>
public sealed class ApiConventionCoverageTests
{
    private static readonly string RepositoryRoot = PluginIdentityChecks.FindRepositoryRoot();

    public static TheoryData<string> Plugins => new(PluginIdentityChecks.FindPluginFolders(RepositoryRoot));

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginTests_RunTheKitApiChecks(string plugin)
    {
        var tests = ReadSources(Path.Combine(RepositoryRoot, "plugins", plugin, "tests"));

        foreach (var call in new[] { "SchemaPrefixChecks.Check(", "ControllerNameChecks.CheckNames(", "ControllerNameChecks.CheckRoutes(", "AnonymousActionChecks.Check(", "PublicBoundaryChecks.Check(" })
        {
            Assert.True(tests.Contains(call, StringComparison.Ordinal), $"plugins/{plugin}/tests never calls {call}...); every plugin runs the kit's API checks, the boundary check included.");
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginTests_PinTheirPublicSurface(string plugin)
    {
        // The boundary check tells a DTO by where it lives (Api.Models) and a service interface by who takes it; a pinned
        // list makes each new one a deliberate change, so a record moved into Api.Models or a store interface a controller
        // takes can't slip in (Bookmarks' Api/PublicBoundaryTests.PublicSurface_IsThePinnedSet).
        var tests = ReadSources(Path.Combine(RepositoryRoot, "plugins", plugin, "tests"));

        foreach (var call in new[] { "PublicBoundaryChecks.Describe(", "OfRole(PublicRole.Dto)", "OfRole(PublicRole.ServiceInterface)" })
        {
            Assert.True(tests.Contains(call, StringComparison.Ordinal), $"plugins/{plugin}/tests never uses {call}; every plugin pins its public surface: its DTOs and service interfaces.");
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginSources_ReadClaimsOnlyThroughTheKit(string plugin)
    {
        var sources = ReadSources(Path.Combine(RepositoryRoot, "plugins", plugin), exclude: "tests");

        Assert.DoesNotContain("\"Jellyfin-UserId\"", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Jellyfin-IsApiKey\"", sources, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginTests_RunTheStoreConformanceSuite_ForEveryJsonDocument(string plugin)
    {
        var sources = ReadSources(Path.Combine(RepositoryRoot, "plugins", plugin), exclude: "tests");
        var tests = ReadSources(Path.Combine(RepositoryRoot, "plugins", plugin, "tests"));
        var documents = Regex.Matches(sources, @"JsonDocumentStore<(\w+)>").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var covered = Regex.Matches(tests, @":\s*StoreConformance<(\w+)>").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.All(documents, d => Assert.True(covered.Contains(d), $"plugins/{plugin} stores {d} in a JsonDocumentStore, but no test class derives StoreConformance<{d}>."));
        if (covered.Count > 0)
        {
            Assert.Contains(": StoreConformanceTests", tests, StringComparison.Ordinal);
        }
    }

    private static string ReadSources(string folder, string? exclude = null)
    {
        if (!Directory.Exists(folder))
        {
            return string.Empty;
        }

        var files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var parts = Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Contains("bin") && !parts.Contains("obj") && (exclude is null || parts[0] != exclude);
            });
        return string.Join('\n', files.Select(File.ReadAllText));
    }
}
