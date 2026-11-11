using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// A copy of a store file the store keeps until an admin deletes it ("listed on the Diagnostics tab"): the file
/// as it was before an upgrade (<c>&lt;file&gt;.v&lt;N&gt;-&lt;ts&gt;</c>), the one to put back to use the previous plugin
/// version again, or a file that couldn't be read and was moved aside (<c>&lt;file&gt;.corrupt-&lt;ts&gt;</c>).
/// </summary>
/// <param name="Name">The file name, next to the store file.</param>
/// <param name="Kind">Why it is kept.</param>
/// <param name="SchemaVersion">For a pre-upgrade copy, the schema version it holds (the <c>N</c> of its name).</param>
/// <param name="Utc">When it was made, from its name (UTC).</param>
/// <param name="Size">The size in bytes.</param>
internal sealed record StoreKeptFile(string Name, StoreKeptFileKind Kind, int? SchemaVersion, DateTime Utc, long Size)
{
    /// <summary>
    /// Describes the file with plain values, for Diagnostics.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Name"] = Name,
        ["Kind"] = Kind.ToString(),
        ["SchemaVersion"] = SchemaVersion,
        ["Utc"] = Utc,
        ["Size"] = Size
    };
}
