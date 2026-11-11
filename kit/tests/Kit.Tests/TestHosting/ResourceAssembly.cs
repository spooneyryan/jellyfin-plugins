using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Jellyfin.Plugin.Kit.Tests.TestHosting;

/// <summary>
/// A plugin assembly stand-in whose embedded resources, version and MVID a test chooses, for EmbeddedScript and the web
/// coupling checks.
/// </summary>
internal sealed class ResourceAssembly : Assembly
{
    private readonly Dictionary<string, byte[]> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);
    private readonly FakeModule _module;
    private readonly string _name;

    public ResourceAssembly(string name = "Jellyfin.Plugin.KitTest", string version = "1.2.3.0", Guid? mvid = null)
    {
        _name = name;
        Version = new Version(version);
        _module = new FakeModule(mvid ?? Guid.Parse("0123abcd-4567-89ef-0123-456789abcdef"));
    }

    public Version Version { get; }

    public override string FullName => _name;

    public override Module ManifestModule => _module;

    public ResourceAssembly With(string name, string text) => With(name, new UTF8Encoding(false).GetBytes(text));

    public ResourceAssembly With(string name, byte[] bytes)
    {
        _resources[name] = bytes;
        return this;
    }

    /// <summary>Copies resources of another assembly, for example kit.js and kit.css from Kit.Tests itself.</summary>
    public ResourceAssembly WithResourcesOf(Assembly source, params string[] names)
    {
        foreach (var name in names)
        {
            using var stream = source.GetManifestResourceStream(name) ?? throw new InvalidOperationException("No resource " + name);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            _resources[name] = copy.ToArray();
        }

        return this;
    }

    /// <summary>Adds an <see cref="AssemblyMetadataAttribute"/>, as Kit.props does (for example <c>-p:KitRenameAnchors</c>).</summary>
    public ResourceAssembly WithMetadata(string key, string value)
    {
        _metadata[key] = value;
        return this;
    }

    public override object[] GetCustomAttributes(bool inherit) => GetCustomAttributes(typeof(Attribute), inherit);

    public override object[] GetCustomAttributes(Type attributeType, bool inherit)
    {
        var matching = attributeType.IsAssignableFrom(typeof(AssemblyMetadataAttribute))
            ? _metadata.Select(p => new AssemblyMetadataAttribute(p.Key, p.Value)).ToList()
            : [];
        var result = (object[])Array.CreateInstance(attributeType, matching.Count);
        for (var i = 0; i < matching.Count; i++)
        {
            result[i] = matching[i];
        }

        return result;
    }

    public override AssemblyName GetName() => new(_name) { Version = Version };

    public override string[] GetManifestResourceNames() => _resources.Keys.ToArray();

    public override Stream? GetManifestResourceStream(string name)
        => _resources.TryGetValue(name, out var bytes) ? new MemoryStream(bytes, writable: false) : null;

    private sealed class FakeModule : Module
    {
        public FakeModule(Guid mvid)
        {
            ModuleVersionId = mvid;
        }

        public override Guid ModuleVersionId { get; }
    }
}
