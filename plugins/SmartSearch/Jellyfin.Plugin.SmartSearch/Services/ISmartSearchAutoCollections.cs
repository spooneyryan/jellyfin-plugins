using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// Auto-collections: Jellyfin collections whose members follow a saved query. Every call needs a caller who manages
/// collections: an administrator (as the controller found, <c>callerIsAdmin</c>) or a user allowed to manage collections.
/// Only the list and the refreshes also take an API key (<c>caller</c> <c>null</c>).
/// </summary>
public interface ISmartSearchAutoCollections
{
    /// <summary>Checks whether a new collection can take a name; writes nothing.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="request">The name, and the auto-collection it is for when it exists.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether the name is free, what is in the way, warnings and suggestions.</returns>
    Task<SmartSearchAutoCollectionNameCheckDto> CheckNameAsync(User caller, bool callerIsAdmin, SmartSearchAutoCollectionCheckNameRequestDto request, CancellationToken cancellationToken);

    /// <summary>Shows what an auto-collection would hold now; saves and writes nothing.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="request">The unsaved auto-collection, or the saved one's id with changes.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <returns>The preview.</returns>
    Task<SmartSearchAutoCollectionPreviewDto> PreviewAsync(User caller, bool callerIsAdmin, SmartSearchAutoCollectionPreviewRequestDto request, CancellationToken cancellationToken);

    /// <summary>Lists the auto-collections with their state (no manual edits).</summary>
    /// <param name="caller">The calling user, or <c>null</c> for an API key.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="status">Only those with this status, or <c>null</c> for all.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collections, by name.</returns>
    Task<IReadOnlyList<SmartSearchAutoCollectionDto>> ListAsync(User? caller, bool callerIsAdmin, string? status, CancellationToken cancellationToken);

    /// <summary>Gets an auto-collection with its state.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collection.</returns>
    Task<SmartSearchAutoCollectionDto> GetAsync(User caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a new auto-collection and creates its collection, waiting for it up to a minute; the state tells whether it
    /// was made (<c>Ok</c>) or is still on its way (<c>Queued</c> or <c>Running</c>).
    /// </summary>
    /// <param name="caller">The calling user, its creator.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="request">The auto-collection and the preview it was saved from.</param>
    /// <param name="cancellationToken">Cancels the request (once saved, the collection is still made).</param>
    /// <returns>The auto-collection.</returns>
    Task<SmartSearchAutoCollectionDto> CreateAsync(User caller, bool callerIsAdmin, SmartSearchAutoCollectionRequestDto request, CancellationToken cancellationToken);

    /// <summary>Changes an auto-collection; its collection is updated next (queued).</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The changes, and the version they apply to.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collection.</returns>
    Task<SmartSearchAutoCollectionDto> UpdateAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionRequestDto request, CancellationToken cancellationToken);

    /// <summary>Asks for an update of an auto-collection's collection now.</summary>
    /// <param name="caller">The calling user, or <c>null</c> for an API key.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collection, its update queued.</returns>
    Task<SmartSearchAutoCollectionDto> RefreshAsync(User? caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken);

    /// <summary>Asks for an update of every enabled auto-collection now.</summary>
    /// <param name="caller">The calling user, or <c>null</c> for an API key.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task.</returns>
    Task RefreshAllAsync(User? caller, bool callerIsAdmin, CancellationToken cancellationToken);

    /// <summary>Creates the collection again of an auto-collection whose collection was deleted, never made, or replaced.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">A new name, or none to keep it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collection.</returns>
    Task<SmartSearchAutoCollectionDto> RecreateAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionRecreateRequestDto request, CancellationToken cancellationToken);

    /// <summary>Lets the held removals go once: the next run applies them, whatever the guard says.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The held removals' <c>DetectedUtc</c>, as read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collection, its run queued.</returns>
    Task<SmartSearchAutoCollectionDto> ApplyGuardAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionGuardRequestDto request, CancellationToken cancellationToken);

    /// <summary>Keeps the held members: they become pins, and the next run is queued.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The held removals' <c>DetectedUtc</c>, as read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when they are pinned.</returns>
    Task KeepGuardAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionGuardRequestDto request, CancellationToken cancellationToken);

    /// <summary>Gets an auto-collection's last runs, newest first; removed members only those the caller can open.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The runs.</returns>
    Task<IReadOnlyList<SmartSearchAutoCollectionHistoryDto>> GetHistoryAsync(User caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken);

    /// <summary>Gives an auto-collection its own copy of its saved query, so it no longer follows the saved query.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collection.</returns>
    Task<SmartSearchAutoCollectionDto> DetachAsync(User caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Gives an auto-collection whose creator was deleted to another user (an administrator only): the definitions of that
    /// creator that follow the same saved query, and the saved query itself, move together.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The new creator.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The auto-collection.</returns>
    Task<SmartSearchAutoCollectionDto> ReassignAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchSavedQueryOwnerDto request, CancellationToken cancellationToken);

    /// <summary>Deletes an auto-collection; its collection is kept as an ordinary one, or deleted too.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="deleteCollection">Whether to delete the collection too.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether a collection was kept or deleted.</returns>
    Task<bool> DeleteAsync(User caller, bool callerIsAdmin, Guid id, bool deleteCollection, CancellationToken cancellationToken);

    /// <summary>Lists the collections that auto-collections fill and the caller can open; nothing while auto-collections are off.</summary>
    /// <param name="caller">The calling user.</param>
    /// <returns>The collections' ids.</returns>
    IReadOnlyList<Guid> GetCollectionIds(User caller);

    /// <summary>Tells what fills a collection, for its page.</summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <param name="boxSetId">The collection's id.</param>
    /// <returns>The note; not found when no auto-collection fills it, the caller can't open it, or auto-collections are off.</returns>
    SmartSearchAutoCollectionNoteDto GetNote(User caller, bool callerIsAdmin, Guid boxSetId);
}
