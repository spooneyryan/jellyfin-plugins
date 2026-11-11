using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// One imported source entry (<c>ImportLedger</c>) with the rows it produced (<c>ImportLedgerRows</c>: several
/// with <c>Copies = All</c>).
/// </summary>
/// <param name="Source">The source (<c>JE</c>, <c>Envelope</c>...).</param>
/// <param name="SourceKey">The entry's key in the source (a Jellyfin Enhanced key, an envelope row id).</param>
/// <param name="SourceUpdatedUtc">The entry's last change in the source (UTC).</param>
/// <param name="ImportedUtc">When it was imported (UTC).</param>
/// <param name="RatingIds">The rows it produced.</param>
internal sealed record ImportLedgerRecord(string Source, string SourceKey, DateTime SourceUpdatedUtc, DateTime ImportedUtc, IReadOnlyList<Guid> RatingIds);
