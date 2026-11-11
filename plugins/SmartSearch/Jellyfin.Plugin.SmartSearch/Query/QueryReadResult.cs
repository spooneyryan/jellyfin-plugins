using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Migration;
using Jellyfin.Plugin.SmartSearch.Query.Validation;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// What <see cref="QueryJson.Read(string, QueryMigrator?)"/> made of a query's JSON.
/// </summary>
internal sealed class QueryReadResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QueryReadResult"/> class.
    /// </summary>
    /// <param name="definition">The query, or <c>null</c> when it couldn't be read.</param>
    /// <param name="errors">Why not.</param>
    /// <param name="migration">What the migrator did, or <c>null</c> when it didn't run.</param>
    public QueryReadResult(SmartSearchQueryDefinition? definition, IReadOnlyList<QueryValidationError> errors, QueryMigrationStatus? migration)
    {
        Definition = definition;
        Errors = errors;
        Migration = migration;
    }

    /// <summary>Gets the query, at the current version; <c>null</c> when <see cref="Errors"/> isn't empty.</summary>
    public SmartSearchQueryDefinition? Definition { get; }

    /// <summary>Gets why the query couldn't be read.</summary>
    public IReadOnlyList<QueryValidationError> Errors { get; }

    /// <summary>Gets what the migrator did, or <c>null</c> when the text wasn't a JSON object.</summary>
    public QueryMigrationStatus? Migration { get; }

    /// <summary>
    /// Gets a value indicating whether this build can read the query. When it can't (a newer version, an unknown
    /// operator), the caller keeps the original text as it is and marks the query unsupported.
    /// </summary>
    public bool Supported => !Errors.Any(e => QueryErrorCodes.Unsupported.Contains(e.Code));
}
