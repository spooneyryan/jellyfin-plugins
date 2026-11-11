using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// One record of the developer library demo: an item-linked record shaped like a bookmark's
/// lifecycle fields (<c>ItemId</c>, <c>Identity</c>, <c>AttachedUtc</c>, <c>DetachedUtc</c>, the relink mark and the
/// file-specific <c>MediaSourceId</c>), owned by a user, so the kit's verify sweep, re-resolution, archive and purge can be
/// proven against a real library before bookmarks got the same fields (1.1.0).
/// </summary>
internal sealed record LibraryDemoRecord
{
    /// <summary>Gets the record id.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the user the record belongs to; the kit resolves it in that user's view.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the library item the record is on (the snapshot's <c>ItemId</c>).</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the snapshot of the item.</summary>
    public IdentitySnapshot Identity { get; init; } = new();

    /// <summary>Gets a free text, to see that it survives the archive.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Gets the media source the record was made on (the item id in the "N" format, as Bookmarks stores it): cleared by a
    /// relink to another file, rewritten by an Exact one.
    /// </summary>
    public string? MediaSourceId { get; init; }

    /// <summary>Gets when the record was written.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets when the record got its current item id (as a bookmark's <c>AttachedUtc</c>).</summary>
    public DateTime? AttachedUtc { get; init; }

    /// <summary>Gets when the record was detached, or null while it is attached.</summary>
    public DateTime? DetachedUtc { get; init; }

    /// <summary>Gets what detached it: <c>Removed</c> (its item's event), <c>Container</c> (a removed series, season or
    /// album), <c>Sweep</c> (the verify sweep).</summary>
    public string? DetachedBy { get; init; }

    /// <summary>Gets when a relink to another file set the "relinked, check positions" mark.</summary>
    public DateTime? RelinkedUtc { get; init; }

    /// <summary>Gets how the last relink matched, when it wasn't Exact.</summary>
    public MatchStrength? RelinkStrength { get; init; }

    /// <summary>Gets the last re-resolution's answer for a detached record (<c>Unmatched</c>, <c>Ambiguous: reason</c>).</summary>
    public string? LastOutcome { get; init; }

    /// <summary>Gets the fields this version doesn't know.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Gets the record id as the kit's lifecycle names it.</summary>
    [JsonIgnore]
    public string Key => Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
