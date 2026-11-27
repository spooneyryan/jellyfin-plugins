using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;

/// <summary>
/// A Jellyfin plugins folder with several copies of one plugin, for the kit's duplicate guard: folders as Jellyfin's
/// installer lays them out (<c>&lt;name&gt;_&lt;version&gt;</c> with a <c>meta.json</c>), synthetic DLLs with or without the
/// guard, and a real plugin assembly copied in and loaded in its own load context, as Jellyfin loads each plugin folder,
/// so its registrator sees the folder it was loaded from.
/// </summary>
public sealed class PluginCopies : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly List<AssemblyLoadContext> _contexts = [];

    public PluginCopies()
    {
        PluginsPath = _root.Combine("plugins");
        Directory.CreateDirectory(PluginsPath);
    }

    /// <summary>Gets the plugins folder.</summary>
    public string PluginsPath { get; }

    /// <summary>
    /// Writes a minimal .NET library: its identity, and optionally the duplicate guard's type and the kit's Jellyfin
    /// target metadata, which is all the guard reads from another copy.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="name">The assembly name.</param>
    /// <param name="version">The assembly version.</param>
    /// <param name="hasGuard">Whether to define <c>Jellyfin.Plugin.Kit.Hosting.KitDuplicateGuard</c>.</param>
    /// <param name="jellyfinTarget">The <c>Jellyfin.Plugin.Kit.JellyfinTarget</c> metadata value, or <c>null</c> for none.</param>
    /// <returns>The path.</returns>
    public static string WriteAssembly(string path, string name, Version version, bool hasGuard, string? jellyfinTarget = null)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), version, default, default, default, AssemblyHashAlgorithm.Sha1);
        var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(9, 0, 0, 0), default, default, default, default);
        var objectType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        if (hasGuard)
        {
            metadata.AddTypeDefinition(
                TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed,
                metadata.GetOrAddString("Jellyfin.Plugin.Kit.Hosting"),
                metadata.GetOrAddString("KitDuplicateGuard"),
                objectType,
                MetadataTokens.FieldDefinitionHandle(1),
                MetadataTokens.MethodDefinitionHandle(1));
        }

        if (jellyfinTarget is not null)
        {
            var attributeType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Reflection"), metadata.GetOrAddString("AssemblyMetadataAttribute"));
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature(isInstanceMethod: true).Parameters(
                2,
                returnType => returnType.Void(),
                parameters =>
                {
                    parameters.AddParameter().Type().String();
                    parameters.AddParameter().Type().String();
                });
            var constructor = metadata.AddMemberReference(attributeType, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(signature));
            var value = new BlobBuilder();
            value.WriteUInt16(1);
            value.WriteSerializedString("Jellyfin.Plugin.Kit.JellyfinTarget");
            value.WriteSerializedString(jellyfinTarget);
            value.WriteUInt16(0);
            metadata.AddCustomAttribute(EntityHandle.AssemblyDefinition, constructor, metadata.GetOrAddBlob(value));
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }

    /// <summary>
    /// Writes a <c>meta.json</c> the way Jellyfin's installer does (the members the guard reads).
    /// </summary>
    /// <param name="folder">The plugin folder.</param>
    /// <param name="id">The plugin id.</param>
    /// <param name="name">The catalog name.</param>
    /// <param name="version">The version text.</param>
    /// <param name="status">The status, as Jellyfin writes it (<c>Active</c>, <c>Deleted</c>, ...).</param>
    /// <param name="targetAbi">The target ABI.</param>
    public static void WriteManifest(string folder, Guid id, string name, string version, string status = "Active", string targetAbi = "10.11.0.0")
    {
        Directory.CreateDirectory(folder);
        var manifest = new Dictionary<string, object>
        {
            ["category"] = "General",
            ["changelog"] = string.Empty,
            ["description"] = string.Empty,
            ["guid"] = id.ToString(),
            ["name"] = name,
            ["overview"] = string.Empty,
            ["owner"] = "Test",
            ["targetAbi"] = targetAbi,
            ["timestamp"] = "2026-11-22T00:00:00Z",
            ["version"] = version,
            ["status"] = status,
            ["autoUpdate"] = true,
            ["assemblies"] = Array.Empty<string>()
        };
        File.WriteAllText(Path.Combine(folder, "meta.json"), JsonSerializer.Serialize(manifest));
    }

    /// <summary>Gets the full path of a folder under <see cref="PluginsPath"/>.</summary>
    /// <param name="folderName">The folder name, such as <c>Bookmarks_2.5.0.0</c>.</param>
    /// <returns>The path.</returns>
    public string Folder(string folderName) => Path.Combine(PluginsPath, folderName);

    /// <summary>
    /// Adds an older build of the plugin without the guard: a synthetic DLL of the plugin's file name and a meta.json.
    /// </summary>
    /// <param name="folderName">The folder name.</param>
    /// <param name="assemblyFileName">The plugin's DLL file name.</param>
    /// <param name="id">The plugin id.</param>
    /// <param name="version">The version.</param>
    /// <param name="hasGuard">Whether the build has the duplicate guard.</param>
    /// <returns>The folder.</returns>
    public string AddBuild(string folderName, string assemblyFileName, Guid id, Version version, bool hasGuard = false)
    {
        var folder = Folder(folderName);
        WriteAssembly(Path.Combine(folder, assemblyFileName), Path.GetFileNameWithoutExtension(assemblyFileName), version, hasGuard);
        WriteManifest(folder, id, folderName.Split('_')[0], version.ToString());
        return folder;
    }

    /// <summary>
    /// Copies the real plugin assembly into <paramref name="folderName"/> with a meta.json and loads the copy in its own
    /// load context (dependencies come from the test's own context).
    /// </summary>
    /// <param name="pluginAssembly">The plugin assembly the test references.</param>
    /// <param name="folderName">The folder name.</param>
    /// <param name="id">The plugin id.</param>
    /// <returns>The loaded copy.</returns>
    public Assembly LoadCopy(Assembly pluginAssembly, string folderName, Guid id)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        var folder = Folder(folderName);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(pluginAssembly.Location));
        File.Copy(pluginAssembly.Location, target, overwrite: true);
        WriteManifest(folder, id, folderName.Split('_')[0], pluginAssembly.GetName().Version!.ToString());
        var context = new AssemblyLoadContext("plugin copy " + folderName, isCollectible: true);
        _contexts.Add(context);
        return context.LoadFromAssemblyPath(target);
    }

    /// <summary>
    /// Runs every service registrator of <paramref name="assembly"/> on a server of <paramref name="serverVersion"/>.
    /// </summary>
    /// <param name="assembly">The plugin assembly (usually a <see cref="LoadCopy"/>).</param>
    /// <param name="serverVersion">The server version.</param>
    /// <returns>The registrations.</returns>
    public static ServiceCollection Register(Assembly assembly, string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(serverVersion));
        var services = new ServiceCollection();
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPluginServiceRegistrator).IsAssignableFrom(t)))
        {
            ((IPluginServiceRegistrator)Activator.CreateInstance(type)!).RegisterServices(services, host.Object);
        }

        return services;
    }

    /// <summary>Reads the kit's <c>KitAbiStatus</c> from the registrations (kit types are internal: by name).</summary>
    /// <param name="services">The registrations.</param>
    /// <returns>Whether the plugin is active and whether it stands down as a duplicate.</returns>
    public static (bool IsActive, bool IsDuplicate, string Message) ReadStatus(IServiceCollection services)
    {
        var descriptor = services.Single(d => d.ServiceType.FullName == "Jellyfin.Plugin.Kit.Hosting.KitAbiStatus");
        var status = descriptor.ImplementationInstance!;
        var type = status.GetType();
        return ((bool)type.GetProperty("IsActive")!.GetValue(status)!, (bool)type.GetProperty("IsDuplicate")!.GetValue(status)!, (string)type.GetProperty("Message")!.GetValue(status)!);
    }

    /// <summary>Reads the kit's process-wide <c>KitDuplicateGuard.IsStandingDown</c> of an assembly.</summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <returns>The value.</returns>
    public static bool IsStandingDown(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return (bool)assembly.GetType("Jellyfin.Plugin.Kit.Hosting.KitDuplicateGuard", throwOnError: true)!
            .GetProperty("IsStandingDown", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Unload();
        }

        _root.Dispose();
    }
}
