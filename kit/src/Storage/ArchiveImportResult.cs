using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// What an import did, or would do, with the export's <c>Archive</c>.
/// </summary>
/// <param name="Records">How many archived records the export holds.</param>
/// <param name="Imported">How many were (or would be) imported.</param>
/// <param name="Note">Why others weren't, or <c>null</c>.</param>
internal sealed record ArchiveImportResult(int Records, int Imported, string? Note)
{
    /// <summary>
    /// Describes the result with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Records"] = Records,
        ["Imported"] = Imported,
        ["Note"] = Note
    };
}
