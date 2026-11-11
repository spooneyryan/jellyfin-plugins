using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The settings on the Settings tab (and since 1.1.0.5 the web client's and Move's switches):
/// what <c>GET /Admin/Settings</c> answers, what <c>POST /Admin/Settings</c> takes, and an export's <c>Settings</c>. On a POST
/// a field left out (null) keeps its saved value.
/// </summary>
public sealed class LibraryMaintenanceSettingsDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the Duplicates tab is shown and its API answers.
    /// </summary>
    public bool? DuplicatesEnabled { get; set; }

    /// <summary>
    /// Gets or sets the match mode a viewer with no saved choice starts in: <c>TitleAndYear</c> or <c>Title</c>.
    /// </summary>
    public string? DefaultMatchMode { get; set; }

    /// <summary>
    /// Gets or sets how a movie with no year groups in Title + Year: <c>MatchUnknownOnly</c> or <c>MatchAny</c>.
    /// </summary>
    public string? UnknownYearPolicy { get; set; }

    /// <summary>
    /// Gets or sets the libraries the index reads; empty is every <c>movies</c> and mixed library, ones added later too.
    /// </summary>
    public IReadOnlyList<Guid>? IncludedLibraryIds { get; set; }

    /// <summary>
    /// Gets or sets the full folder paths whose movies the index leaves out (a Bookmarks clip folder inside a movie
    /// library, for example).
    /// </summary>
    public IReadOnlyList<string>? ExcludedPathPrefixes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an edition suffix (<see cref="EditionPhrases"/>) is cut before titles are
    /// compared.
    /// </summary>
    public bool? StripEditionSuffixes { get; set; }

    /// <summary>
    /// Gets or sets the edition phrases that are cut.
    /// </summary>
    public IReadOnlyList<string>? EditionPhrases { get; set; }

    /// <summary>
    /// Gets or sets the languages whose leading articles are dropped: <c>En</c>, <c>Fr</c>, <c>De</c>, <c>Es</c>,
    /// <c>It</c>, <c>Nl</c>.
    /// </summary>
    public IReadOnlyList<string>? ArticleLanguages { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the index is built after every library scan, even when nobody opened the
    /// page.
    /// </summary>
    public bool? WarmIndexAfterScan { get; set; }

    /// <summary>
    /// Gets or sets how long a request waits for an index build: 0 to 30 seconds.
    /// </summary>
    public int? RequestWaitSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's script is added to the web client's <c>index.html</c>: Move's
    /// entry in the item menu and its dialog. Off, the Dashboard page and the API still work.
    /// </summary>
    public bool? EnableWebUi { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Move media is on; off, its entry is hidden and its API answers 403.
    /// </summary>
    public bool? MoveEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an administrator's item menu has Move's entry.
    /// </summary>
    public bool? ShowInItemMenu { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server records the browsers' reports of a missing web element.
    /// </summary>
    public bool? ReportWebAnchorProblems { get; set; }
}
