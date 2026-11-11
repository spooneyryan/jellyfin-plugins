using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// The index's content, as <see cref="MovieIndexBuilder"/> builds it.
/// </summary>
/// <param name="Entries">The entries, in the order of their first indexed member.</param>
/// <param name="Libraries">The libraries read.</param>
/// <param name="Stats">The counts.</param>
/// <param name="LibraryQueries">The <c>GetItemIds</c> calls made.</param>
/// <param name="ItemQueries">The <c>GetItemList</c> calls made.</param>
internal sealed record MovieIndexContent(IReadOnlyList<VersionEntry> Entries, IReadOnlyList<IndexLibrary> Libraries, IndexStats Stats, int LibraryQueries, int ItemQueries);
