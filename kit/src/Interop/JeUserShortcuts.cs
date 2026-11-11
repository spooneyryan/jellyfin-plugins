using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// A user's personal Jellyfin Enhanced shortcuts (<c>&lt;userIdN&gt;/shortcuts.json</c>), which JE's script applies over
/// the server's (JE 12.8 to 12.12 <c>config.js</c>, <c>initializeShortcuts</c>: <c>Object.assign(active, defaults, userShortcuts)</c>).
/// </summary>
/// <param name="UserId">The user, from the folder's name.</param>
/// <param name="Shortcuts">The entries that override a server shortcut (a name and a key), in file order.</param>
/// <param name="Problem">Why the file couldn't be read, or null.</param>
internal sealed record JeUserShortcuts(Guid UserId, IReadOnlyList<JeShortcut> Shortcuts, string? Problem);
