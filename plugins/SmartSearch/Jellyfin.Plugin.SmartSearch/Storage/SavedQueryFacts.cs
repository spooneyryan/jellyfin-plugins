using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Validation;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// What a stored query is, read once per record: the query (when this build can read it), whether it is supported and
/// valid, what it uses, and its hash. Checked as an administrator with every kind allowed, so an administrator-only field
/// is a fact here (<see cref="UsesAdminOnlyFields"/>), not an error; each run and save checks again as its caller.
/// </summary>
/// <param name="Definition">The query, or <c>null</c> when it can't be read (invalid JSON, a newer version).</param>
/// <param name="Supported">Whether this build understands it: no newer version, field, operator or sort key.</param>
/// <param name="Valid">Whether it has no errors.</param>
/// <param name="Errors">Its errors, at most 50.</param>
/// <param name="UsesPerUserFields">Whether a rule reads the user's own data (watch state).</param>
/// <param name="UsesAdminOnlyFields">Whether a rule uses an administrator-only field.</param>
/// <param name="Hash">Its canonical hash, or <c>null</c> when it isn't valid.</param>
internal sealed record SavedQueryFacts(
    SmartSearchQueryDefinition? Definition,
    bool Supported,
    bool Valid,
    IReadOnlyList<QueryValidationError> Errors,
    bool UsesPerUserFields,
    bool UsesAdminOnlyFields,
    string? Hash);
