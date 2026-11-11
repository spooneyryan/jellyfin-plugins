namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>One problem with a query.</summary>
public sealed class SmartSearchValidationErrorDto
{
    /// <summary>Gets or sets where, inside the query (<c>Where.Children[2].Values[0]</c>), or empty for the whole query.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets what, as a stable code (<c>UnknownField</c>, <c>OutOfRange</c>, …).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the same in plain English, without the query's values.</summary>
    public string Message { get; set; } = string.Empty;
}
