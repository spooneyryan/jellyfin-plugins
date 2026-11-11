using System;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// Item-level checks (<c>MoveEligibility</c>), for the Eligibility endpoint and the planner. The
/// item is resolved for the caller (<c>GetItemById&lt;BaseItem&gt;(id, user)</c> through the kit's
/// <see cref="LibraryAccess"/>), so an item in a library the caller can't open is the same as none. A version or
/// part owned by another item is retargeted to its owner; an extra isn't (it moves with its movie). An item whose library
/// folder, or any folder from it down to the unit's, is a junction or symbolic link is <c>move.reparse-point</c>
/// (<see cref="LinkGuard"/>).
/// </summary>
/// <remarks>
/// <c>CanDelete()</c> is checked by its parts (<c>Video.cs:347-360</c>, <c>BaseItem.CanDelete</c>): a library item (not a
/// channel's), a local file path (<see cref="PathRules.Classify"/>, no media source manager needed), not being recorded
/// (<c>Video.RecordingsManager</c>).
/// </remarks>
internal sealed class MoveEligibility
{
    /// <summary>The code of an item the caller can't see; the controllers answer 404 for it.</summary>
    public const string NotFoundCode = "move.not-found";

    // One answer for an item that isn't there and one the caller can't open, its owner included: a version whose movie is
    // in a library the caller can't open must not read differently from no item at all.
    private const string NotFoundReason = "The item isn't there, or you can't open its library.";

    private readonly LibraryAccess _access;
    private readonly LibraryLocationCatalog _catalog;
    private readonly LibraryOperationGate _gate;
    private readonly MoveFeature _feature;
    private readonly Func<string, bool>? _isMappedNetworkDrive;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoveEligibility"/> class.
    /// </summary>
    /// <param name="access">Which items and libraries the caller can open.</param>
    /// <param name="catalog">The libraries' locations.</param>
    /// <param name="gate">The operation gate (the busy set).</param>
    /// <param name="feature">The settings (<c>AllowedItemKinds</c>).</param>
    /// <param name="isMappedNetworkDrive">Whether a drive root is mapped to a share; by default Windows is asked.</param>
    public MoveEligibility(LibraryAccess access, LibraryLocationCatalog catalog, LibraryOperationGate gate, MoveFeature feature, Func<string, bool>? isMappedNetworkDrive = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(feature);
        _access = access;
        _catalog = catalog;
        _gate = gate;
        _feature = feature;
        _isMappedNetworkDrive = isMappedNetworkDrive;
    }

    /// <summary>
    /// Gets the unit kind Jellyfin's own delete unit gives an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Folder or FileSet.</returns>
    public static MoveUnitKind KindOf(Video item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.VideoType is VideoType.Dvd or VideoType.BluRay || (item is not Episode && !item.IsInMixedFolder) ? MoveUnitKind.Folder : MoveUnitKind.FileSet;
    }

    /// <summary>
    /// Checks an item.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The result.</returns>
    public EligibilityResult Check(Guid id, User? user)
    {
        var asked = _access.GetItem<BaseItem>(id, user);
        if (asked is null)
        {
            return new EligibilityResult(id, NotFoundCode, NotFoundReason);
        }

        if (asked.ExtraType is not null)
        {
            return Not(id, "This is an extra; extras move with their movie.");
        }

        var item = asked;
        Guid? owner = null;
        if (asked.OwnerId != Guid.Empty)
        {
            item = _access.GetItem<BaseItem>(asked.OwnerId, user);
            if (item is null)
            {
                return new EligibilityResult(id, NotFoundCode, NotFoundReason);
            }

            owner = item.Id;
        }

        var kind = item.GetBaseItemKind();
        if (item is not Video video || !MoveSettings.SupportedKinds.Contains(kind))
        {
            return Not(id, "Move works with movies, episodes and music videos.", owner);
        }

        if (!_feature.Settings.AllowedKinds.Contains(kind))
        {
            return Not(id, "Moving " + KindName(kind) + " is turned off in Library Maintenance's settings.", owner);
        }

        // Before SourceType, which reads LiveTV for a file being recorded (Video.cs:168-179), so the reason says why.
        if (!string.IsNullOrEmpty(item.Path) && Video.RecordingsManager?.GetActiveRecordingInfo(item.Path) is not null)
        {
            return Not(id, "This item is being recorded.", owner);
        }

        if (item.SourceType != SourceType.Library)
        {
            return Not(id, "This isn't a library item (a channel's or Live TV's).", owner);
        }

        if (item.IsVirtualItem || video.IsPlaceHolder)
        {
            return Not(id, "This item has no file (a missing episode or a placeholder).", owner);
        }

        if (item.IsShortcut)
        {
            return Not(id, "This item is a shortcut (.strm); only real files move.", owner);
        }

        if (string.IsNullOrEmpty(item.Path) || PathRules.Classify(item.Path, _isMappedNetworkDrive) != LocationKind.Local)
        {
            return Not(id, "This item isn't on a local drive.", owner);
        }

        if (_catalog.FindSource(item, user) is not { } source)
        {
            return Not(id, "This item isn't inside a library folder.", owner);
        }

        if (source.Library.CollectionType is not { } type || !LibraryLocationCatalog.MoveTypes.Contains(type))
        {
            return Not(id, "Move works in Movies, Shows and Music Videos libraries.", owner);
        }

        var ids = new[] { item.Id }.Concat(MovePreflight.VersionPartners(video));
        if (_gate.FindBusy(ids) is { } busy)
        {
            return new EligibilityResult(id, MoveErrorCodes.ItemBusy, LibraryOperationGate.BusyMessage(busy.Owner), OwnerItemId: owner);
        }

        // The only check that reads the disk: the location and every folder down to the unit's, for links.
        // An item reached through one can't move, so Source, Destinations, Folders and Plan refuse it.
        if (LinkGuard.Find(source.Library.Locations[source.LocationIndex], UnitRootOf(video)) is { } link)
        {
            return new EligibilityResult(id, MoveErrorCodes.ReparsePoint, link.Message, OwnerItemId: owner);
        }

        return new EligibilityResult(id, null, null, video, owner, KindOf(video), source.Library, source.LocationIndex);
    }

    /// <summary>
    /// Gets the folder a unit is planned from: a folder unit's root (<c>ContainingFolderPath</c>) or a
    /// file set's directory, without a trailing separator.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The folder.</returns>
    public static string UnitRootOf(Video item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return PathRules.TrimEnd(KindOf(item) == MoveUnitKind.Folder ? item.ContainingFolderPath : Path.GetDirectoryName(item.Path) ?? string.Empty);
    }

    private static EligibilityResult Not(Guid id, string reason, Guid? owner = null) => new(id, MoveErrorCodes.NotEligible, reason, OwnerItemId: owner);

    private static string KindName(BaseItemKind kind) => kind switch
    {
        BaseItemKind.Episode => "episodes",
        BaseItemKind.MusicVideo => "music videos",
        _ => "movies"
    };
}
