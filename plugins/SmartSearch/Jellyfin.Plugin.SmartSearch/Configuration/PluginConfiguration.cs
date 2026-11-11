using Jellyfin.Data.Enums;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SmartSearch.Configuration;

/// <summary>
/// Plugin settings, edited by administrators on the plugin's settings page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the Smart Search web client is added to jellyfin-web. When off, the REST API
    /// still works. Read on every page load, so it applies without a restart.
    /// </summary>
    public bool EnableWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Advanced Search page is registered with the Plugin Pages plugin at
    /// startup, in the <c>Auto</c> and <c>PluginPagesOnly</c> page host modes. Changes apply after a restart.
    /// </summary>
    public bool RegisterPluginPages { get; set; } = true;

    /// <summary>
    /// Gets or sets where the Advanced Search page lives: <c>FallbackOnly</c> (the default: its own
    /// route, linked from the "Library tools" section of the drawer), <c>Auto</c> or <c>PluginPagesOnly</c> (the Plugin
    /// Pages plugin's section when it lists it). Read at startup for the Plugin Pages registration and on every page load
    /// for the link.
    /// </summary>
    public string PageHostMode { get; set; } = "FallbackOnly";

    /// <summary>
    /// Gets or sets a value indicating whether the Advanced Search page is linked from the "Library tools" section of the
    /// drawer. Read on every page load.
    /// </summary>
    public bool ShowFallbackNav { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server records the reports browsers send when a jellyfin-web element
    /// Smart Search relies on is missing. Browsers warn in their console either way.
    /// </summary>
    public bool ReportWebAnchorProblems { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Advanced Search page is on. Off hides its nav link, its route
    /// says "Advanced Search is turned off by your server admin." and the Plugin Pages registration is dropped at the next
    /// restart; the search and lookup endpoints answer 403 while it is off.
    /// </summary>
    public bool EnableSearchPage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether jellyfin-web's header gets an "Advanced search" button next to its own
    /// search button, while the page is on. Read on every page load.
    /// </summary>
    public bool ShowHeaderButton { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether users may share their saved queries with every user (each runs a shared
    /// query as themselves). Off, shared queries are their owners' alone again and nothing new can be shared; their
    /// sharing comes back when it is turned on. Read on each request.
    /// </summary>
    public bool AllowQuerySharing { get; set; } = true;

    /// <summary>
    /// Gets or sets the most saved queries one user may own, 1 to 10,000. Read on each save.
    /// </summary>
    public int MaxSavedQueriesPerUser { get; set; } = Services.SavedQuerySettings.DefaultMaxPerUser;

    /// <summary>
    /// Gets or sets what happens to a deleted user's saved queries: <c>Delete</c> (the default: private queries go,
    /// shared ones stay as "Deleted user" until an administrator gives them to someone else or deletes them),
    /// <c>KeepAsOrphan</c> or <c>Anonymize</c> (every query stays that way).
    /// </summary>
    public string DeletedUserPolicy { get; set; } = "Delete";

    /// <summary>
    /// Gets or sets how many daily snapshots of the saved queries are kept in <c>data/smartsearch/snapshots</c>: 7 by
    /// default, 0 takes none.
    /// </summary>
    public int SnapshotsToKeep { get; set; } = 7;

    /// <summary>
    /// Gets or sets the item kinds a search may name: of Movie, Series, Episode, MusicVideo, Video and BoxSet. A query
    /// naming another kind is refused; the rule builder offers only these. Empty, or naming none of the six, means all
    /// six. Read on each request.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The server's XML serializer and the settings page's JSON need a settable array.")]
    public BaseItemKind[] AllowedItemKinds { get; set; } = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video, BaseItemKind.BoxSet];

    /// <summary>
    /// Gets or sets the most items one search may check: the candidate id list of a search whose rules
    /// SQL can't answer alone, plus the items of the libraries its <c>Library</c> rules name. Past it the search is refused
    /// with "too broad" and the count, never cut short. 1,000 to 1,000,000; read on each search.
    /// </summary>
    public int MaxCandidates { get; set; } = Query.Execution.EngineSettings.DefaultMaxCandidates;

    /// <summary>
    /// Gets or sets the longest a search may run, in seconds, before it is stopped and refused (linked with the
    /// request's own cancellation). 1 to 300; read on each search.
    /// </summary>
    public int SearchTimeoutSeconds { get; set; } = Query.Execution.EngineSettings.DefaultSearchTimeoutSeconds;

    /// <summary>
    /// Gets or sets how many searches run at once, server-wide; one more waits up to 10 seconds, then is
    /// refused as busy. The auto-collections have a slot of their own. 1 to 16; changes apply after a restart.
    /// </summary>
    public int MaxConcurrentSearches { get; set; } = Query.Execution.EngineSettings.DefaultMaxConcurrentSearches;

    /// <summary>
    /// Gets or sets how long, in seconds, an unused search result is kept for its next pages: a page asked for
    /// later than that evaluates the search again. 10 to 3,600; read on each search.
    /// </summary>
    public int ResultCacheSeconds { get; set; } = Query.Execution.EngineSettings.DefaultResultCacheSeconds;

    /// <summary>
    /// Gets or sets the most item ids the kept search results hold together (16 bytes each): past it the least recently
    /// used results are dropped. 1,000,000 to 20,000,000; read on each search.
    /// </summary>
    public int MaxCachedResultIds { get; set; } = Query.Execution.EngineSettings.DefaultMaxCachedResultIds;

    /// <summary>
    /// Gets or sets a value indicating whether auto-collections are kept up to date. Off: nothing runs and none can be
    /// created; the definitions and collections stay.
    /// </summary>
    public bool EnableAutoCollections { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an auto-collection's page in jellyfin-web says it updates automatically
    /// (with Edit and Refresh now for users who manage collections). Read on every page load.
    /// </summary>
    public bool ShowCollectionNote { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an auto-collection's collection that has no poster gets the plugin's
    /// default one, once: a poster removed or replaced later is left as it is. Off, none is given and none is removed.
    /// </summary>
    public bool AutoCollectionDefaultImage { get; set; } = true;

    /// <summary>Gets or sets the most members one auto-collection may have. 1 to 20,000.</summary>
    public int MaxMembersPerCollection { get; set; } = AutoCollections.AutoCollectionSettings.DefaultMaxMembersPerCollection;

    /// <summary>Gets or sets the most auto-collections on the server. 1 to 1,000.</summary>
    public int MaxAutoCollections { get; set; } = AutoCollections.AutoCollectionSettings.DefaultMaxAutoCollections;

    /// <summary>
    /// Gets or sets how long, in seconds, library changes must be quiet before the auto-collections they touch run.
    /// 5 to 3,600; changes apply after a restart.
    /// </summary>
    public int EventDebounceSeconds { get; set; } = 60;

    /// <summary>Gets or sets the longest, in minutes, library changes wait before they run. 1 to 1,440; applies after a restart.</summary>
    public int EventMaxWaitMinutes { get; set; } = 10;

    /// <summary>Gets or sets the least time, in seconds, between two runs of one auto-collection that library changes start. 0 to 86,400.</summary>
    public int DefinitionCooldownSeconds { get; set; } = 120;

    /// <summary>Gets or sets how long, in minutes, after the server starts every auto-collection is checked; 0 turns it off.</summary>
    public int StartupPassDelayMinutes { get; set; } = 5;

    /// <summary>Gets or sets a value indicating whether large removals while a library looks unhealthy wait for a review.</summary>
    public bool GuardEnabled { get; set; } = true;

    /// <summary>Gets or sets the share of a collection's members, in percent, whose removal counts as large. 1 to 100.</summary>
    public int GuardMaxRemovalPercent { get; set; } = 50;

    /// <summary>Gets or sets the fewest removals that count as large.</summary>
    public int GuardMinRemovals { get; set; } = 5;

    /// <summary>Gets or sets a value indicating whether emptying a collection always waits for a review, unless it allows it.</summary>
    public bool GuardBlockEmpty { get; set; } = true;

    /// <summary>Gets or sets the most items one pass over the auto-collections keeps loaded to share between them. 0 to 200,000.</summary>
    public int PassCacheMaxItems { get; set; } = 15_000;

    /// <summary>Gets or sets how long, in hours, the daily auto-collection task waits for a running library scan. 0 to 48.</summary>
    public int DailyPassScanWaitHours { get; set; } = 6;

    /// <summary>
    /// Gets or sets a value indicating whether an auto-collection whose search reads watch state (played, favourite...)
    /// updates when its evaluation user's watch state changes; otherwise only the daily update and the other triggers
    /// update it.
    /// </summary>
    public bool UseUserDataTriggers { get; set; } = true;

    /// <summary>
    /// Gets or sets how collections are written: <c>Auto</c> (the direct writer on this server), <c>DirectReplace</c>
    /// (the links replaced in one save, in the query's order, dead links pruned) or <c>ManagerApi</c> (the server's own
    /// add and remove calls: no query order, dead links stay).
    /// </summary>
    public string CollectionWriter { get; set; } = "Auto";
}
