using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;

namespace Jellyfin.Plugin.Bookmarks.Import;

/// <summary>
/// What a Jellyfin Enhanced import does (<see cref="JeImportPlanner.Plan"/>): the report's rows and samples, and the
/// records, ledger entries and applied-import entries it writes.
/// </summary>
/// <param name="Users">One row per file.</param>
/// <param name="Samples">Up to 50 sample rows.</param>
/// <param name="MoreEntries">How many entries the samples leave out.</param>
/// <param name="Added">The new bookmarks.</param>
/// <param name="Replaced">The bookmarks rewritten from changed JE entries.</param>
/// <param name="Ledger">The ledger entries to set, by key.</param>
/// <param name="Runs">The applied-import entries to set, by source.</param>
/// <param name="Owners">The owners of the files, id to name, for <c>KnownUsers</c>.</param>
internal sealed record JeImportPlan(
    IReadOnlyList<BookmarkJeImportUserDto> Users,
    IReadOnlyList<BookmarkJeImportSampleDto> Samples,
    int MoreEntries,
    IReadOnlyList<Bookmark> Added,
    IReadOnlyList<Bookmark> Replaced,
    IReadOnlyDictionary<string, BookmarkImportLedgerEntry> Ledger,
    IReadOnlyDictionary<string, BookmarkImportRun> Runs,
    IReadOnlyDictionary<Guid, string> Owners)
{
    /// <summary>Gets a value indicating whether applying the plan changes the document.</summary>
    public bool Writes => Added.Count + Replaced.Count + Ledger.Count + Runs.Count > 0;

    /// <summary>
    /// Applies the plan to a document: the records added or replaced, the ledger and applied-import entries set, the owners'
    /// names in <c>KnownUsers</c> (refreshed on writes).
    /// </summary>
    /// <param name="document">The document the plan was made over.</param>
    /// <returns>The new document, or <paramref name="document"/> when nothing changes.</returns>
    public BookmarkStoreDocument ApplyTo(BookmarkStoreDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Writes)
        {
            return document;
        }

        var byId = new Dictionary<Guid, Bookmark>();
        foreach (var bookmark in document.Bookmarks)
        {
            if (bookmark is not null)
            {
                byId[bookmark.Id] = bookmark;
            }
        }

        foreach (var bookmark in Added.Concat(Replaced))
        {
            byId[bookmark.Id] = bookmark;
        }

        var known = document.KnownUsers;
        foreach (var (id, name) in Owners)
        {
            if (Added.Concat(Replaced).Any(b => b.UserId.Equals(id)))
            {
                known = known.With(id, name);
            }
        }

        return BookmarkStore.WithBookmarks(document, byId.Values) with
        {
            ImportLedger = document.ImportLedger.ToImmutableSortedDictionary(StringComparer.Ordinal).SetItems(Ledger),
            ImportRuns = document.ImportRuns.ToImmutableSortedDictionary(StringComparer.Ordinal).SetItems(Runs),
            KnownUsers = known
        };
    }
}
