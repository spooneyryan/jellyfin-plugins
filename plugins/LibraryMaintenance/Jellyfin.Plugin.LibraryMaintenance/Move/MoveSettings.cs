using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// The default of "Put it in its own folder" for a loose movie or music video file.
/// </summary>
internal enum LooseFileFolderMode
{
    /// <summary>Wrap only when the chosen folder holds own-folder movies and no loose videos (the default).</summary>
    MatchDestination,

    /// <summary>Never wrap by default.</summary>
    Never,

    /// <summary>Always wrap by default.</summary>
    Always
}

/// <summary>
/// Move's settings as the planner reads them: names parsed, a value it doesn't know replaced by the default, so
/// a file a newer build wrote never breaks a plan.
/// </summary>
internal sealed record MoveSettings
{
    /// <summary>The item kinds 1.1 can move.</summary>
    public static readonly IReadOnlyList<BaseItemKind> SupportedKinds = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo];

    /// <summary>Gets a value indicating whether Move media is on.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets a value indicating whether the item menu has the entry.</summary>
    public bool ShowInItemMenu { get; init; } = true;

    /// <summary>Gets a value indicating whether the details page has the button.</summary>
    public bool ShowDetailPageButton { get; init; }

    /// <summary>Gets a value indicating whether the entry shows in the TV layout.</summary>
    public bool ShowInTvLayout { get; init; }

    /// <summary>Gets the kinds that can be moved: the configured ones that 1.1 supports.</summary>
    public IReadOnlySet<BaseItemKind> AllowedKinds { get; init; } = SupportedKinds.ToHashSet();

    /// <summary>Gets a value indicating whether another library of the same type is a destination.</summary>
    public bool AllowCrossLibraryMoves { get; init; } = true;

    /// <summary>Gets the default of the wrap checkbox.</summary>
    public LooseFileFolderMode LooseFileFolderMode { get; init; } = LooseFileFolderMode.MatchDestination;

    /// <summary>Gets the metadata carry-over mode's name: <c>AllEditableFields</c>, <c>LockedFieldsOnly</c> or <c>None</c>.</summary>
    public string MetadataCarryOver { get; init; } = "AllEditableFields";

    /// <summary>Gets a value indicating whether the date added is kept.</summary>
    public bool RestoreDateAdded { get; init; } = true;

    /// <summary>Gets a value indicating whether internal data is pre-staged.</summary>
    public bool PreStageInternalData { get; init; } = true;

    /// <summary>Gets a value indicating whether collections and playlists follow.</summary>
    public bool RewriteCollectionsAndPlaylists { get; init; } = true;

    /// <summary>Gets a value indicating whether other plugins' relink hooks are called.</summary>
    public bool CallRelinkHooks { get; init; } = true;

    /// <summary>
    /// Reads the settings.
    /// </summary>
    /// <param name="configuration">The plugin's configuration.</param>
    /// <returns>The settings.</returns>
    public static MoveSettings From(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var kinds = new HashSet<BaseItemKind>();
        foreach (var name in configuration.AllowedItemKinds ?? [])
        {
            // By name only: Enum.TryParse would take "5" for whatever kind has that number.
            foreach (var kind in SupportedKinds.Where(k => string.Equals(k.ToString(), name?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                kinds.Add(kind);
            }
        }

        var modeName = configuration.LooseFileFolderMode?.Trim();
        var mode = Enum.GetValues<LooseFileFolderMode>().FirstOrDefault(m => string.Equals(m.ToString(), modeName, StringComparison.OrdinalIgnoreCase));

        return new MoveSettings
        {
            Enabled = configuration.MoveEnabled,
            ShowInItemMenu = configuration.ShowInItemMenu,
            ShowDetailPageButton = configuration.ShowDetailPageButton,
            ShowInTvLayout = configuration.ShowInTvLayout,
            AllowedKinds = kinds,
            AllowCrossLibraryMoves = configuration.AllowCrossLibraryMoves,
            LooseFileFolderMode = mode,
            MetadataCarryOver = configuration.MetadataCarryOver?.Trim() switch
            {
                var m when string.Equals(m, "LockedFieldsOnly", StringComparison.OrdinalIgnoreCase) => "LockedFieldsOnly",
                var m when string.Equals(m, "None", StringComparison.OrdinalIgnoreCase) => "None",
                _ => "AllEditableFields"
            },
            RestoreDateAdded = configuration.RestoreDateAdded,
            PreStageInternalData = configuration.PreStageInternalData,
            RewriteCollectionsAndPlaylists = configuration.RewriteCollectionsAndPlaylists,
            CallRelinkHooks = configuration.CallRelinkHooks
        };
    }
}
