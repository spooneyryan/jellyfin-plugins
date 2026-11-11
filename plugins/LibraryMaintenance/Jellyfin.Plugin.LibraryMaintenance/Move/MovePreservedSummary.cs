using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// The plan's "Kept" summary (<c>Preserved</c>): how many users have watch state, favourites and resume points
/// on the unit's items, the date added, the collections and playlists that hold them, the metadata carry-over mode, the
/// locked fields and the images kept in Jellyfin's metadata folder. It is a preview read now; the job's
/// <c>StateSnapshotter</c> takes the definitive snapshot when it starts, through the kit's <c>FreshUserData</c>, and
/// counts the downloaded subtitles and trickplay resolutions, which stay null here.
/// </summary>
internal sealed class MovePreservedSummary
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly MoveFeature _feature;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovePreservedSummary"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager (every user, through the kit's <c>KitUsers</c>).</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="feature">The settings (<c>MetadataCarryOver</c>).</param>
    public MovePreservedSummary(ILibraryManager library, IUserManager users, IUserDataManager userData, MoveFeature feature)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(userData);
        ArgumentNullException.ThrowIfNull(feature);
        _library = library;
        _users = users;
        _userData = userData;
        _feature = feature;
    }

    /// <summary>
    /// Summarizes what a unit's move keeps.
    /// </summary>
    /// <param name="unit">The planned unit.</param>
    /// <returns>The summary.</returns>
    public LibraryMaintenanceMovePreservedDto Summarize(MoveUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var primary = unit.Primary;
        var items = unit.Items
            .Select(i => i.OldId == primary.Id ? primary : _library.GetItemById(i.OldId))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

        int users = 0, favorites = 0, resume = 0;
        foreach (var user in KitUsers.GetAll(_users))
        {
            var data = items.Select(i => _userData.GetUserData(user, i)).Where(d => d is not null).Select(d => d!).ToList();
            users += data.Any(HasState) ? 1 : 0;
            favorites += data.Any(d => d.IsFavorite) ? 1 : 0;
            resume += data.Any(d => d.PlaybackPositionTicks > 0) ? 1 : 0;
        }

        var ids = unit.Items.Select(i => i.OldId).ToHashSet();
        var paths = unit.Items.Select(i => i.OldPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new LibraryMaintenanceMovePreservedDto
        {
            UserDataUsers = users,
            Favorites = favorites,
            ResumePoints = resume,
            DateCreated = primary.DateCreated,
            Collections = Containing(BaseItemKind.BoxSet, ids, paths),
            Playlists = Containing(BaseItemKind.Playlist, ids, paths),
            CarryOverMode = _feature.Settings.MetadataCarryOver,
            LockedFields = (primary.LockedFields ?? []).Select(f => f.ToString()).Order(StringComparer.Ordinal).ToList(),
            InternalImages = (primary.ImageInfos ?? []).Count(i => i is not null && i.IsLocalFile && !InUnit(unit, i.Path)),
        };
    }

    private static bool HasState(UserItemData data)
        => data.Played || data.PlayCount > 0 || data.IsFavorite || data.Likes is not null || data.Rating is not null || data.PlaybackPositionTicks > 0 || data.LastPlayedDate is not null;

    // An image file that moves with the unit (next to the media) isn't one of Jellyfin's own.
    private static bool InUnit(MoveUnit unit, string path)
        => unit.Kind == MoveUnitKind.Folder
            ? PathRules.IsSameOrUnder(path, unit.SourceRoot)
            : unit.Entries.Any(e => PathRules.SamePath(System.IO.Path.Join(unit.SourceRoot, e.RelativePath), path));

    // The collections or playlists whose linked children include one of the unit's items, by id or by path.
    private List<string> Containing(BaseItemKind kind, HashSet<Guid> ids, HashSet<string> paths)
        => (_library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [kind], Recursive = true }) ?? [])
            .OfType<Folder>()
            .Where(f => (f.LinkedChildren ?? []).Any(c => c is not null && ((c.ItemId is { } id && ids.Contains(id)) || (c.Path is { } path && paths.Contains(path)))))
            .Select(f => f.Name ?? string.Empty)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
