using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Plugins list users only through the kit's <c>KitUsers.GetAll</c>. Jellyfin 10.11.9 replaced
/// the property <c>IUserManager.Users</c> (and <c>UsersIds</c>) with methods, so a 10.11.5 build that reads it directly
/// throws <c>MissingMethodException</c> there. Two checks: a grep of every plugin's sources and the kit's (compiled into
/// every plugin), and the compiled plugin assemblies' member references, which catch any spelling the grep can't.
/// </summary>
public sealed partial class KitUsersCoverageTests
{
    private static readonly string RepositoryRoot = PluginIdentityChecks.FindRepositoryRoot();

    public static TheoryData<string> Plugins => new(PluginIdentityChecks.FindPluginFolders(RepositoryRoot));

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginSources_NeverReadIUserManagerUsersDirectly(string plugin)
    {
        var offending = Grep(Path.Combine(RepositoryRoot, "plugins", plugin), exclude: "tests");

        Assert.True(offending.Count == 0, $"plugins/{plugin} reads IUserManager.Users or UsersIds directly; use KitUsers.GetAll:{Environment.NewLine}{string.Join(Environment.NewLine, offending)}");
    }

    [Fact]
    public void KitSources_NeverReadIUserManagerUsersDirectly()
    {
        var offending = Grep(Path.Combine(RepositoryRoot, "kit", "src"));

        Assert.True(offending.Count == 0, "kit/src reads IUserManager.Users or UsersIds directly:" + Environment.NewLine + string.Join(Environment.NewLine, offending));
    }

    [Theory]
    [InlineData("var all = userManager.Users;", true)]
    [InlineData("foreach (var u in _userManager.Users.Where(x => x.IsAdmin))", true)]
    [InlineData("var ids = UserManager\n    .UsersIds;", true)]
    [InlineData("var n = _userManager?.Users.Count();", true)]
    [InlineData("var name = nameof(IUserManager.Users);", true)]
    [InlineData("var all = ((IUserManager)services.GetService(t)).Users;", true)]
    [InlineData("var all = KitUsers.GetAll(userManager);", false)]
    [InlineData("var user = userManager.GetUserById(id);", false)]
    [InlineData("// userManager.Users was replaced in 10.11.9", false)]
    [InlineData("/// <c>IUserManager.Users</c> is 10.11.5's property.", false)]
    [InlineData("var users = document.Users;", false)]
    public void TheGrep_FindsTheShapesItLooksFor(string source, bool found)
    {
        Assert.Equal(found, Find(source).Any());
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PluginAssemblies_HaveNoMemberReferenceToIUserManagerUsers(string plugin)
    {
        var yaml = BuildYaml.Load(Path.Combine(RepositoryRoot, "plugins", plugin, "build.yaml"));
        var dll = yaml.Artifacts.First(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var assembly = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(dll)));

        Assert.Empty(UserManagerUsersReferences(assembly.Location));
    }

#if !JELLYFIN_12
    // Jellyfin 12's IUserManager has no Users property (only GetUsers()), so there the check has nothing to find and no
    // control to run.
    [Fact]
    public void TheAssemblyCheck_FindsADirectRead()
    {
        // Positive control: this test assembly reads the property on purpose (ReadsUsersDirectly below).
        Assert.Contains("get_Users", UserManagerUsersReferences(typeof(KitUsersCoverageTests).Assembly.Location));
    }

    internal static IEnumerable<User> ReadsUsersDirectly(IUserManager userManager) => userManager.Users;
#endif

    private static List<string> UserManagerUsersReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var found = new List<string>();
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            var name = metadata.GetString(member.Name);
            if (name is not ("get_Users" or "get_UsersIds") || member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.GetString(type.Namespace) == "MediaBrowser.Controller.Library" && metadata.GetString(type.Name) == "IUserManager")
            {
                found.Add(name);
            }
        }

        return found;
    }

    private static List<string> Grep(string folder, string? exclude = null)
    {
        var offending = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj") || (exclude is not null && parts[0] == exclude))
            {
                continue;
            }

            offending.AddRange(Find(File.ReadAllText(file)).Select(m => Path.GetRelativePath(RepositoryRoot, file) + ": " + m));
        }

        return offending;
    }

    private static IEnumerable<string> Find(string source)
    {
        var code = LineComment().Replace(source, string.Empty);
        return DirectRead().Matches(code).Concat(CastRead().Matches(code)).Select(m => m.Value);
    }

    // A comment to the end of the line (/// doc comments included); a "//" inside a string literal is rare enough here.
    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    // userManager.Users, _userManager?.UsersIds, UserManager\n.Users, nameof(IUserManager.Users)
    [GeneratedRegex(@"\b\w*[Uu]ser[Mm]anager\w*\s*[?!]?\s*\.\s*(Users|UsersIds)\b")]
    private static partial Regex DirectRead();

    // ((IUserManager)x).Users
    [GeneratedRegex(@"\(\s*IUserManager\s*\)[^;]*?\)\s*\.\s*(Users|UsersIds)\b")]
    private static partial Regex CastRead();
}
