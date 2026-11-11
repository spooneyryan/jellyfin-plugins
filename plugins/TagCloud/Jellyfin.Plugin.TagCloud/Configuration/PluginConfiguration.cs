using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TagCloud.Configuration;

/// <summary>
/// Plugin settings, edited by administrators on the plugin's settings page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The fewest words a cloud may be asked for, and the words slider's step.</summary>
    public const int WordStep = 25;

    /// <summary>The lowest <see cref="MaxWordsLimit"/> may be set to.</summary>
    public const int LowestWordsLimit = 100;

    /// <summary>The highest <see cref="MaxWordsLimit"/> may be set to.</summary>
    public const int HighestWordsLimit = 2000;

    /// <summary>
    /// Gets or sets a value indicating whether the Tag Cloud page is added to the web client. When off, the REST API still
    /// works. Read on every page load, so it applies without a restart.
    /// </summary>
    public bool EnableWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's user page is registered with the Plugin Pages plugin at
    /// startup, in the <c>Auto</c> and <c>PluginPagesOnly</c> page host modes. Changes apply after a restart.
    /// </summary>
    public bool RegisterPluginPages { get; set; } = true;

    /// <summary>
    /// Gets or sets where the Tag Cloud page lives: <c>FallbackOnly</c> (the default: its own route,
    /// linked from the "Library tools" section of the drawer), <c>Auto</c> or <c>PluginPagesOnly</c> (the Plugin Pages
    /// plugin's section when it lists it). Read at startup for the Plugin Pages registration and on every page load for
    /// the link.
    /// </summary>
    public string PageHostMode { get; set; } = "FallbackOnly";

    /// <summary>
    /// Gets or sets a value indicating whether the Tag Cloud page is linked from the "Library tools" section of the drawer.
    /// Read on every page load.
    /// </summary>
    public bool ShowFallbackNav { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server records the reports browsers send when a jellyfin-web element
    /// the Tag Cloud relies on is missing. Browsers warn in their console either way.
    /// </summary>
    public bool ReportWebAnchorProblems { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Tag Cloud page is on. Off hides its nav link, its route says
    /// "Tag Cloud is turned off by your server admin.", and the libraries, words, titles and saved clouds endpoints answer
    /// 403 with the kit's "turned off" type; the capabilities and the admin endpoints still answer.
    /// </summary>
    public bool EnableTagCloudPage { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the cloud of tags is offered and indexed.</summary>
    public bool EnableTags { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the cloud of genres is offered and indexed.</summary>
    public bool EnableGenres { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the cloud of studios is offered and indexed.</summary>
    public bool EnableStudios { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the cloud of actors is offered and indexed.</summary>
    public bool EnableActors { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the cloud of directors is offered and indexed.</summary>
    public bool EnableDirectors { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the cloud of writers is offered and indexed.</summary>
    public bool EnableWriters { get; set; } = true;

    /// <summary>
    /// Gets or sets the most words one cloud may show: the words slider's top, from
    /// <see cref="LowestWordsLimit"/> to <see cref="HighestWordsLimit"/>, a multiple of <see cref="WordStep"/>.
    /// </summary>
    public int MaxWordsLimit { get; set; } = 1000;

    /// <summary>
    /// Gets or sets how many words a cloud starts with on the TV layout, where drawing many words is slow.
    /// </summary>
    public int TvMaxWordsDefault { get; set; } = 150;

    /// <summary>
    /// Gets or sets a value indicating whether users may share a saved cloud with everyone. Off hides Share and
    /// stops shared clouds being listed to anyone but their owner; their records are kept.
    /// </summary>
    public bool AllowSharedClouds { get; set; } = true;

    /// <summary>
    /// Gets or sets how many changed titles in one batch of library changes make the index rebuild in full rather than
    /// patch each title.
    /// </summary>
    public int FullRebuildAfterChanges { get; set; } = 500;

    /// <summary>
    /// Gets or sets how many daily snapshots of the saved clouds (<c>data/tagcloud/clouds.json</c>) the maintenance keeps.
    /// </summary>
    public int SnapshotsToKeep { get; set; } = 7;
}
