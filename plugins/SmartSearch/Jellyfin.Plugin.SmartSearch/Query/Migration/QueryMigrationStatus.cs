namespace Jellyfin.Plugin.SmartSearch.Query.Migration;

/// <summary>
/// The outcome of <see cref="QueryMigrator.Migrate"/>.
/// </summary>
internal enum QueryMigrationStatus
{
    /// <summary>The query was at the current version (or had none, which is version 1).</summary>
    UpToDate,

    /// <summary>The query was upgraded to the current version.</summary>
    Migrated,

    /// <summary>The query is newer than this build: left alone, unsupported.</summary>
    Newer,

    /// <summary><c>Version</c> isn't a whole number of 1 or more: left alone, invalid.</summary>
    Invalid
}
