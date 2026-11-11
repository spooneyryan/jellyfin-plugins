using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The People-search filter's one query per page (measured on a large 10.11.5 sandbox): which of a
/// page's person names have at least one item in the given libraries.
/// </summary>
internal interface IPeopleLibraries
{
    /// <summary>
    /// Gets the names among <paramref name="names"/> that have an item whose top parent is one of
    /// <paramref name="topParentIds"/> (the libraries' physical folders).
    /// </summary>
    /// <param name="names">The page's names.</param>
    /// <param name="topParentIds">The caller's libraries' top-parent ids.</param>
    /// <returns>The names that do, compared ordinally.</returns>
    IReadOnlySet<string> NamesWithItemsIn(IReadOnlyCollection<string> names, IReadOnlyCollection<Guid> topParentIds);
}
