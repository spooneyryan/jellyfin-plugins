using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>What <c>POST /SmartSearch/Validate</c> found.</summary>
public sealed class SmartSearchValidationDto
{
    /// <summary>Gets or sets a value indicating whether the query has no errors.</summary>
    public bool Valid { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this version of Smart Search can read the query (no newer version, unknown
    /// field, operator or sort key).
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>Gets or sets the errors, each with its path inside the query.</summary>
    public IReadOnlyList<SmartSearchValidationErrorDto> Errors { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether a rule reads the user's own data (watch state).</summary>
    public bool UsesPerUserFields { get; set; }

    /// <summary>Gets or sets a value indicating whether a rule uses an administrator-only field.</summary>
    public bool UsesAdminOnlyFields { get; set; }

    /// <summary>Gets or sets the fields the query uses that this server can't answer now.</summary>
    public IReadOnlyList<string> UnavailableFields { get; set; } = [];

    /// <summary>
    /// Gets or sets, for an administrator, the path a search would take, for a query that can run: <c>Fast</c> (the
    /// database answers it alone), <c>Residual</c> (its candidates are checked in memory) or <c>Empty</c> (it can match
    /// nothing, such as a scope of libraries the user can't open); empty for a query that can't run. Absent for other
    /// users: a plan depends on what the server holds, beyond what they can see.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; set; }
}
