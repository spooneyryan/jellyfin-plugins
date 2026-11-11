using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.Bookmarks.Model;

/// <summary>
/// A user's bookmark on a video, schema version 2: a position, or a range when <see cref="EndTicks"/> is set,
/// with a name, a note, the identity of its video and, when one was made, its clip.
/// </summary>
/// <remarks>
/// <para>Internal since schema 2: it holds the kit's internal <see cref="IdentitySnapshot"/>, so only DTOs cross
/// the plugin's public boundary. Records are immutable; a change is a <c>with</c> copy, which keeps <see cref="Extra"/>.</para>
/// <para>A record is <b>live</b> while <see cref="DeletedUtc"/> is null. A deleted bookmark whose clip file was kept is a
/// <b>clip-only tombstone</b>: only clip operations see it. <see cref="BookmarkRecordRules"/> holds the rules.</para>
/// </remarks>
internal sealed record Bookmark
{
    private readonly IdentitySnapshot _identity = BookmarkRecordRules.NoIdentity;

    /// <summary>
    /// Gets the bookmark id.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Gets the id of the user who owns the bookmark.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Gets the id of the bookmarked library item. <see cref="Guid.Empty"/> only for <see cref="BookmarkOrigin.Adopted"/>
    /// records (from 1.3.0), which no code may look up: <see cref="BookmarkRecordRules.HasLibraryItem"/> guards every
    /// lookup, because 10.11.5's <c>GetItemById(Guid.Empty)</c> throws.
    /// </summary>
    public Guid ItemId { get; init; }

    /// <summary>
    /// Gets the media source (version) that was playing, when known.
    /// </summary>
    public string? MediaSourceId { get; init; }

    /// <summary>
    /// Gets the audio stream index that was playing, when known. Used as the clip's audio track.
    /// </summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>
    /// Gets the bookmarked position, or the start of the range, in ticks.
    /// </summary>
    public long StartTicks { get; init; }

    /// <summary>
    /// Gets the end of the range in ticks, or <c>null</c> for a single position.
    /// </summary>
    public long? EndTicks { get; init; }

    /// <summary>
    /// Gets the bookmark's name: one line of at most 100 characters, or <c>null</c> when it is unnamed.
    /// Since schema 2; the 1→2 migration moves a short single-line 1.0 note here.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the user's note: free text of at most 500 characters, or <c>null</c>.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Gets a display name for the item, captured when the bookmark was created so the bookmark stays readable if the
    /// item is later removed or re-identified, for example <c>Heat (1995)</c> or <c>Show S01E02 - Title</c>.
    /// </summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>
    /// Gets what the bookmark remembers about its video, never null: a record read without one (a hand edit)
    /// gets an empty snapshot. Replaces 1.0's <c>ItemType</c> (now <see cref="IdentitySnapshot.Kind"/>) and
    /// <c>ItemProviderIds</c> (now <see cref="IdentitySnapshot.ProviderIds"/>). <see cref="IdentitySnapshot.Path"/> is
    /// admin-only. Since schema 2.
    /// </summary>
    public IdentitySnapshot Identity
    {
        get => _identity;
        init => _identity = value ?? BookmarkRecordRules.NoIdentity;
    }

    /// <summary>
    /// Gets where the record came from: <see cref="BookmarkOrigin.Native"/> unless it was imported or adopted. Since
    /// schema 2.
    /// </summary>
    public BookmarkOrigin Origin { get; init; }

    /// <summary>
    /// Gets when the record got its current <see cref="ItemId"/> (UTC): when it was written on the item, relinked by the
    /// id, path or provider-id steps, or, when a move-journal line moved it, that line's time. The resolver follows only
    /// journal lines committed after it, because a path used again gives an old id again. Migrated 1.0 records get their
    /// <see cref="CreatedAt"/>; <c>null</c> follows the whole journal. Since schema 2.
    /// </summary>
    public DateTime? AttachedUtc { get; init; }

    /// <summary>
    /// Gets when the item was found removed from the library (UTC); the record waits to be reattached.
    /// <c>null</c> while it is attached, and always on adopted records. Since schema 2.
    /// </summary>
    public DateTime? DetachedUtc { get; init; }

    /// <summary>
    /// Gets when the record was reattached to a different file by a provider-id match, which shows the
    /// "Relinked, check position" badge until the next successful update or rename. Since schema 2.
    /// </summary>
    public DateTime? RelinkedUtc { get; init; }

    /// <summary>
    /// Gets how the record was reattached when <see cref="RelinkedUtc"/> is set: <see cref="MatchStrength.Strong"/>, the
    /// only value written (Exact relinks leave no badge). Since schema 2.
    /// </summary>
    public MatchStrength? RelinkStrength { get; init; }

    /// <summary>
    /// Gets when the bookmark was deleted while its clip file was kept (UTC): the record is then a clip-only tombstone
    /// <c>null</c> for a live bookmark. Since schema 2.
    /// </summary>
    public DateTime? DeletedUtc { get; init; }

    /// <summary>
    /// Gets when the bookmark was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Gets when the bookmark was last changed (UTC). It is the API's <c>IfUpdatedAt</c> token.
    /// </summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>
    /// Gets the latest clip extraction for this bookmark, if any was requested.
    /// </summary>
    public ClipInfo? Clip { get; init; }

    /// <summary>
    /// Gets the stored fields this version doesn't know, so a round trip through <c>bookmarks.json</c> keeps them.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
