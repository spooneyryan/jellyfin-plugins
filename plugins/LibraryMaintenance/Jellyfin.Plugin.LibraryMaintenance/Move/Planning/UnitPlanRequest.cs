using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// What <see cref="UnitPlanner"/> plans: an eligible item (already retargeted to its owner), where it is
/// and where it goes.
/// </summary>
/// <param name="Item">The item.</param>
/// <param name="SourceLibrary">Its library.</param>
/// <param name="SourceLocationIndex">The location it is in.</param>
/// <param name="Destination">The destination, resolved (<see cref="DestinationResolver"/>).</param>
/// <param name="Wrap">The dialog's "Put it in its own folder" choice; null for <see cref="MoveSettings.LooseFileFolderMode"/>.</param>
/// <param name="WrapFolderName">The wrap folder's name as typed; null for the cleaned <c>Title (Year)</c>.</param>
/// <param name="Settings">The settings.</param>
internal sealed record UnitPlanRequest(
    Video Item,
    CatalogLibrary SourceLibrary,
    int SourceLocationIndex,
    ResolvedDestination Destination,
    bool? Wrap,
    string? WrapFolderName,
    MoveSettings Settings);
