using System;
using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// Store files written by Bookmarks, embedded byte for byte (see <c>Fixtures/README.md</c>).
/// </summary>
internal static class StoreFixtures
{
    /// <summary>A <c>bookmarks.json</c> written by Bookmarks 1.0.0 in the default sandbox (schema 1).</summary>
    public const string Bookmarks100 = "bookmarks-1.0.0.json";

    /// <summary>
    /// The <c>bookmarks.json</c> the Bookmarks 1.0 development build wrote in its own sandbox before the release (schema 1):
    /// clips without a file range, a cancelled remake that kept its file.
    /// </summary>
    public const string Bookmarks10DevSandbox = "bookmarks-1.0-dev-sandbox.json";

    /// <summary>The number of records in <see cref="Bookmarks10DevSandbox"/>.</summary>
    public const int Bookmarks10DevSandboxRecords = 14;

    /// <summary>
    /// <see cref="Bookmarks100"/> with one record's <c>Note</c> written twice, as a hand edit could leave it (schema 1): the
    /// 1→2 upgrade refuses it and leaves it untouched (<c>ReadOnlyMigrationFailed</c>).
    /// </summary>
    public const string Bookmarks100DuplicateKey = "bookmarks-1.0-duplicate-key.json";

    /// <summary>A schema 2 <c>bookmarks.json</c> holding every field of schema 2, written by a 1.1 development build.</summary>
    public const string Schema2 = "bookmarks-schema2.json";

    /// <summary>The number of records in <see cref="Schema2"/>, tombstones included.</summary>
    public const int Schema2Records = 6;

    /// <summary>
    /// A <c>bookmarks.json</c> of a schema 3 that doesn't exist yet: <see cref="Bookmarks100"/> as a 1.1 development build
    /// upgraded it in the default sandbox, at version 3, with a queued and a running clip job and fields of the later
    /// version (read-only, worker idle).
    /// </summary>
    public const string Schema3 = "bookmarks-schema3.json";

    public static byte[] Read(string name)
    {
        using var stream = typeof(StoreFixtures).Assembly.GetManifestResourceStream("Fixtures." + name)
            ?? throw new InvalidOperationException("No embedded fixture " + name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
