using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The answer of <c>POST /Bookmarks/Admin/Import/JellyfinEnhanced</c>: per Jellyfin Enhanced user
/// what the import found and does (or would do, in a dry run), up to 50 sample rows, and after an applied import the
/// switch-off checklist as the server sees it then. Admins only: it holds users' names and labels.
/// </summary>
public sealed class BookmarkJeImportReportDto
{
    /// <summary>
    /// Gets or sets a value indicating whether nothing was written (a dry run).
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin Enhanced format the import reads: <c>je-bookmark/1</c> (JE 12.8 to 12.12
    /// <c>bookmark.json</c>).
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the import was applied (UTC); <c>null</c> for a dry run.
    /// </summary>
    public DateTime? AppliedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bookmark data is read-only (a newer version's file, or an upgrade that
    /// failed): a dry run still reports, an import is refused with 503.
    /// </summary>
    public bool StoreReadOnly { get; set; }

    /// <summary>
    /// Gets or sets the copy of <c>bookmarks.json</c> taken just before an applied import that changed anything (in the
    /// snapshots folder, for <i>Restore snapshot</i>); <c>null</c> otherwise.
    /// </summary>
    public string? Snapshot { get; set; }

    /// <summary>
    /// Gets or sets one row per Jellyfin Enhanced user with a <c>bookmark.json</c>, in folder order.
    /// </summary>
    public IReadOnlyList<BookmarkJeImportUserDto> Users { get; set; } = [];

    /// <summary>
    /// Gets or sets up to 50 sample rows, the unusual outcomes first (skipped, duplicate, not in library, ambiguous,
    /// updated, moved off the end, matched by TMDb), then the plain ones.
    /// </summary>
    public IReadOnlyList<BookmarkJeImportSampleDto> Samples { get; set; } = [];

    /// <summary>
    /// Gets or sets how many entries the samples leave out.
    /// </summary>
    public int MoreEntries { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every Jellyfin Enhanced user with bookmarks is covered by an applied import
    /// now, so the settings page shows the switch-off checklist (only after a successful import; a dry
    /// run doesn't count).
    /// </summary>
    public bool ChecklistReady { get; set; }

    /// <summary>
    /// Gets or sets the switch-off checklist, after an applied import that covers every user; empty for a
    /// dry run.
    /// </summary>
    public IReadOnlyList<BookmarkJeChecklistItemDto> Checklist { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether every checklist step is done.
    /// </summary>
    public bool ChecklistComplete { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin Enhanced's <c>B</c> is durably free for everyone, so the settings
    /// page offers "Use B for bookmarks".
    /// </summary>
    public bool CanUseB { get; set; }
}
