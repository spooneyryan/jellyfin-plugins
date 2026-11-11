using System;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryMaintenance.Configuration;

/// <summary>
/// The plugin's settings (the 1.0 fields of its "Duplicates" section), in Jellyfin's XML file
/// <c>plugins/configurations/Jellyfin.Plugin.LibraryMaintenance.xml</c>.
/// </summary>
/// <remarks>
/// Settings whose values are names (<see cref="DefaultMatchMode"/> and the like) are strings, so a value a newer build wrote
/// never stops the file from loading; each reader takes its default for anything it doesn't know. The kit's common fields
/// arrive with the parts that read them: the ClientScript's two (<see cref="EnableWebUi"/>,
/// <see cref="ReportWebAnchorProblems"/>) came with Move's item-menu entry; there are no user pages or store yet.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the Duplicates tab is shown and its API answers; off, the API
    /// answers 404.
    /// </summary>
    public bool DuplicatesEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the match mode a viewer with no saved preference starts in: <c>TitleAndYear</c> or <c>Title</c>.
    /// </summary>
    public string DefaultMatchMode { get; set; } = "TitleAndYear";

    /// <summary>
    /// Gets or sets how a movie with no year groups in Title + Year mode: <c>MatchUnknownOnly</c> (only with other movies
    /// with no year) or <c>MatchAny</c>.
    /// </summary>
    public string UnknownYearPolicy { get; set; } = "MatchUnknownOnly";

    /// <summary>
    /// Gets or sets the libraries the index reads; empty means every <c>movies</c> and mixed library.
    /// <c>musicvideos</c> and the other types are never read.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public Guid[] IncludedLibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets absolute folders whose movies the index leaves out, compared separator-aware and
    /// case-insensitive; for example a Bookmarks clip folder inside a Movies library.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public string[] ExcludedPathPrefixes { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a title's edition suffix (<see cref="EditionPhrases"/>) is cut before
    /// titles are compared (rule N4).
    /// </summary>
    public bool StripEditionSuffixes { get; set; } = true;

    /// <summary>
    /// Gets or sets the edition phrases rule N4 cuts, matched as a whole segment, case-insensitive.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public string[] EditionPhrases { get; set; } = [.. NormalizationOptions.DefaultEditionPhrases];

    /// <summary>
    /// Gets or sets the languages whose leading articles are dropped before titles are compared (rules N7 and N7b):
    /// <c>En</c> by default; 1.0 knows only <c>En</c>. The setting's text must warn that N7b drops a first word that
    /// is an article even when it belongs to the title ("La La Land" becomes "La Land" with <c>Fr</c> or <c>Es</c>).
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public string[] ArticleLanguages { get; set; } = ["En"];

    /// <summary>
    /// Gets or sets a value indicating whether the index is rebuilt after every library scan, even when nobody opened the
    /// page.
    /// </summary>
    public bool WarmIndexAfterScan { get; set; } = true;

    /// <summary>
    /// Gets or sets how long a request waits for an index build before it is answered from the old index:
    /// 0 to 30 seconds.
    /// </summary>
    public int RequestWaitSeconds { get; set; } = 5;

    // Move media (Library Maintenance 1.1, in development). The Settings tab, the export and the import carry
    // MoveEnabled and ShowInItemMenu since 1.1.0.5; the rest waits for the Moves tab,
    // and SettingsRules.Copy keeps them through every save of the tab.

    /// <summary>
    /// Gets or sets a value indicating whether Move media is on; off, its entries are hidden and every
    /// <c>/LibraryMaintenance/Move</c> route answers 403 with the kit's <c>feature-disabled</c> problem type.
    /// </summary>
    public bool MoveEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the item "⋮" menu has the "Move media to new location" entry.
    /// </summary>
    public bool ShowInItemMenu { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the details page has a Move button (off by default).
    /// </summary>
    public bool ShowDetailPageButton { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Move entry shows in the TV layout (off by default).
    /// </summary>
    public bool ShowInTvLayout { get; set; }

    /// <summary>
    /// Gets or sets the item kinds that can be moved: <c>Movie</c>, <c>Episode</c>, <c>MusicVideo</c>. Other
    /// names are ignored in 1.1 (<c>Video</c> comes in a later version).
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public string[] AllowedItemKinds { get; set; } = ["Movie", "Episode", "MusicVideo"];

    /// <summary>
    /// Gets or sets a value indicating whether an item can move to another library of the same type; off, only
    /// to another folder of its own library.
    /// </summary>
    public bool AllowCrossLibraryMoves { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether moves between drives are allowed once they exist. 1.1
    /// refuses every such move (<c>move.cross-volume-later</c>) whatever this says.
    /// </summary>
    public bool AllowCrossVolumeMoves { get; set; } = true;

    /// <summary>
    /// Gets or sets the free space kept on the destination drive of a copy, in MB.
    /// </summary>
    public int FreeSpaceMarginMb { get; set; } = 2048;

    /// <summary>
    /// Gets or sets how a copy is verified: <c>SizeAndSha256</c> or <c>Size</c>.
    /// </summary>
    public string VerifyCopies { get; set; } = "SizeAndSha256";

    /// <summary>
    /// Gets or sets which metadata follows a moved item: <c>AllEditableFields</c>, <c>LockedFieldsOnly</c> or <c>None</c>.
    /// </summary>
    public string MetadataCarryOver { get; set; } = "AllEditableFields";

    /// <summary>
    /// Gets or sets a value indicating whether the moved item keeps its date added.
    /// </summary>
    public bool RestoreDateAdded { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether images, downloaded subtitles and trickplay are copied ahead to the new item.
    /// </summary>
    public bool PreStageInternalData { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether collections and playlists follow the moved item.
    /// </summary>
    public bool RewriteCollectionsAndPlaylists { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether other plugins' relink hooks are called after a move; the move
    /// journal is written either way.
    /// </summary>
    public bool CallRelinkHooks { get; set; } = true;

    /// <summary>
    /// Gets or sets the default of "Put it in its own folder" for a loose movie or music video file:
    /// <c>MatchDestination</c> (only when the chosen folder holds own-folder movies and no loose videos), <c>Never</c> or
    /// <c>Always</c>.
    /// </summary>
    public string LooseFileFolderMode { get; set; } = "MatchDestination";

    /// <summary>
    /// Gets or sets a value indicating whether source folders a move leaves empty are removed (never the library
    /// folder).
    /// </summary>
    public bool RemoveEmptySourceFolders { get; set; } = true;

    /// <summary>
    /// Gets or sets how long a move waits for a running library scan, in minutes.
    /// </summary>
    public int WaitForScanMinutes { get; set; } = 120;

    /// <summary>
    /// Gets or sets how long a move waits for Jellyfin to show the moved item, in hours.
    /// </summary>
    public int AwaitLibraryHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets how often a copy's source delete is tried, 10 s apart.
    /// </summary>
    public int SourceDeleteRetries { get; set; } = 5;

    /// <summary>
    /// Gets or sets the most move jobs waiting in the queue.
    /// </summary>
    public int MaxQueuedJobs { get; set; } = 20;

    /// <summary>
    /// Gets or sets how long a finished move job is kept, in days.
    /// </summary>
    public int JobHistoryDays { get; set; } = 90;

    // The ClientScript's two common fields of the kit: the script carries Move's item-menu entry and its dialog. Both are
    // on the Settings tab, in the export and in the import since 1.1.0.5. Disabling the plugin in Jellyfin's Plugins page
    // is not a way to turn them off on 10.11.5: Jellyfin's DiscoverPlugins then deletes the plugin's folder at the next
    // start (an upstream bug, fixed only on its master in a272efb9a2); this tab is.

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's script is added to the web client's <c>index.html</c>:
    /// Move's entry in the item "⋮" menu and its dialog. When off, the REST API and the Dashboard page still work.
    /// Read on every page load, so it applies without a restart.
    /// </summary>
    public bool EnableWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server records the reports browsers send when a jellyfin-web element the
    /// script relies on is missing: one warning in the server log per element and web version, and
    /// the list in the Diagnostics tab. Browsers warn in their console either way.
    /// </summary>
    public bool ReportWebAnchorProblems { get; set; } = true;
}
