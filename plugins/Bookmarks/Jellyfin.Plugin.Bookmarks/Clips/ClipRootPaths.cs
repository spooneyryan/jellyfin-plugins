using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Storage;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Clip folder paths as the store's <c>ClipRoots</c> keeps them: absolute (<see cref="Path.GetFullPath(string)"/>),
/// without a trailing separator (a drive or share root keeps its own), compared without regard to case on Windows and
/// macOS, and containment checked on whole path segments, so <c>clips2</c> is not under <c>clips</c>.
/// </summary>
internal static class ClipRootPaths
{
    /// <summary>
    /// The message for a <c>ClipOutputPath</c> that isn't a folder path.
    /// </summary>
    public const string InvalidFolderMessage = "The clip folder in the plugin's settings isn't a valid folder path.";

    /// <summary>
    /// Gets how clip paths are compared: the file system's case rules, as <c>ClipPlanner.PathsEqual</c>.
    /// </summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Gets a comparer with the same rules as <see cref="Comparison"/>.
    /// </summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Normalises a folder path: trimmed, made absolute, a trailing separator removed. A relative path resolves against the
    /// server's working directory, as the clip planner's file writes do.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="normalized">The normalised path.</param>
    /// <returns><c>false</c> for a blank path or one <see cref="Path.GetFullPath(string)"/> refuses.</returns>
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return normalized.Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the current clip folder, the one new clips go to: <c>ClipOutputPath</c>, or the default folder in the
    /// plugin's data folder when it is empty, normalised as <see cref="TryNormalize"/> does. The clip planner writes under it,
    /// and Diagnostics <c>ClipStorage</c> describes it.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="paths">The plugin's paths, for the default folder.</param>
    /// <param name="root">The normalised folder.</param>
    /// <returns><c>false</c> when <c>ClipOutputPath</c> isn't a folder path.</returns>
    public static bool TryGetCurrent(PluginConfiguration configuration, BookmarkPaths paths, out string root)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        var configured = string.IsNullOrWhiteSpace(configuration.ClipOutputPath)
            ? paths.DefaultClipsDirectory
            : configuration.ClipOutputPath.Trim();
        return TryNormalize(configured, out root);
    }

    /// <summary>
    /// Whether a normalised path is a normalised folder or lies under it, on whole path segments.
    /// </summary>
    /// <param name="path">The normalised path.</param>
    /// <param name="root">The normalised folder.</param>
    /// <returns>Whether <paramref name="path"/> is inside <paramref name="root"/>.</returns>
    public static bool IsUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        if (root.Length == 0 || !path.StartsWith(root, Comparison))
        {
            return false;
        }

        // A drive or share root keeps its separator ("C:\"), so the next character is already inside it.
        return path.Length == root.Length
            || Path.EndsInDirectorySeparator(root)
            || path[root.Length] == Path.DirectorySeparatorChar
            || path[root.Length] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Whether a normalised path is under any of the folders.
    /// </summary>
    /// <param name="path">The normalised path.</param>
    /// <param name="roots">The normalised folders.</param>
    /// <returns>Whether one of them contains it.</returns>
    public static bool IsUnderAny(string path, IEnumerable<string> roots) => roots.Any(root => IsUnder(path, root));

    /// <summary>
    /// Gets a clip folder's id, its <c>RootId</c>: the first 12 hex characters (lower case) of the SHA-256 of its
    /// normalised path's UTF-8, the path in upper case where paths compare without regard to case (Windows, macOS), so every
    /// spelling of one folder has one id. Its first user is the folder's marker file (<see cref="ClipRootMarker"/>);
    /// the 1.3.0 admin routes name folders by it.
    /// </summary>
    /// <param name="root">The folder, normalised (<see cref="TryNormalize"/>).</param>
    /// <returns>The id.</returns>
    public static string RootId(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var key = Comparison == StringComparison.OrdinalIgnoreCase ? root.ToUpperInvariant() : root;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }
}
