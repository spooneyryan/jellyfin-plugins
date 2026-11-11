using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// The packages the server ships are pinned to exactly the versions Jellyfin 10.11.5 ships: a
/// plugin compiled against a newer Microsoft.Data.Sqlite than the server has passes the DLL allowlist but can stop the
/// whole server at startup. Deploy-Plugin.ps1's reference-version check catches it at install time; this catches it in
/// the repository.
/// </summary>
public sealed class ServerPackagePinTests
{
    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();

    // What Jellyfin 10.11.5 ships: v10.11.5:Directory.Packages.props (Microsoft.Data.Sqlite 9.0.11, whose .Core package
    // has the same version) and the server packages of the release itself.
    private static readonly Dictionary<string, string> ServerShips = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jellyfin.Controller"] = "10.11.5",
        ["Jellyfin.Model"] = "10.11.5",
        ["Microsoft.Data.Sqlite.Core"] = "9.0.11",
        ["Microsoft.Data.Sqlite"] = "9.0.11",
    };

    [Fact]
    public void ServerPackages_ArePinnedToExactlyWhatJellyfin10115Ships()
    {
        var versions = CentralVersions();

        Assert.Equal("9.0.11", versions["Microsoft.Data.Sqlite.Core"]);
        foreach (var (package, shipped) in ServerShips)
        {
            if (versions.TryGetValue(package, out var pinned))
            {
                Assert.True(pinned == shipped, $"Directory.Packages.props pins {package} at {pinned}; Jellyfin 10.11.5 ships {shipped}. Pin exactly the server's version, never higher, and no range.");
            }
        }
    }

    [Fact]
    public void NoProject_OverridesACentralVersion()
    {
        // VersionOverride, a Version on a PackageReference, or turning central management off would let one project
        // compile against another version than Directory.Packages.props says. Projects outside kit, plugins and tests keep their own settings.
        var offenders = new List<string>();
        foreach (var file in ProjectFiles())
        {
            var xml = XDocument.Load(file);
            var relative = Path.GetRelativePath(Root, file);
            foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion" && Path.GetFileName(file) != "Directory.Packages.props"))
            {
                if (reference.Attribute("Version") is not null || reference.Attribute("VersionOverride") is not null || reference.Elements().Any(c => c.Name.LocalName is "Version" or "VersionOverride"))
                {
                    offenders.Add($"{relative}: {reference.Name.LocalName} {reference.Attribute("Include")?.Value} sets its own version");
                }
            }

            foreach (var property in xml.Descendants().Where(e => e.Name.LocalName is "ManagePackageVersionsCentrally" or "CentralPackageVersionOverrideEnabled"))
            {
                if (Path.GetFileName(file) != "Directory.Packages.props")
                {
                    offenders.Add($"{relative}: sets {property.Name.LocalName}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheKitsSqliteReference_ExcludesTheRuntimeAssets()
    {
        // The server's copy is used at run time; shipping one breaks every open, and Deploy's allowlist refuses it.
        var kit = XDocument.Load(Path.Combine(Root, "kit", "build", "Kit.props"));
        var sqlite = kit.Descendants().Where(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true).ToList();

        Assert.Equal(["Microsoft.Data.Sqlite.Core"], sqlite.Select(e => e.Attribute("Include")!.Value));
        Assert.Equal("runtime", sqlite[0].Attribute("ExcludeAssets")?.Value);
        Assert.Equal("all", sqlite[0].Attribute("PrivateAssets")?.Value);

        // Only plugins with KitUseSqlite get it; a test project compiling the kit gets the full package from TestSupport.props
        // instead, so the condition names both.
        var condition = sqlite[0].Parent!.Attribute("Condition")?.Value;
        Assert.Equal("'$(KitUseSqlite)' == 'true' and '$(IsTestProject)' != 'true'", condition);
    }

    [Fact]
    public void OnlyTestProjects_ReferenceTheFullSqlitePackage_ThroughKitUseSqlite()
    {
        // Microsoft.Data.Sqlite with its native e_sqlite3 comes into test output only.
        var support = XDocument.Load(Path.Combine(Root, "kit", "build", "TestSupport.props"));
        var full = Assert.Single(support.Descendants(), e => e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value == "Microsoft.Data.Sqlite");
        Assert.Equal("'$(KitUseSqlite)' == 'true'", full.Parent!.Attribute("Condition")?.Value);
        Assert.Equal("9.0.11", CentralVersions()["Microsoft.Data.Sqlite"]);

        var offenders = ProjectFiles()
            .Where(f => !Path.GetFileName(f).Equals("TestSupport.props", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(f).EndsWith(".Tests", StringComparison.Ordinal))
            .Where(f => XDocument.Load(f).Descendants().Any(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value == "Microsoft.Data.Sqlite"))
            .Select(f => Path.GetRelativePath(Root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void NoKitOrPluginSource_CallsClearAllPools()
    {
        // ClearAllPools empties every SQLite pool in the server process, Jellyfin's pooled jellyfin.db included. The kit
        // clears only its own database's pool (SqliteConnection.ClearPool).
        var sources = Directory.EnumerateFiles(Path.Combine(Root, "kit", "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateDirectories(Path.Combine(Root, "plugins")).SelectMany(p => Directory.EnumerateDirectories(p, "Jellyfin.Plugin.*")).Where(d => !d.EndsWith(".Tests", StringComparison.Ordinal)).SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories)))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        var offenders = sources.Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\.ClearAllPools\s*\(")).Select(f => Path.GetRelativePath(Root, f)).ToList();

        Assert.Empty(offenders);
    }

    private static Dictionary<string, string> CentralVersions()
        => XDocument.Load(Path.Combine(Root, "Directory.Packages.props")).Descendants()
            .Where(e => e.Name.LocalName == "PackageVersion")
            .ToDictionary(e => e.Attribute("Include")!.Value, e => e.Attribute("Version")!.Value, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ProjectFiles()
        => new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" }
            .Select(f => Path.Combine(Root, f))
            .Where(File.Exists)
            .Concat(new[] { "kit", "plugins", "tests" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(Root, folder), "*.*", SearchOption.AllDirectories)))
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
