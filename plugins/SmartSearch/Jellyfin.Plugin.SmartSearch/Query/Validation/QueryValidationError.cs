namespace Jellyfin.Plugin.SmartSearch.Query.Validation;

/// <summary>
/// One problem with a query.
/// </summary>
/// <param name="Path">Where: a property path such as <c>Where.Children[2].Values[0]</c>, or empty for the whole query.</param>
/// <param name="Code">What, as one of <see cref="QueryErrorCodes"/>.</param>
/// <param name="Message">The same in plain English, without the query's values.</param>
internal sealed record QueryValidationError(string Path, string Code, string Message);
