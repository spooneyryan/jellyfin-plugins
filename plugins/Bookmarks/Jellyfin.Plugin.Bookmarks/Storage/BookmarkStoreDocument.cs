using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// The on-disk shape of the bookmark database, <c>bookmarks.json</c>: schema version 2 (since 1.1.0).
/// The kit's <see cref="JsonDocumentStore{TDoc}"/> keeps it, so instances are immutable snapshots; change them
/// through <see cref="BookmarkStore"/>, which keeps the invariants below.
/// </summary>
/// <remarks>
/// <para>Version 1 (Bookmarks 1.0) held only <c>Version</c> and <c>Bookmarks</c>; <c>BookmarkStoreMigration1To2</c>
/// upgrades it. Properties are written in the order declared here, nulls omitted.</para>
/// <para>Every collection reads as empty when the file has none or <c>null</c>, so code never meets a null list.</para>
/// </remarks>
internal sealed record BookmarkStoreDocument : IVersionedDocument
{
    /// <summary>
    /// The current schema version: 2 since named bookmarks (1.1.0).
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// The most clip folders <see cref="ClipRoots"/> holds.
    /// </summary>
    public const int MaxClipRoots = 50;

    /// <summary>
    /// The most entries <see cref="PendingFileDeletes"/> holds.
    /// </summary>
    public const int MaxPendingFileDeletes = 1000;

    private readonly IReadOnlyList<string> _clipRoots = [];
    private readonly IReadOnlyDictionary<string, string> _clipRootTokens = ImmutableSortedDictionary.Create<string, string>(ClipRootPaths.Comparer);
    private readonly IReadOnlyDictionary<Guid, BookmarkUserPreferences> _userPreferences = ImmutableDictionary<Guid, BookmarkUserPreferences>.Empty;
    private readonly IReadOnlyDictionary<string, BookmarkImportLedgerEntry> _importLedger = ImmutableSortedDictionary.Create<string, BookmarkImportLedgerEntry>(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, BookmarkImportRun> _importRuns = ImmutableSortedDictionary.Create<string, BookmarkImportRun>(StringComparer.Ordinal);
    private readonly KnownUsers _knownUsers = KnownUsers.Empty;
    private readonly IReadOnlyList<PendingFileDelete> _pendingFileDeletes = [];
    private readonly IReadOnlyList<Bookmark> _bookmarks = [];

    /// <summary>
    /// Gets the schema version the file was written with.
    /// </summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// Gets every folder clips were written under: normalised absolute paths without a trailing separator,
    /// distinct (ignoring case on Windows), at most <see cref="MaxClipRoots"/>. Recorded when a clip is planned, seeded by
    /// the migration; every clip file operation checks its path against them.
    /// </summary>
    public IReadOnlyList<string> ClipRoots
    {
        get => _clipRoots;
        init => _clipRoots = value ?? [];
    }

    /// <summary>
    /// Gets the token of each clip folder's marker file (<c>Clips/ClipRootMarker</c>; since 1.2.0), by the
    /// folder as <see cref="ClipRoots"/> records it, compared as clip folder paths are: a folder whose marker names another
    /// token (or none, once one is recorded) isn't the folder its clips were made in, but an older drive or a backup copy back
    /// under the same path. Written by the clip worker, the missing-clip sweep and an admin's confirmation, with the marker.
    /// Added to schema 2 before 1.2.0 shipped; a file without it reads as empty, and it is written only once it has an entry
    /// (<see cref="ClipRootTokensJson"/>), so a schema 2 file without tokens is the same bytes as before, and Bookmarks 1.1
    /// keeps it as a field it doesn't know (<see cref="Extra"/>).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ClipRootTokens
    {
        get => _clipRootTokens;
        init => _clipRootTokens = TokensOf(value);
    }

    /// <summary>
    /// Gets <see cref="ClipRootTokens"/> as the file holds it: <c>ClipRootTokens</c>, left out while it is empty.
    /// </summary>
    [JsonPropertyName("ClipRootTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ClipRootTokensJson
    {
        get => _clipRootTokens.Count == 0 ? null : _clipRootTokens;
        init => _clipRootTokens = TokensOf(value);
    }

    /// <summary>
    /// Gets each user's naming preferences, by user id.
    /// </summary>
    [JsonConverter(typeof(UserIdMapJsonConverter<BookmarkUserPreferences>))]
    public IReadOnlyDictionary<Guid, BookmarkUserPreferences> UserPreferences
    {
        get => _userPreferences;
        init => _userPreferences = value ?? ImmutableDictionary<Guid, BookmarkUserPreferences>.Empty;
    }

    /// <summary>
    /// Gets the import ledger, by source key (<c>je:&lt;userIdN&gt;:&lt;JE key&gt;</c>).
    /// </summary>
    public IReadOnlyDictionary<string, BookmarkImportLedgerEntry> ImportLedger
    {
        get => _importLedger;
        init => _importLedger = value ?? ImmutableSortedDictionary.Create<string, BookmarkImportLedgerEntry>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the applied imports, by source (<c>je:&lt;userIdN&gt;</c>, one Jellyfin Enhanced user's file): when each
    /// was last applied and what it did. Written with the ledger by every applied import, never by a dry run; the settings
    /// page shows the switch-off checklist once every Jellyfin Enhanced user with bookmarks has one. Added to schema 2
    /// with the Jellyfin Enhanced import, before 1.1.0 shipped; a file without it reads as empty, and it is written only
    /// once it has an entry (<see cref="ImportRunsJson"/>), so a schema 2 file without imports is the same bytes as before.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, BookmarkImportRun> ImportRuns
    {
        get => _importRuns;
        init => _importRuns = value ?? ImmutableSortedDictionary.Create<string, BookmarkImportRun>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets <see cref="ImportRuns"/> as the file holds it: <c>ImportRuns</c>, left out while it is empty.
    /// </summary>
    [JsonPropertyName("ImportRuns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, BookmarkImportRun>? ImportRunsJson
    {
        get => _importRuns.Count == 0 ? null : _importRuns;
        init => _importRuns = value ?? ImmutableSortedDictionary.Create<string, BookmarkImportRun>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the last seen name of each user who owns records: refreshed on writes and by the reconciler, and
    /// what <i>Remap by name</i> and an export's <c>Users[]</c> use.
    /// </summary>
    public KnownUsers KnownUsers
    {
        get => _knownUsers;
        init => _knownUsers = value ?? KnownUsers.Empty;
    }

    /// <summary>
    /// Gets the move journal this store last caught up with, or <c>null</c> before the first catch-up. A
    /// different journal id resets <see cref="LastMoveSeq"/> to 0.
    /// </summary>
    public Guid? MoveJournalId { get; init; }

    /// <summary>
    /// Gets the last move-journal line this store has applied.
    /// </summary>
    public long LastMoveSeq { get; init; }

    /// <summary>
    /// Gets the old clip files waiting to be deleted, at most <see cref="MaxPendingFileDeletes"/>, one per path.
    /// </summary>
    public IReadOnlyList<PendingFileDelete> PendingFileDeletes
    {
        get => _pendingFileDeletes;
        init => _pendingFileDeletes = value ?? [];
    }

    /// <summary>
    /// Gets every record, live bookmarks and clip-only tombstones, ordered by creation time and then id when written.
    /// </summary>
    public IReadOnlyList<Bookmark> Bookmarks
    {
        get => _bookmarks;
        init => _bookmarks = value ?? [];
    }

    /// <summary>
    /// Gets document-level fields this version doesn't know, so a round trip keeps them, as
    /// <see cref="Bookmark.Extra"/> does for each record.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Creates the document of a new store.
    /// </summary>
    /// <returns>An empty document at the current version.</returns>
    public static BookmarkStoreDocument CreateEmpty() => new();

    /// <summary>
    /// Gets the document with a clip folder's marker token recorded (<see cref="ClipRootTokens"/>), replacing an earlier one.
    /// </summary>
    /// <param name="root">The folder, normalised as <see cref="ClipRoots"/> records it.</param>
    /// <param name="token">The token its marker file names.</param>
    /// <returns>The new document.</returns>
    public BookmarkStoreDocument WithClipRootToken(string root, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(token);
        return this with { ClipRootTokens = ImmutableSortedDictionary.CreateRange(ClipRootPaths.Comparer, _clipRootTokens).SetItem(root, token) };
    }

    // The tokens as a dictionary compared as clip folder paths are (without regard to case on Windows), in a stable order for
    // the file; of two spellings of one folder in a hand-edited file the last wins, and an empty key or token is dropped.
    private static ImmutableSortedDictionary<string, string> TokensOf(IReadOnlyDictionary<string, string>? tokens)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(ClipRootPaths.Comparer);
        foreach (var (root, token) in tokens ?? ImmutableDictionary<string, string>.Empty)
        {
            if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(token))
            {
                builder[root] = token;
            }
        }

        return builder.ToImmutable();
    }
}
