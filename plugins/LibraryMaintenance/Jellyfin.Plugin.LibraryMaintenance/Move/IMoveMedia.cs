using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// Move media as its controllers see it: every answer is a DTO, every item and library is resolved for the
/// caller. The routes' gate (<c>MoveEnabled</c>, the host) is <see cref="MoveGateAttribute"/>'s.
/// </summary>
public interface IMoveMedia
{
    /// <summary>
    /// Gets the caller's capabilities (<c>GET /LibraryMaintenance/Capabilities</c>).
    /// </summary>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c> on this request.</param>
    /// <returns>The capabilities; every flag false for a caller who isn't an administrator.</returns>
    LibraryMaintenanceCapabilitiesDto GetCapabilities(bool isAdmin);

    /// <summary>
    /// Gets whether items can move (<c>GET /LibraryMaintenance/Move/Eligibility</c>).
    /// </summary>
    /// <param name="ids">The item ids, comma-separated; 1 to 100.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>One answer per id, in the order asked.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for no ids, more than 100, or an id that isn't a GUID.</exception>
    IReadOnlyList<LibraryMaintenanceMoveEligibilityDto> GetEligibility(string? ids, User? user);

    /// <summary>
    /// Gets what moves with an item (<c>GET /LibraryMaintenance/Move/Source</c>).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The item that moves (its owner, for a version or part), its library and location, and its unit.</returns>
    /// <exception cref="Kit.Api.PluginException">404 for an item the caller can't see; 409 with its code for one that
    /// can't move.</exception>
    LibraryMaintenanceMoveSourceDto GetSource(Guid itemId, User? user);

    /// <summary>
    /// Gets the libraries and folders an item can move to (<c>GET /LibraryMaintenance/Move/Destinations</c>).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The libraries of the item's type the caller can open, the item's own first.</returns>
    /// <exception cref="Kit.Api.PluginException">404 for an item the caller can't see; 409 for one that can't move.</exception>
    IReadOnlyList<LibraryMaintenanceMoveDestinationLibraryDto> GetDestinations(Guid itemId, User? user);

    /// <summary>
    /// Gets a page of a destination folder's subfolders (<c>GET /LibraryMaintenance/Move/Folders</c>).
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="locationIndex">The index of its folder.</param>
    /// <param name="relativePath">A folder under it, or empty.</param>
    /// <param name="filter">Only names containing this.</param>
    /// <param name="startIndex">The first folder of the page.</param>
    /// <param name="limit">The page size, 1 to 200.</param>
    /// <param name="itemId">The item being moved, for its own folder and its library's type; optional.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The page.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for a bad path or page, 404 for a library or folder the caller can't
    /// open, 503 for a folder that isn't reachable.</exception>
    LibraryMaintenanceMoveFoldersDto GetFolders(Guid libraryId, int locationIndex, string? relativePath, string? filter, int startIndex, int limit, Guid? itemId, User? user);

    /// <summary>
    /// Plans a move (<c>POST /LibraryMaintenance/Move/Plan</c>); nothing moves.
    /// </summary>
    /// <param name="request">The item and the destination.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The plan, also when it has blockers.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for a bad request (<c>move.destination-invalid</c> for a destination
    /// that can't be used), 404, 409 for an item that can't move, 503 for a destination that isn't reachable.</exception>
    Task<LibraryMaintenanceMovePlanDto> PlanAsync(LibraryMaintenanceMovePlanRequestDto? request, User? user, CancellationToken cancellationToken);
}
