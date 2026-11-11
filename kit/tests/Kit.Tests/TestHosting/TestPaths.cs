using System.IO;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;

namespace Jellyfin.Plugin.Kit.Tests.TestHosting;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = PluginIdentityChecks.FindRepositoryRoot();

    public static string KitRoot { get; } = Path.Combine(RepositoryRoot, "kit");
}
