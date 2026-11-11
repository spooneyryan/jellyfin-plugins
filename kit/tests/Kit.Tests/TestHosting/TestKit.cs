using System.Reflection;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.Kit.Tests.TestHosting;

/// <summary>
/// Kit options of a pretend plugin, "Jellyfin.Plugin.KitTest", whose fixtures use the prefix <c>Fx</c>.
/// </summary>
internal static class TestKit
{
    /// <summary>The pretend plugin's id, which its export envelopes carry.</summary>
    public static readonly System.Guid PluginId = new("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");

    public static KitOptions Options { get; } = Create();

    public static KitOptions Create(
        string pluginName = "Kit Test",
        string routePrefix = "Fx",
        string prefix = "jfkt",
        string schemaPrefix = "Fx",
        string dataFolder = "kittest",
        string assemblyName = "Jellyfin.Plugin.KitTest")
        => new()
        {
            PluginName = pluginName,
            RoutePrefix = routePrefix,
            Prefix = prefix,
            SchemaPrefix = schemaPrefix,
            DataFolder = dataFolder,
            PluginAssembly = new NamedAssembly(assemblyName),
            PluginId = PluginId
        };

    /// <summary>An assembly that only has a name, standing in for a plugin assembly.</summary>
    private sealed class NamedAssembly : Assembly
    {
        private readonly string _name;

        public NamedAssembly(string name)
        {
            _name = name;
        }

        public override string FullName => _name;

        public override AssemblyName GetName() => new(_name);
    }
}
