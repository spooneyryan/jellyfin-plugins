using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Writes a minimal .NET library (no code, just its identity and its AssemblyRef table) for the reference-version
/// check of Deploy-Plugin.ps1: "server" assemblies with a given version, and "plugin" assemblies
/// that reference given versions of them, without building a project.
/// </summary>
internal static class SyntheticAssembly
{
    public static string Write(string path, string name, Version version, IEnumerable<(string Name, Version Version)>? references = null)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), version, default, default, default, AssemblyHashAlgorithm.Sha1);
        foreach (var reference in references ?? [])
        {
            metadata.AddAssemblyReference(metadata.GetOrAddString(reference.Name), reference.Version, default, default, default, default);
        }

        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }

    /// <summary>What the file says about itself, read back through the runtime, for the tests' own sanity.</summary>
    public static (string Name, Version Version, AssemblyName[] References) Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        var md = reader.GetMetadataReader();
        var definition = md.GetAssemblyDefinition();
        var references = new List<AssemblyName>();
        foreach (var handle in md.AssemblyReferences)
        {
            references.Add(md.GetAssemblyReference(handle).GetAssemblyName());
        }

        return (md.GetString(definition.Name), definition.Version, references.ToArray());
    }
}
