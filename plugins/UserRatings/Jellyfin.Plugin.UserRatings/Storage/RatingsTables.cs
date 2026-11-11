using System;
using System.Collections.Generic;
using Jellyfin.Plugin.UserRatings.Domain;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// Every table of the database an export holds, read in one read transaction so they agree with each other.
/// </summary>
/// <param name="Ratings">Every rating, attached and detached, by id.</param>
/// <param name="FormerUsers">The former members (the <c>Anonymize</c> policy).</param>
/// <param name="ModerationLog">The moderation log, oldest first.</param>
/// <param name="ImportLedger">The import ledger with each entry's rating ids.</param>
/// <param name="Tombstones">The tombstones.</param>
internal sealed record RatingsTables(
    IReadOnlyList<RatingRecord> Ratings,
    IReadOnlyList<FormerUserRecord> FormerUsers,
    IReadOnlyList<ModerationLogRecord> ModerationLog,
    IReadOnlyList<ImportLedgerRecord> ImportLedger,
    IReadOnlyList<TombstoneRecord> Tombstones);
