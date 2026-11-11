using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.Query.Validation;

/// <summary>
/// What the validator found: the errors, and what the query uses.
/// </summary>
internal sealed class QueryValidationResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QueryValidationResult"/> class.
    /// </summary>
    /// <param name="errors">The errors, at most <see cref="QueryLimits.MaxErrors"/>.</param>
    /// <param name="usesPerUserFields">Whether a rule reads the user's data.</param>
    /// <param name="usesAdminOnlyFields">Whether a rule uses an admin-only field.</param>
    /// <param name="unavailableFields">The fields used that aren't available now, by id.</param>
    public QueryValidationResult(
        IReadOnlyList<QueryValidationError> errors,
        bool usesPerUserFields,
        bool usesAdminOnlyFields,
        IReadOnlyList<string> unavailableFields)
    {
        Errors = errors;
        UsesPerUserFields = usesPerUserFields;
        UsesAdminOnlyFields = usesAdminOnlyFields;
        UnavailableFields = unavailableFields;
    }

    /// <summary>Gets the errors.</summary>
    public IReadOnlyList<QueryValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether the query has no errors (it may be saved).</summary>
    public bool Valid => Errors.Count == 0;

    /// <summary>
    /// Gets a value indicating whether this build can read the query: no newer version, unknown field, operator or sort
    /// key. An unsupported query is kept as it is, but can't be run or edited.
    /// </summary>
    public bool Supported => !Errors.Any(e => QueryErrorCodes.Unsupported.Contains(e.Code));

    /// <summary>Gets a value indicating whether a rule reads the user's data (watch state).</summary>
    public bool UsesPerUserFields { get; }

    /// <summary>Gets a value indicating whether a rule uses an admin-only field (such a query can't be shared).</summary>
    public bool UsesAdminOnlyFields { get; }

    /// <summary>Gets the fields the query uses that aren't available now (<c>FieldUnavailable</c> on a run, not on a save).</summary>
    public IReadOnlyList<string> UnavailableFields { get; }

    /// <summary>Gets a value indicating whether the query can be run now.</summary>
    public bool Runnable => Valid && UnavailableFields.Count == 0;
}
