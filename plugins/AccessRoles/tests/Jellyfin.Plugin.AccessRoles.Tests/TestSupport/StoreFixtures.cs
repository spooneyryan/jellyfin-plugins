using System;
using System.IO;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// Store files the schema must read, embedded byte for byte (<c>Fixtures/*.json</c>; <c>.gitattributes</c> keeps git
/// from changing their line ends).
/// </summary>
internal static class StoreFixtures
{
    /// <summary>The example document of the data file's design, with its abbreviated ids written out (schema 1).</summary>
    public const string SchemaExample = "accessroles-schema1-example.json";

    /// <summary>The number of assignments in <see cref="SchemaExample"/>.</summary>
    public const int SchemaExampleRecords = 1;

    /// <summary>
    /// A schema 1 file as a newer build might write it: lock names, states, modes and overrides this build doesn't know,
    /// and unknown fields on the document and on every kind of record.
    /// </summary>
    public const string NewerValues = "accessroles-schema1-newer-values.json";

    /// <summary>The number of assignments in <see cref="NewerValues"/>.</summary>
    public const int NewerValuesRecords = 2;

    /// <summary>
    /// Reads a fixture's bytes.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Read(string name)
    {
        using var stream = typeof(StoreFixtures).Assembly.GetManifestResourceStream("Fixtures." + name)
            ?? throw new InvalidOperationException($"The test assembly doesn't embed Fixtures/{name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
