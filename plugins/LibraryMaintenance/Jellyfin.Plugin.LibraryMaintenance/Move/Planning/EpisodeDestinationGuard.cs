using System;
using System.IO;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// Where an episode may go: into a series (walking up from the destination
/// reaches a <see cref="Series"/> item before the location), or into a new folder made directly in a TV location, which
/// Jellyfin's <c>SeriesResolver</c> turns into a new series. Anything else is <c>move.episode-needs-series</c>. Warns when
/// the episode changes series, when its season will follow the folder (no season in its file name) and differs, and when
/// its file name's season differs from the folder's.
/// </summary>
internal sealed class EpisodeDestinationGuard
{
    private readonly ILibraryManager _library;
    private readonly EpisodeResolver _episodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeDestinationGuard"/> class.
    /// </summary>
    /// <param name="library">The library manager (<c>FindByPath</c>).</param>
    /// <param name="naming">Jellyfin's naming options.</param>
    public EpisodeDestinationGuard(ILibraryManager library, NamingOptions naming)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(naming);
        _library = library;
        _episodes = new EpisodeResolver(naming);
    }

    /// <summary>
    /// Checks an episode's destination, adding the warnings and blockers to the unit's lists.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="destination">The destination the client chose.</param>
    /// <param name="targetDirectory">The folder the episode lands in.</param>
    /// <param name="unit">The unit, whose warnings and blockers this adds to.</param>
    public void Check(Episode episode, ResolvedDestination destination, string targetDirectory, MoveUnit unit)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(unit);

        var location = destination.LocationPath;
        Series? series = null;
        for (var dir = destination.ExistingDirectory; PathRules.IsUnder(dir, location); dir = Path.GetDirectoryName(dir)!)
        {
            if (KitItemQueries.FindAnyByPath(_library, dir, true) is Series found)
            {
                series = found;
                break;
            }
        }

        string seriesPath;
        if (series is not null)
        {
            seriesPath = series.Path;
            if (series.Id != episode.SeriesId)
            {
                unit.Warnings.Add(new PlanIssue(
                    MoveErrorCodes.EpisodeOtherSeries,
                    "This episode moves to a different series (" + Name(episode.SeriesName) + " → " + Name(series.Name) + "); its metadata will follow the new series."));
            }
        }
        else if (destination.NewFolderName is not null && PathRules.SamePath(destination.ExistingDirectory, location))
        {
            seriesPath = destination.Directory;
            var grouping = destination.Library.Options?.EnableAutomaticSeriesGrouping == true;
            unit.Warnings.Add(new PlanIssue(
                MoveErrorCodes.NewSeries,
                grouping
                    ? "\"" + destination.NewFolderName + "\" will be a new series folder; automatic series grouping is on in " + destination.Library.Name + ", so it joins a series of the same name."
                    : "Automatic series grouping is off in " + destination.Library.Name + ", so \"" + destination.NewFolderName + "\" will show as a separate series."));
        }
        else
        {
            unit.Blockers.Add(new PlanIssue(
                MoveErrorCodes.EpisodeNeedsSeries,
                "An episode needs a series: choose a series or season folder, or a new series folder directly in " + location + "."));
            return;
        }

        var folderSeason = FolderSeason(seriesPath, targetDirectory);
        var fileSeason = _episodes.Resolve(episode.Path, false, null, null, null, false)?.SeasonNumber;
        if (fileSeason is null)
        {
            if (folderSeason != episode.ParentIndexNumber)
            {
                unit.Warnings.Add(new PlanIssue(
                    MoveErrorCodes.EpisodeOtherSeason,
                    "This episode moves to a different season; its file name has no season number, so season and episode numbers will follow the new folder."));
            }
        }
        else if (folderSeason is not null && folderSeason != fileSeason)
        {
            unit.Warnings.Add(new PlanIssue(
                MoveErrorCodes.EpisodeSeasonFromName,
                "The file name says season " + fileSeason + ", and the season comes from the file name: it stays in season " + fileSeason + " although the folder is season " + folderSeason + "."));
        }
    }

    // The season of the folder an episode lands in: the Season item of the first folder below the series, or the number
    // Jellyfin reads from that folder's name; null in the series folder itself.
    private int? FolderSeason(string seriesPath, string targetDirectory)
    {
        if (!PathRules.IsUnder(targetDirectory, seriesPath))
        {
            return null;
        }

        var first = Path.GetRelativePath(seriesPath, targetDirectory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var seasonPath = Path.Join(seriesPath, first);
        if (KitItemQueries.FindAnyByPath(_library, seasonPath, true) is Season season)
        {
            return season.IndexNumber;
        }

        var parsed = SeasonPathParser.Parse(seasonPath, seriesPath, true, true);
        return parsed.Success ? parsed.SeasonNumber : null;
    }

    private static string Name(string? name) => string.IsNullOrWhiteSpace(name) ? "another series" : name;
}
