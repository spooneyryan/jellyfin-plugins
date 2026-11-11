namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// One of Jellyfin Enhanced's keyboard shortcuts as its settings or a user's <c>shortcuts.json</c> hold it (JE 12.8 to 12.12
/// <c>Configuration.Shortcut</c>: <c>Name</c>, <c>Key</c>, <c>Label</c>, <c>Category</c>).
/// </summary>
/// <param name="Name">The action, such as <c>BookmarkCurrentTime</c>; empty for a malformed entry.</param>
/// <param name="Key">The key as written (<c>B</c>, <c>Shift+H</c>, <c>0-9</c>); <c>""</c> switches the action off,
/// <c>null</c> when the entry has no key.</param>
/// <param name="Label">JE's label, when the entry has one.</param>
/// <param name="Category">JE's category (<c>Global</c> or <c>Player</c>), when the entry has one.</param>
internal sealed record JeShortcut(string Name, string? Key, string? Label = null, string? Category = null);
