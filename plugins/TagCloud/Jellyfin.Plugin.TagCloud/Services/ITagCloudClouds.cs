using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TagCloud.Api.Models;

namespace Jellyfin.Plugin.TagCloud.Services;

/// <summary>
/// Saved clouds: each user's own, and the ones shared with everyone while the admin allows sharing.
/// </summary>
public interface ITagCloudClouds
{
    /// <summary>
    /// Lists the clouds the user may open: their own, in the order saved, then the ones others share, by name.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The clouds.</returns>
    IReadOnlyList<TagCloudSavedCloudDto> List(User user);

    /// <summary>
    /// Gets one cloud the user may open.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="id">The cloud's id.</param>
    /// <returns>The cloud; a 404 when the user may not open it or it is gone.</returns>
    TagCloudSavedCloudDto Get(User user, Guid id);

    /// <summary>
    /// Saves a new cloud of the user's.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="request">The cloud.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>The saved cloud.</returns>
    Task<TagCloudSavedCloudDto> CreateAsync(User user, TagCloudSaveRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a cloud of the user's again.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="id">The cloud's id.</param>
    /// <param name="request">The cloud.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>The saved cloud.</returns>
    Task<TagCloudSavedCloudDto> UpdateAsync(User user, Guid id, TagCloudSaveRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a cloud the user may open into a new private cloud of theirs.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="id">The cloud's id.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>The copy.</returns>
    Task<TagCloudSavedCloudDto> DuplicateAsync(User user, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a cloud: the user's own, or as an administrator a shared one.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="id">The cloud's id.</param>
    /// <param name="isAdmin">Whether the user is an administrator.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>A task that completes when it is deleted.</returns>
    Task DeleteAsync(User user, Guid id, bool isAdmin, CancellationToken cancellationToken);
}
