using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// What <see cref="ForeignMediaGuard"/> found in a folder unit.
/// </summary>
/// <param name="Entries">The manifest: every file and folder below the unit folder.</param>
/// <param name="ReparsePoints">Junctions and symbolic links inside (a blocker).</param>
/// <param name="Foreign">Videos and folders that belong to another item (a blocker, <c>move.foreign-media</c>).</param>
/// <param name="TooLarge">Whether the walk stopped at <see cref="ForeignMediaGuard.MaxEntries"/>.</param>
internal sealed record UnitTree(IReadOnlyList<UnitEntry> Entries, IReadOnlyList<string> ReparsePoints, IReadOnlyList<string> Foreign, bool TooLarge);
