using System;
using System.IO;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The marker file of a clip folder: <c>.jfbm-clip-root</c> in a recorded clip folder (<c>ClipRoots</c>), naming the
/// folder's <see cref="ClipRootPaths.RootId"/> and a random token of its own that the store records for the folder
/// (<c>BookmarkStoreDocument.ClipRootTokens</c>). It is the proof that the folder at a recorded clip folder's path is
/// the one its clips were made in, and not an empty mount point, a share whose disk isn't mounted, another drive that
/// got the same letter, a folder made again while the real one is away, or an older drive or a backup copy of the
/// folder back under the same path (its marker names an older token, or none), all of which hold none of the newer
/// clips: the missing-clip sweep marks no clip missing in a folder without it, and its retention forgets or archives
/// none there (<see cref="MissingClipSweep"/>), and the clip worker never writes into such a folder while clips are
/// recorded in it.
/// </summary>
/// <remarks>
/// <para><b>Written</b> by the clip worker into the clip folder a job writes under, when no clip recorded in Bookmarks lies in
/// it (a new folder, created then) or its clips prove it (one of them there and none gone); by the sweep the first time it
/// finds a folder's clips there and none gone (the backfill, for the clip folders of Bookmarks 1.1, which wrote none); and
/// with an admin's confirmation that a folder left out is the clip folder whose clips are gone
/// (<see cref="ClipFileOps.ConfirmFolderAsync"/>), as for the default clip folder made again after it was deleted. Each
/// writing gets a new token, recorded with it (<see cref="ClipFileOps.WriteRootMarkerAsync"/>). Never deleted or moved by
/// Bookmarks, and never followed through a link.</para>
/// <para>A small UTF-8 text file: a sentence for people, then <c>RootId=&lt;id&gt;</c> and <c>Token=&lt;32 hex digits&gt;</c>.
/// Anything else at that path (another folder's id, a file of more than <see cref="MaxBytes"/> bytes, a folder, a link)
/// doesn't name the folder. A marker without a token (written by an earlier build) or one whose token the store doesn't
/// have is adopted once, while no token is recorded for the folder (<see cref="ClipRootState.Adoptable"/>).</para>
/// <para>An exact copy of a folder, its marker included, can't be told from the folder: a backup copied back after the
/// folder's marker was last written counts as the folder, and its missing newer clips as gone. A copy made before that is
/// recognised as another folder.</para>
/// </remarks>
internal static class ClipRootMarker
{
    /// <summary>
    /// The marker file's name, in the clip folder itself (not in the per-user folders).
    /// </summary>
    public const string FileName = ".jfbm-clip-root";

    /// <summary>
    /// The most a marker file holds; a larger file at its path isn't one.
    /// </summary>
    public const int MaxBytes = 4096;

    private const string IdKey = "RootId=";

    private const string TokenKey = "Token=";

    private const string Explanation = "Jellyfin Bookmarks plugin: this folder holds clip files. Keep this file here: it tells Bookmarks that this is the folder its clips were made in, so an unplugged drive, or a disk or share that isn't mounted, is never taken for clip files that are gone.";

    /// <summary>
    /// Gets the marker file's text for a clip folder.
    /// </summary>
    /// <param name="root">The folder, normalised (<see cref="ClipRootPaths.TryNormalize"/>).</param>
    /// <param name="token">Its token (<see cref="NewToken"/>), or <c>null</c> for a marker as an earlier build wrote it.</param>
    /// <returns>The text.</returns>
    public static string Text(string root, string? token = null)
        => Explanation + "\n" + IdKey + ClipRootPaths.RootId(root) + "\n" + (token is null ? string.Empty : TokenKey + token + "\n");

    /// <summary>
    /// Gets a new random token for a marker file: 32 lower-case hex digits.
    /// </summary>
    /// <returns>The token.</returns>
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Whether a marker file's text names a clip folder: one of its lines is <c>RootId=</c> and the folder's id.
    /// </summary>
    /// <param name="text">The file's text, or <c>null</c> when there is none.</param>
    /// <param name="root">The folder, normalised.</param>
    /// <returns>Whether it names the folder.</returns>
    public static bool Names(string? text, string root) => Read(text, root).NamesRoot;

    /// <summary>
    /// Reads a marker file's text: whether it names a clip folder, and then its token (<c>Token=</c>), if it has one.
    /// </summary>
    /// <param name="text">The file's text, or <c>null</c> when there is none.</param>
    /// <param name="root">The folder, normalised.</param>
    /// <returns>Whether it names the folder, and its token (lower case) or <c>null</c>.</returns>
    public static (bool NamesRoot, string? Token) Read(string? text, string root)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (false, null);
        }

        var id = ClipRootPaths.RootId(root);
        var names = false;
        string? token = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(IdKey, StringComparison.Ordinal) && trimmed[IdKey.Length..].Trim().Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                names = true;
            }
            else if (trimmed.StartsWith(TokenKey, StringComparison.Ordinal) && trimmed[TokenKey.Length..].Trim() is { Length: > 0 } value)
            {
                token = value.ToLowerInvariant();
            }
        }

        return (names, names ? token : null);
    }

    /// <summary>
    /// Judges a clip folder that is there by its marker file's text and the token the store records for it:
    /// <see cref="ClipRootState.Marked"/> when the marker names the folder and the recorded token;
    /// <see cref="ClipRootState.Adoptable"/> when it names the folder and no token is recorded (a marker of an earlier
    /// build, without a token, or one whose token the store lost); <see cref="ClipRootState.Unmarked"/> otherwise, also
    /// for a marker with another token or none while one is recorded (an older drive or a backup copy back under the
    /// same path).
    /// </summary>
    /// <param name="text">The marker file's text, or <c>null</c> when there is none.</param>
    /// <param name="root">The folder, normalised.</param>
    /// <param name="recordedToken">The token the store records for the folder, or <c>null</c>.</param>
    /// <returns>What the folder is, as far as its marker says.</returns>
    public static ClipRootState Judge(string? text, string root, string? recordedToken)
    {
        var (names, token) = Read(text, root);
        if (!names)
        {
            return ClipRootState.Unmarked;
        }

        if (string.IsNullOrEmpty(recordedToken))
        {
            return ClipRootState.Adoptable;
        }

        return token is not null && token.Equals(recordedToken, StringComparison.OrdinalIgnoreCase) ? ClipRootState.Marked : ClipRootState.Unmarked;
    }

    /// <summary>
    /// Gets the marker file's path in a clip folder.
    /// </summary>
    /// <param name="root">The folder.</param>
    /// <returns>The path.</returns>
    public static string PathIn(string root) => Path.Combine(root, FileName);
}
