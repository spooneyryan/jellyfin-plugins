using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Every plugin's web client goes through the kit: its scripts live in Web/js (and
/// Web/admin/js), Kit.props embeds them, the kit's EmbeddedScript builds them, and the plugin's tests run the kit's
/// script checks on the result (node --check, the kit's ClientScript checks, the coupling policy).
/// </summary>
public sealed class WebConventionCoverageTests
{
    private static readonly string RepositoryRoot = PluginIdentityChecks.FindRepositoryRoot();

    public static TheoryData<string> Plugins => new(PluginIdentityChecks.FindPluginFolders(RepositoryRoot));

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginScripts_LiveInWebJs_AndAreEmbeddedByTheKitOnly(string plugin)
    {
        foreach (var project in PluginProjects(plugin))
        {
            var folder = Path.GetDirectoryName(project)!;
            var web = Path.Combine(folder, "Web");
            var stray = Directory.Exists(web)
                ? Directory.EnumerateFiles(web, "*.js", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                    .Where(f => !Regex.IsMatch(f, "^Web/(admin/)?js/[^/]+\\.js$"))
                    .ToList()
                : [];

            Assert.True(stray.Count == 0, $"plugins/{plugin}: scripts outside Web/js and Web/admin/js aren't served: {string.Join(", ", stray)}.");

            // The one script a plugin embeds itself: the dashboard page's <Plugin>Js stub (its embedded name
            // must end in .js), which only imports the kit's AdminScript module and mounts it (Access Roles' too). Every
            // other script is Kit.props' to embed.
            var embedded = EmbeddedScripts(File.ReadAllText(project), folder);
            Assert.All(embedded, js => Assert.Equal($"Configuration/{plugin}Js.js", js));
            foreach (var js in embedded)
            {
                var stub = File.ReadAllText(Path.Combine(folder, js));
                Assert.Contains("/AdminScript'))", stub, StringComparison.Ordinal);
                Assert.Contains("module.mount(view, ", stub, StringComparison.Ordinal);
                Assert.True(stub.Split('\n').Length <= 25, $"plugins/{plugin}/{js}: the stub only imports the AdminScript; its code goes in Web/admin/js.");
            }
        }
    }

    [Fact]
    public void TheEmbeddedScriptScan_SeesWildcards_UpdateItems_AndSingleQuotes()
    {
        // The scan was once a regex for Include="….js", so a wildcard, an Update= item or single quotes slipped
        // past it. It reads the project as XML now, and expands a wildcard over the project's files as MSBuild would.
        var folder = Directory.CreateTempSubdirectory("repo-embedded-");
        try
        {
            foreach (var file in new[] { "Configuration/StubJs.js", "Web/extra/a.js", "Web/extra/b.css", "Other/c.js", "bin/Debug/d.js" })
            {
                var path = Path.Combine(folder.FullName, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            static string Project(string items) => "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" + items + "</ItemGroup></Project>";

            Assert.Equal(["Configuration/StubJs.js"], EmbeddedScripts(Project("<EmbeddedResource Include=\"Configuration\\StubJs.js\" /><EmbeddedResource Include=\"Web\\extra\\b.css\" />"), folder.FullName));
            Assert.Equal(["Web/extra/a.js"], EmbeddedScripts(Project("<EmbeddedResource Include=\"Web\\**\" />"), folder.FullName));
            Assert.Equal(["Other/c.js"], EmbeddedScripts(Project("<EmbeddedResource Update=\"Other\\c.js\" />"), folder.FullName));
            Assert.Equal(["Other/c.js"], EmbeddedScripts(Project("<EmbeddedResource Include='Other/c.js' />"), folder.FullName));
            Assert.Equal(["Configuration/StubJs.js", "Other/c.js", "Web/extra/a.js"], EmbeddedScripts(Project("<EmbeddedResource Include=\"**\\*.js\" />"), folder.FullName));
            Assert.Equal(["Configuration/StubJs.js", "Web/extra/a.js"], EmbeddedScripts(Project("<EmbeddedResource Include=\"Configuration\\StubJs.js;Web\\*\\*.js\" />"), folder.FullName));
            Assert.Empty(EmbeddedScripts(Project("<EmbeddedResource Include=\"Web\\extra\\*.css\" /><None Include=\"Other\\c.js\" />"), folder.FullName));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginTests_RunTheKitScriptChecks(string plugin)
    {
        var folders = PluginProjects(plugin).Select(Path.GetDirectoryName).ToList();
        var hasClientScript = folders.Any(f => Directory.Exists(Path.Combine(f!, "Web", "js")));
        var hasAdminScript = folders.Any(f => Directory.Exists(Path.Combine(f!, "Web", "admin", "js")));
        var tests = ReadSources(Path.Combine(RepositoryRoot, "plugins", plugin, "tests"));

        if (hasClientScript || hasAdminScript)
        {
            foreach (var call in new[] { "KitScriptChecks.CheckSyntaxAsync(", "WebCouplingChecks.CheckPluginScripts(", "WebCouplingChecks.CheckPluginStyles(", "WebCouplingChecks.CheckAnchors(" })
            {
                Assert.True(tests.Contains(call, StringComparison.Ordinal), $"plugins/{plugin}/tests never calls {call}...); every plugin with a web client must run it.");
            }
        }

        if (hasClientScript)
        {
            Assert.True(tests.Contains("KitScriptChecks.RunClientScriptTestsAsync(", StringComparison.Ordinal), $"plugins/{plugin}/tests never runs the kit's ClientScript checks.");
        }

        if (hasAdminScript)
        {
            Assert.True(tests.Contains("KitScriptChecks.RunAdminScriptTestsAsync(", StringComparison.Ordinal), $"plugins/{plugin}/tests never runs the kit's AdminScript checks.");
            Assert.True(tests.Contains("module: true", StringComparison.Ordinal), $"plugins/{plugin}/tests never checks the AdminScript as a module.");
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginTests_RunTheDashboardPageChecks(string plugin)
    {
        // Every plugin has a settings page, whose scripts render server values with textContent.
        var tests = ReadSources(Path.Combine(RepositoryRoot, "plugins", plugin, "tests"));

        Assert.True(tests.Contains("DashboardPageChecks.Check(", StringComparison.Ordinal), $"plugins/{plugin}/tests never runs DashboardPageChecks.Check on its GetPages(); every plugin must.");
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginsWithScripts_DeclareTheirAnchors(string plugin)
    {
        // Web/anchors.json next to Web/js; it is the one place a plugin writes jellyfin-web selectors.
        foreach (var folder in PluginProjects(plugin).Select(Path.GetDirectoryName))
        {
            if (Directory.Exists(Path.Combine(folder!, "Web", "js")))
            {
                Assert.True(File.Exists(Path.Combine(folder!, "Web", "anchors.json")), $"plugins/{plugin}: Web/js without Web/anchors.json.");
            }
        }
    }

    // The .js files a project's EmbeddedResource items name (Include or Update, ';'-separated, any quotes), relative to the
    // project folder with '/' separators, a wildcard ('*', '**', '?') expanded over the folder's files outside bin and obj.
    private static List<string> EmbeddedScripts(string projectXml, string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/", StringComparison.Ordinal) && !f.StartsWith("obj/", StringComparison.Ordinal))
            .ToList();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in XDocument.Parse(projectXml).Descendants().Where(e => e.Name.LocalName == "EmbeddedResource"))
        {
            foreach (var attribute in new[] { "Include", "Update" })
            {
                foreach (var pattern in ((string?)item.Attribute(attribute) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var spec = pattern.Replace('\\', '/');
                    if (spec.IndexOfAny(['*', '?']) < 0)
                    {
                        if (spec.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(spec);
                        }

                        continue;
                    }

                    var glob = new Regex("^" + Regex.Escape(spec).Replace("\\*\\*/", "(.*/)?", StringComparison.Ordinal).Replace("\\*\\*", ".*", StringComparison.Ordinal).Replace("\\*", "[^/]*", StringComparison.Ordinal).Replace("\\?", "[^/]", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);
                    foreach (var file in files.Where(f => f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && glob.IsMatch(f)))
                    {
                        found.Add(file);
                    }
                }
            }
        }

        return [.. found];
    }

    private static string[] PluginProjects(string plugin)
        => Directory.GetFiles(Path.Combine(RepositoryRoot, "plugins", plugin), "Jellyfin.Plugin.*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".Tests.csproj", StringComparison.Ordinal))
            .ToArray();

    private static string ReadSources(string folder)
        => !Directory.Exists(folder)
            ? string.Empty
            : string.Join('\n', Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Select(File.ReadAllText));
}
