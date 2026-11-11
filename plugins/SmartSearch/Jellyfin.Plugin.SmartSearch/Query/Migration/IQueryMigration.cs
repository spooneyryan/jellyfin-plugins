using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.SmartSearch.Query.Migration;

/// <summary>
/// One step of <see cref="QueryMigrator"/>: it rewrites a query of schema version
/// <see cref="FromVersion"/> into version <c>FromVersion + 1</c>, in place, like the kit's store migrations.
/// </summary>
internal interface IQueryMigration
{
    /// <summary>Gets the version this step reads.</summary>
    int FromVersion { get; }

    /// <summary>Rewrites the query. The migrator sets <c>Version</c> afterwards; properties the step doesn't know stay.</summary>
    /// <param name="query">The query's root object.</param>
    void Apply(JsonObject query);
}
