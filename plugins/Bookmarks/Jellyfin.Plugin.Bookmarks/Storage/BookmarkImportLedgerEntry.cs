using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// What an import did with one source entry, keyed in the document's <c>ImportLedger</c> by
/// <c>je:&lt;userIdN&gt;:&lt;JE key&gt;</c>: running the import again skips entries already imported, unless the source
/// changed later and our copy didn't.
/// </summary>
internal sealed record BookmarkImportLedgerEntry
{
    /// <summary>
    /// Gets the bookmark the entry became, or <see cref="Guid.Empty"/> when none was written (a duplicate).
    /// </summary>
    public Guid BookmarkId { get; init; }

    /// <summary>
    /// Gets the source entry's own <c>UpdatedAt</c> when it was imported (UTC), else its <c>CreatedAt</c>, if it had one.
    /// A later run re-imports the entry only when the source's time is later than this one.
    /// </summary>
    public DateTime? SourceUpdatedAt { get; init; }

    /// <summary>
    /// Gets when the entry was imported (UTC).
    /// </summary>
    public DateTime ImportedAt { get; init; }

    /// <summary>
    /// Gets the <c>UpdatedAt</c> the import gave the bookmark (UTC; the source's own time). While the bookmark still has it,
    /// nobody changed it here since, so a source entry changed later may replace it; once a user edits it, the local edit
    /// wins. Since 1.1.0.
    /// </summary>
    public DateTime? RecordUpdatedAt { get; init; }

    /// <summary>
    /// Gets the outcome, for example <c>Imported</c> or <c>Duplicate</c>. Text rather than an enum, so an outcome a later
    /// release adds never makes the file unreadable here.
    /// </summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>
    /// Gets fields this version doesn't know, kept on a round trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
