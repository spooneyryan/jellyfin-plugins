using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// An applied import of one source, kept in the document's <c>ImportRuns</c> by source (<c>je:&lt;userIdN&gt;</c>, one
/// Jellyfin Enhanced user's <c>bookmark.json</c>). A dry run never writes one. The settings page shows the checklist for
/// switching off Jellyfin Enhanced's bookmarks once every Jellyfin Enhanced user with bookmarks has one (only after a
/// successful import).
/// </summary>
internal sealed record BookmarkImportRun
{
    /// <summary>
    /// Gets when the import was last applied to this source (UTC).
    /// </summary>
    public DateTime AppliedUtc { get; init; }

    /// <summary>
    /// Gets the admin who last applied it, or <c>null</c> (left out of the file) when an API key did.
    /// </summary>
    public Guid? AppliedBy { get; init; }

    /// <summary>
    /// Gets how many entries the source held when it was applied, the malformed ones included.
    /// </summary>
    public int Found { get; init; }

    /// <summary>
    /// Gets how many bookmarks that run added.
    /// </summary>
    public int Imported { get; init; }

    /// <summary>
    /// Gets how many bookmarks that run rewrote from a source entry changed since its import.
    /// </summary>
    public int Updated { get; init; }

    /// <summary>
    /// Gets how many of the added bookmarks are not in the library (stored detached).
    /// </summary>
    public int Detached { get; init; }

    /// <summary>
    /// Gets how many entries that run left out as duplicates.
    /// </summary>
    public int Duplicates { get; init; }

    /// <summary>
    /// Gets how many entries that run found imported already.
    /// </summary>
    public int AlreadyImported { get; init; }

    /// <summary>
    /// Gets how many entries that run skipped (malformed, JE's cross-version copies, no such user).
    /// </summary>
    public int Skipped { get; init; }

    /// <summary>
    /// Gets fields this version doesn't know, kept on a round trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
