using System;
using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Log helpers for clip files. The files themselves are deleted and moved only by <see cref="ClipFileOps"/>.
/// </summary>
/// <remarks>
/// A clip file's name holds its bookmark's name, or note (<see cref="ClipFileNaming.BuildFileName"/>), and names and notes
/// are never logged at Information or above (the kit's privacy rule): a line at those levels names a clip file only as
/// <see cref="LogPlaceholder"/> (<see cref="RedactForLog"/>), and the path goes to a Debug line of its own.
/// </remarks>
internal static class ClipFiles
{
    /// <summary>
    /// What a clip file's path or name becomes in a log line at Information or above.
    /// </summary>
    public const string LogPlaceholder = "<clip file>";

    /// <summary>
    /// Replaces clip files' paths and names in a message (an exception's, or ffmpeg's error output) with
    /// <see cref="LogPlaceholder"/>, for a log line at Information or above. Case is ignored, as on Windows. The paths are
    /// replaced in the order given, each whole path before its file name, so a file whose path starts with an earlier one
    /// (the temporary <c>&lt;output&gt;.partial</c> after the output) reads <c>&lt;clip file&gt;.partial</c>.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="paths">The clip files; <c>null</c> or empty entries are skipped.</param>
    /// <returns>The message without the clip files' paths and names.</returns>
    public static string RedactForLog(string message, params string?[] paths)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            message = message.Replace(path, LogPlaceholder, StringComparison.OrdinalIgnoreCase);
            var fileName = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(fileName))
            {
                message = message.Replace(fileName, LogPlaceholder, StringComparison.OrdinalIgnoreCase);
            }
        }

        return message;
    }
}
