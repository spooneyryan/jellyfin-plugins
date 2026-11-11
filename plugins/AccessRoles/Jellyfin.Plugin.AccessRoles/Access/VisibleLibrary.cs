using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// A library a user can open, with every reason (<c>Libraries.Visible</c>).
/// </summary>
/// <param name="Library">The library.</param>
/// <param name="Sources">The reasons, from <see cref="AccessSources"/>.</param>
internal sealed record VisibleLibrary(CatalogLibrary Library, IReadOnlyList<string> Sources);
