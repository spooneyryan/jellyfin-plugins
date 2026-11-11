namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// A library a user can't open, with the reason (<c>Libraries.Hidden</c>).
/// </summary>
/// <param name="Library">The library.</param>
/// <param name="Reason">The reason, from <see cref="AccessSources"/>.</param>
internal sealed record HiddenLibrary(CatalogLibrary Library, string Reason);
