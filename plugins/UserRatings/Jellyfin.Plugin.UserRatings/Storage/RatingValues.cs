using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The growable enums of <c>userratings.db</c>: stored as text, validated here rather than with
/// CHECK constraints, which SQLite can't alter without a table rebuild. A later version adds a value here and
/// nowhere else; a value this version doesn't know is refused on every write.
/// </summary>
internal static class RatingValues
{
    /// <summary>
    /// Refuses a value a set doesn't hold.
    /// </summary>
    /// <param name="known">The known values.</param>
    /// <param name="value">The value.</param>
    /// <param name="column">The column, for the message.</param>
    /// <exception cref="ArgumentException">The value is unknown.</exception>
    public static void Require(IReadOnlySet<string> known, string? value, string column)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (value is null || !known.Contains(value))
        {
            throw new ArgumentException($"'{value}' is not a {column} this version knows.", column);
        }
    }

    /// <summary>Where a rating came from (<c>Ratings.Origin</c>).</summary>
    public static class Origin
    {
        /// <summary>Made in the plugin's UI or API.</summary>
        public const string User = "User";

        /// <summary>Imported from Jellyfin Enhanced's <c>reviews.json</c>.</summary>
        public const string JellyfinEnhanced = "JellyfinEnhanced";

        /// <summary>Imported from Jellyfin's native <c>UserData.Rating</c> (1.1).</summary>
        public const string NativeRating = "NativeRating";

        /// <summary>Imported from an export envelope.</summary>
        public const string Import = "Import";

        /// <summary>Gets every known value.</summary>
        public static IReadOnlySet<string> All { get; } = ImmutableHashSet.Create(StringComparer.Ordinal, User, JellyfinEnhanced, NativeRating, Import);
    }

    /// <summary>Why a rating is detached from its item (<c>Ratings.DetachReason</c>).</summary>
    public static class DetachReason
    {
        /// <summary>The item (or its series, season or album) was removed.</summary>
        public const string Removed = "Removed";

        /// <summary>The verify sweep didn't find the item any more.</summary>
        public const string Missing = "Missing";

        /// <summary>An imported rating whose item isn't in the library (yet).</summary>
        public const string Unmatched = "Unmatched";

        /// <summary>Several items match its identity; an admin relinks it.</summary>
        public const string Ambiguous = "Ambiguous";

        /// <summary>Gets every known value.</summary>
        public static IReadOnlySet<string> All { get; } = ImmutableHashSet.Create(StringComparer.Ordinal, Removed, Missing, Unmatched, Ambiguous);
    }

    /// <summary>What a moderation log row records (<c>ModerationLog.Action</c>).</summary>
    public static class ModerationAction
    {
        /// <summary>A moderator removed a rating and its review.</summary>
        public const string RemoveEntry = "RemoveEntry";

        /// <summary>A moderator removed a review's text, keeping the stars.</summary>
        public const string RemoveText = "RemoveText";

        /// <summary>An admin relinked a detached rating.</summary>
        public const string Relink = "Relink";

        /// <summary>The retention step moved detached ratings into the archive.</summary>
        public const string Archive = "Archive";

        /// <summary>An admin purged detached or archived ratings.</summary>
        public const string Purge = "Purge";

        /// <summary>A Jellyfin Enhanced import was applied.</summary>
        public const string ImportJe = "ImportJe";

        /// <summary>A native-ratings import was applied.</summary>
        public const string ImportNative = "ImportNative";

        /// <summary>An export envelope was imported.</summary>
        public const string ImportEnvelope = "ImportEnvelope";

        /// <summary>A deleted user's ratings were deleted (the Delete policy).</summary>
        public const string UserDeleted = "UserDeleted";

        /// <summary>A deleted user's ratings were kept as a former member's (the Anonymize policy).</summary>
        public const string UserAnonymized = "UserAnonymized";

        /// <summary>A deleted user's ratings were kept under the old id (the KeepAsOrphan policy).</summary>
        public const string UserOrphaned = "UserOrphaned";

        /// <summary>An admin's Remap by name gave an unknown id's ratings to a user: ids and counts only.</summary>
        public const string UserRemapped = "UserRemapped";

        /// <summary>The mirror into <c>UserData.Rating</c> was backfilled (1.1).</summary>
        public const string MirrorBackfill = "MirrorBackfill";

        /// <summary>An admin restored a snapshot.</summary>
        public const string RestoreSnapshot = "RestoreSnapshot";

        /// <summary>An admin detached ratings kept on items that are gone (Data → Detached &amp; ambiguous, Detach now).</summary>
        public const string Detach = "Detach";

        /// <summary>An admin started the ratings empty while the database was damaged.</summary>
        public const string StartEmpty = "StartEmpty";

        /// <summary>Gets every known value.</summary>
        public static IReadOnlySet<string> All { get; } = ImmutableHashSet.Create(
            StringComparer.Ordinal, RemoveEntry, RemoveText, Relink, Archive, Purge, ImportJe, ImportNative, ImportEnvelope, UserDeleted, UserAnonymized, UserOrphaned, UserRemapped, MirrorBackfill, RestoreSnapshot, Detach, StartEmpty);
    }

    /// <summary>
    /// Where a deletion was made (<c>Tombstones.Origin</c>, <c>FormerUsers.Origin</c>; schema 4, since
    /// 0.3.20): on this server (<see cref="Local"/>: a member's delete, a moderator's removal, the deleted-user policy,
    /// a Purge), or by an envelope import (<c>Import:&lt;job id&gt;</c>). A restore re-applies only this server's own
    /// deletions made after the snapshot; an import's tombstones and former members are part of that import, so restoring its
    /// snapshot, or an earlier one, undoes them with the rest of it. Rows from before schema 4 read <see cref="Local"/>.
    /// </summary>
    public static class DeletionOrigin
    {
        /// <summary>Made on this server.</summary>
        public const string Local = "Local";

        /// <summary>The prefix of an import's origin; the job's id follows.</summary>
        public const string ImportPrefix = "Import:";

        /// <summary>Gets the origin of an import's deletions.</summary>
        /// <param name="jobId">The import job's id.</param>
        /// <returns><c>Import:</c> and the id in its <c>N</c> form.</returns>
        public static string Import(Guid jobId) => ImportPrefix + jobId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Gets whether a deletion was made on this server (a missing origin counts as one).</summary>
        /// <param name="origin">The origin.</param>
        /// <returns>Whether it is local.</returns>
        public static bool IsLocal(string? origin) => origin is null || !origin.StartsWith(ImportPrefix, StringComparison.Ordinal);
    }

    /// <summary>Who deleted a rating (<c>Tombstones.DeletedBy</c>).</summary>
    public static class DeletedBy
    {
        /// <summary>Its author.</summary>
        public const string User = "User";

        /// <summary>A moderator ("Remove rating and review").</summary>
        public const string Moderator = "Moderator";

        /// <summary>The Delete policy, when its author was deleted.</summary>
        public const string UserDeleted = "UserDeleted";

        /// <summary>An admin's purge.</summary>
        public const string Purge = "Purge";

        /// <summary>Gets every known value.</summary>
        public static IReadOnlySet<string> All { get; } = ImmutableHashSet.Create(StringComparer.Ordinal, User, Moderator, UserDeleted, Purge);
    }
}
