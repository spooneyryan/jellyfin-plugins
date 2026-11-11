namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// A query's canonical form (<see cref="QueryCanonicalizer"/>).
/// </summary>
/// <param name="Json">The canonical JSON, compact.</param>
/// <param name="Hash"><c>QueryHash</c>: <c>sha256:</c> and the lowercase hex SHA-256 of <paramref name="Json"/> in UTF-8.</param>
internal sealed record CanonicalQuery(string Json, string Hash);
