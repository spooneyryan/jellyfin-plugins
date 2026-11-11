using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;

namespace Jellyfin.Plugin.Bookmarks.Import;

/// <summary>
/// The Jellyfin Enhanced bookmark import: reads every user's JE
/// <c>bookmark.json</c> (read-only), matches each bookmark to a video of its owner's libraries (JE's item id, else its TMDb
/// or TVDb id), and in a dry run only reports what it would do; applied, it writes the bookmarks (<c>Origin =
/// JellyfinEnhanced</c>, names and positions kept) with the import ledger in one write, so running it again changes
/// nothing. Public only so the admin controller can take it: DTOs and BCL types only.
/// </summary>
public interface IBookmarkJeImport
{
    /// <summary>
    /// Runs the import, or its dry run.
    /// </summary>
    /// <param name="dryRun">Whether to only report.</param>
    /// <param name="request">Which users; every user with JE bookmarks when null or empty.</param>
    /// <param name="appliedBy">The admin running it, recorded with an applied import; <see cref="System.Guid.Empty"/> for an
    /// API key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The report.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for more than 1000 or empty user ids; 404 when no Jellyfin Enhanced
    /// user (of those asked for) has bookmarks; 503 when JE's data folder can't be listed, and for an import (not a dry run)
    /// while the bookmark data is read-only.</exception>
    Task<BookmarkJeImportReportDto> ImportAsync(bool dryRun, BookmarkJeImportRequest? request, System.Guid appliedBy, CancellationToken cancellationToken);
}
