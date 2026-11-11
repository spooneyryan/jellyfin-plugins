using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// What a plugin's <see cref="SqliteImportAdapter.Apply"/> did to the tables.
/// </summary>
/// <param name="Counts">What changed.</param>
/// <param name="Notes">Sentences for the report, such as records skipped and why; never names or notes of users.</param>
internal sealed record SqliteImportApplied(ImportCounts Counts, IReadOnlyList<string> Notes);
