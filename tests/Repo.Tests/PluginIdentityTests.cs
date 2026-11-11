using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Every plugin's build.yaml agrees with its source, project and compiled
/// assembly. Runs once per plugins/&lt;Name&gt;/build.yaml.
/// </summary>
public sealed class PluginIdentityTests
{
    private static readonly string RepositoryRoot = PluginIdentityChecks.FindRepositoryRoot();

    public static TheoryData<string> Plugins => new(PluginIdentityChecks.FindPluginFolders(RepositoryRoot));

    [Fact]
    public void Repository_HasPlugins()
    {
        Assert.NotEmpty(PluginIdentityChecks.FindPluginFolders(RepositoryRoot));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void BuildYaml_SetsEveryField(string plugin)
    {
        AssertNoProblems(PluginIdentityChecks.CheckRequiredFields(LoadBuildYaml(plugin)));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Guid_IsAssignedAndNotATemplate(string plugin)
    {
        AssertNoProblems(PluginIdentityChecks.CheckGuid(LoadBuildYaml(plugin)));
    }

    [Fact]
    public void Guids_AreUniqueAcrossPlugins()
    {
        var duplicates = PluginIdentityChecks.FindPluginFolders(RepositoryRoot)
            .GroupBy(plugin => Guid.Parse(LoadBuildYaml(plugin).Guid))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is used by {string.Join(", ", g)}.")
            .ToList();
        AssertNoProblems(duplicates);
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Version_HasFourParts(string plugin)
    {
        AssertNoProblems(PluginIdentityChecks.CheckVersionFormat(LoadBuildYaml(plugin).Version));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Version_MatchesProjectFile(string plugin)
    {
        var yaml = LoadBuildYaml(plugin);
        var project = XDocument.Load(GetProjectPath(plugin, yaml));
        AssertNoProblems(PluginIdentityChecks.CheckProjectVersions(project, yaml.Version));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Assembly_MatchesBuildYaml(string plugin)
    {
        var yaml = LoadBuildYaml(plugin);
        AssertNoProblems(PluginIdentityChecks.CheckAssembly(LoadAssembly(yaml), yaml));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void TargetAbi_MatchesJellyfinBuild(string plugin)
    {
        AssertNoProblems(PluginIdentityChecks.CheckTargetAbi(LoadBuildYaml(plugin)));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginId_AndName_MatchBuildYaml(string plugin)
    {
        var yaml = LoadBuildYaml(plugin);
        WithPlugin(yaml, instance => AssertNoProblems(PluginIdentityChecks.CheckPluginInstance(instance, yaml)));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginClass_DerivesFromGenericBasePlugin(string plugin)
    {
        var yaml = LoadBuildYaml(plugin);
        AssertNoProblems(PluginIdentityChecks.CheckPluginBaseType(FindPluginType(LoadAssembly(yaml))));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginSource_GuidMatchesBuildYaml(string plugin)
    {
        var yaml = LoadBuildYaml(plugin);
        var pluginType = FindPluginType(LoadAssembly(yaml));
        var declaration = new Regex($@"\bclass\s+{Regex.Escape(pluginType.Name)}\b");
        var projectDirectory = Path.GetDirectoryName(GetProjectPath(plugin, yaml))!;
        var sources = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(projectDirectory, path))
            .Where(path => declaration.IsMatch(File.ReadAllText(path)))
            .ToList();

        var source = Assert.Single(sources);
        AssertNoProblems(PluginIdentityChecks.CheckSourceGuids(File.ReadAllText(source), Path.GetFileName(source), Guid.Parse(yaml.Guid)));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginScripts_WriteBuildYamlIdentity(string plugin)
    {
        var yaml = LoadBuildYaml(plugin);
        var scripts = Path.Combine(RepositoryRoot, "plugins", plugin, "scripts");
        var problems = Directory.Exists(scripts)
            ? Directory.EnumerateFiles(scripts, "*.ps1")
                .SelectMany(path => PluginIdentityChecks.CheckScriptManifest(File.ReadAllText(path), Path.GetFileName(path), yaml))
                .ToList()
            : [];
        AssertNoProblems(problems);
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void SettingsPage_IsFirstInGetPages(string plugin)
    {
        var yaml = LoadBuildYaml(plugin);
        WithPlugin(yaml, instance =>
        {
            var hasPages = Assert.IsAssignableFrom<IHasWebPages>(instance);
            var pages = hasPages.GetPages().ToList();
            var resources = instance.GetType().Assembly.GetManifestResourceNames();
            AssertNoProblems(PluginIdentityChecks.CheckPages(pages, yaml.Name, resources));
        });
    }

    private static BuildYaml LoadBuildYaml(string plugin)
        => BuildYaml.Load(Path.Combine(RepositoryRoot, "plugins", plugin, "build.yaml"));

    private static string GetAssemblyName(BuildYaml yaml)
    {
        var dll = yaml.Artifacts.FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.True(dll is not null, "build.yaml's artifacts list no DLL.");
        return Path.GetFileNameWithoutExtension(dll);
    }

    private static string GetProjectPath(string plugin, BuildYaml yaml)
    {
        var name = GetAssemblyName(yaml);
        var path = Path.Combine(RepositoryRoot, "plugins", plugin, name, name + ".csproj");
        Assert.True(File.Exists(path), $"Expected the plugin project at {path} (the repository's plugin layout).");
        return path;
    }

    private static Assembly LoadAssembly(BuildYaml yaml)
    {
        var name = GetAssemblyName(yaml);
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"{name} isn't referenced by Repo.Tests; it references plugins/*/Jellyfin.Plugin.*/Jellyfin.Plugin.*.csproj.", ex);
        }
    }

    private static Type FindPluginType(Assembly assembly)
        => Assert.Single(assembly.GetTypes(), t => t is { IsClass: true, IsAbstract: false } && typeof(BasePlugin).IsAssignableFrom(t));

    private static void WithPlugin(BuildYaml yaml, Action<BasePlugin> action)
    {
        var scratch = Directory.CreateTempSubdirectory("jellyfin-plugins-identity-");
        try
        {
            action(PluginIdentityChecks.CreatePlugin(FindPluginType(LoadAssembly(yaml)), scratch.FullName));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static bool IsBuildOutput(string projectDirectory, string path)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }

    private static void AssertNoProblems(IReadOnlyCollection<string> problems)
    {
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, problems));
        }
    }
}
