namespace Jellyfin.Plugin.SmartSearch.Query.Migration;

/// <summary>
/// What <see cref="QueryMigrator.Migrate"/> did.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="FromVersion">The version the query had (1 when it had none), or <c>null</c> when it wasn't a version.</param>
internal sealed record QueryMigrationResult(QueryMigrationStatus Status, int? FromVersion);
