using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// One user's naming preferences, kept on the server so they follow the user across devices.
/// The per-device shortcut override stays in the browser.
/// </summary>
internal sealed record BookmarkUserPreferences
{
    /// <summary>
    /// Gets whether the player asks for a name as soon as a bookmark is made; <c>null</c> follows the server's default
    /// (<c>AskForNameDefault</c>).
    /// </summary>
    public bool? AskForName { get; init; }

    /// <summary>
    /// Gets a value indicating whether playback pauses while the name dialog is open.
    /// </summary>
    public bool PauseWhileNaming { get; init; }

    /// <summary>
    /// Gets fields this version doesn't know, kept on a round trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
