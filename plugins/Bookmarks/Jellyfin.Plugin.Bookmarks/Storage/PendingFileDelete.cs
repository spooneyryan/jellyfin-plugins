using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// An old clip file an automatic deletion (a remake's cleanup, the deleted-user <c>Delete</c> policy, retention) couldn't
/// delete because it was in use: retried at startup and by the daily task, and dropped from the list once it is gone.
/// Every retry goes through <c>ClipFileGuard</c>; entries are never imported.
/// </summary>
internal sealed record PendingFileDelete
{
    /// <summary>
    /// Gets the file's absolute path.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Gets when the first delete failed (UTC).
    /// </summary>
    public DateTime SinceUtc { get; init; }

    /// <summary>
    /// Gets how many times the delete was tried.
    /// </summary>
    public int Attempts { get; init; }

    /// <summary>
    /// Gets fields this version doesn't know, kept on a round trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
