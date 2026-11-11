using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// One item of a problem's <c>errors</c>: <c>path</c>, <c>code</c> and <c>message</c>, camelCase like the problem's own
/// members (<c>title</c>, <c>status</c>, <c>detail</c>, <c>code</c>), whatever naming the server's JSON options use.
/// </summary>
/// <param name="Path">Where, inside the request or the query; empty for the whole.</param>
/// <param name="Code">What, as a stable code.</param>
/// <param name="Message">The same in plain English.</param>
internal sealed record ProblemError(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message)
{
    /// <summary>Gets the problem items of validation errors.</summary>
    /// <param name="errors">The errors.</param>
    /// <returns>The items.</returns>
    public static List<ProblemError> Of(IEnumerable<SmartSearchValidationErrorDto> errors)
        => errors.Select(e => new ProblemError(e.Path, e.Code, e.Message)).ToList();
}
