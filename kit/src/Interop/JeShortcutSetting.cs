namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// One of a plugin's own shortcut settings, as the shortcut policy checks it (<see cref="JeInteropOptions.Shortcuts"/>).
/// </summary>
/// <param name="Name">The setting's name, such as <c>BookmarkNow</c>.</param>
/// <param name="Label">What the shortcut does, for the warning: "Bookmark this moment".</param>
/// <param name="Combo">The combination the admin set, as typed; <c>null</c> or empty when the shortcut is off.</param>
internal sealed record JeShortcutSetting(string Name, string Label, string? Combo);
