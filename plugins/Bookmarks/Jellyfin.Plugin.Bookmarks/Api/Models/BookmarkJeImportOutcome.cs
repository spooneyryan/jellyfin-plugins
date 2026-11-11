namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// What the Jellyfin Enhanced import does, or would do in a dry run, with one of JE's bookmarks,
/// for the samples of <see cref="BookmarkJeImportReportDto"/>.
/// </summary>
public enum BookmarkJeImportOutcome
{
    /// <summary>
    /// Imported on the video JE's item id names, which the bookmark's owner can open.
    /// </summary>
    MatchedById,

    /// <summary>
    /// Imported on the one video of the owner's libraries that has JE's TMDb (or TVDb) id: JE's item id is gone, for example
    /// after the video was replaced by another file. For an episode, the series' id with the season and episode numbers.
    /// </summary>
    MatchedByProviderId,

    /// <summary>
    /// Several of the owner's videos have JE's TMDb id (two copies or two cuts of a film): imported on the first of them by
    /// library order and reported. Check its position.
    /// </summary>
    Ambiguous,

    /// <summary>
    /// No video matches: imported as "Not in library" with what JE knew of it (its title and TMDb id), so it can attach
    /// later when the video comes back.
    /// </summary>
    Detached,

    /// <summary>
    /// Imported before, then changed in Jellyfin Enhanced, and not changed here since: the bookmark is rewritten from JE's
    /// entry.
    /// </summary>
    Updated,

    /// <summary>
    /// Left out: the owner already has a bookmark with the same name within a second of it on the same video.
    /// </summary>
    Duplicate,

    /// <summary>
    /// Left out: an earlier import covered it already (the import ledger), and JE's copy didn't change since, or the
    /// bookmark was changed or deleted here since (a local edit wins).
    /// </summary>
    AlreadyImported,

    /// <summary>
    /// Left out: malformed, a cross-version copy JE made (<c>SyncedFrom</c>), or its user isn't a user of this server.
    /// </summary>
    Skipped
}
