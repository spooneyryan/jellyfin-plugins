using System;
using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Interop;

namespace Jellyfin.Plugin.Bookmarks.Import;

/// <summary>
/// One Jellyfin Enhanced user's <c>bookmark.json</c> as the import read it, with the Jellyfin user its folder names.
/// </summary>
/// <param name="JeUserId">The user id the folder is named after.</param>
/// <param name="Owner">The Jellyfin user with that id, or <c>null</c> when this server has none (every entry is skipped).</param>
/// <param name="File">What the reader found.</param>
internal sealed record JeImportSource(Guid JeUserId, User? Owner, JeBookmarkFile File)
{
    /// <summary>Gets the user id in the N format, as JE's folder and the ledger keys spell it.</summary>
    public string JeUserKey => JeUserId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Gets the key of this source's applied-import entry, <c>je:&lt;userIdN&gt;</c>.</summary>
    public string RunKey => RunKeyOf(JeUserId);

    /// <summary>
    /// Gets the key of a source's applied-import entry.
    /// </summary>
    /// <param name="jeUserId">The JE user id.</param>
    /// <returns><c>je:&lt;userIdN&gt;</c>.</returns>
    public static string RunKeyOf(Guid jeUserId) => "je:" + jeUserId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the ledger key of one of the source's entries, <c>je:&lt;userIdN&gt;:&lt;JE key&gt;</c>.
    /// </summary>
    /// <param name="entryKey">The entry's key in JE's file.</param>
    /// <returns>The key.</returns>
    public string LedgerKey(string entryKey) => RunKey + ":" + entryKey;
}
