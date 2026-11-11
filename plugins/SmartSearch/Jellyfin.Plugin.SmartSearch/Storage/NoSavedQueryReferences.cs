using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// The reference guard while nothing references saved queries: no references, nothing a bulk change breaks, and every
/// edit, delete and bulk change is written as asked. The auto-collections replace it with one that checks their definitions.
/// </summary>
internal sealed class NoSavedQueryReferences : ISavedQueryReferenceGuard
{
    /// <inheritdoc />
    public bool ReferencesUnknown => false;

    /// <inheritdoc />
    public int CountReferences(Guid savedQueryId) => 0;

    /// <inheritdoc />
    public async Task<SavedQueryGuardOutcome> UpdateAsync(SavedQueryEdit edit, Func<CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(save);
        await save(cancellationToken).ConfigureAwait(false);
        return SavedQueryGuardOutcome.Done;
    }

    /// <inheritdoc />
    public async Task<SavedQueryGuardOutcome> DeleteAsync(SavedQueryDeletion deletion, Func<CancellationToken, Task> delete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(delete);
        await delete(cancellationToken).ConfigureAwait(false);
        return SavedQueryGuardOutcome.Done;
    }

    /// <inheritdoc />
    public IReadOnlyList<SmartSearchAutoCollectionRefDto> FindBrokenReferences(SavedQueryBulkChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return [];
    }

    /// <inheritdoc />
    public Task<T> BulkChangeAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write(cancellationToken);
    }
}
