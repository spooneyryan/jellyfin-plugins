using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// What <see cref="SidecarMatcher.Match"/> found in a file set's folder.
/// </summary>
/// <param name="Members">The entries that move: the unit's own videos, its extras named after a stem, the sidecars and the
/// trickplay folders.</param>
/// <param name="LeftBehind">Videos named like a stem that aren't the item's (a warning, <c>move.sidecar-left-behind</c>).</param>
/// <param name="OtherFolders">Folders named like a stem that aren't its trickplay folder: they stay (a warning).</param>
internal sealed record SidecarMatch(IReadOnlyList<FileSystemInfo> Members, IReadOnlyList<string> LeftBehind, IReadOnlyList<string> OtherFolders);
