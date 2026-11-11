using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// A user's Jellyfin Enhanced <c>bookmark.json</c>, read-only.
/// </summary>
/// <param name="UserId">The user, from the folder's name.</param>
/// <param name="Format">What the file is: <see cref="JeDataFormat.Bookmarks1"/>, or why it can't be imported.</param>
/// <param name="Entries">The bookmarks, in file order.</param>
/// <param name="Invalid">The entries left out, and why.</param>
/// <param name="Problem">Why the file couldn't be used, or null.</param>
internal sealed record JeBookmarkFile(Guid UserId, JeDataFormat Format, IReadOnlyList<JeBookmark> Entries, IReadOnlyList<JeDataProblem> Invalid, string? Problem)
{
    /// <summary>The format name import reports give <see cref="JeDataFormat.Bookmarks1"/>.</summary>
    public const string FormatName = "je-bookmark/1";
}
