using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagCloud.Clouds;

/// <summary>
/// The saved clouds' store and its owner for the kit's users service: counts by user, the owners'
/// last seen names, snapshots before the kit's operations, a deleted user's clouds and <i>Remap by name</i>.
/// </summary>
/// <remarks>
/// When a user is deleted, their private clouds go and their shared ones stay, owned by "Former user", until an
/// admin deletes them. The <see cref="DeletedUserPolicy.Delete"/> policy deletes the shared ones too.
/// </remarks>
internal sealed class SavedCloudOwner : IUserDataOwner, IDisposable
{
    /// <summary>The store's file name in the data folder.</summary>
    public const string FileName = "clouds.json";

    private readonly string _snapshotDirectory;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="SavedCloudOwner"/> class.
    /// </summary>
    /// <param name="file">The store's file.</param>
    /// <param name="snapshotDirectory">Where snapshots go.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock.</param>
    public SavedCloudOwner(string file, string snapshotDirectory, ILogger logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        _snapshotDirectory = snapshotDirectory;
        _time = time ?? TimeProvider.System;
        Store = CreateStore(file, logger, _time);
    }

    /// <inheritdoc />
    public string Name => Store.StoreName;

    /// <summary>Gets the store.</summary>
    public JsonDocumentStore<SavedCloudDocument> Store { get; }

    /// <summary>Gets where the store's snapshots go.</summary>
    public string SnapshotDirectory => _snapshotDirectory;

    /// <inheritdoc />
    public bool IsReadOnly => Store.IsReadOnly;

    /// <inheritdoc />
    public KnownUsers KnownUsers => Store.Current.KnownUsers ?? KnownUsers.Empty;

    /// <summary>
    /// Creates the store over a file.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The store.</returns>
    public static JsonDocumentStore<SavedCloudDocument> CreateStore(string file, ILogger logger, TimeProvider? time = null)
        => new(file, SavedCloudDocument.CurrentVersion, SavedCloudDocument.CreateEmpty, [], logger, JsonStoreFormat.Options, time);

    /// <summary>
    /// Gets a document with these clouds, in the order they were saved.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="clouds">The clouds.</param>
    /// <returns>The new document.</returns>
    public static SavedCloudDocument WithClouds(SavedCloudDocument document, IEnumerable<SavedCloudRecord> clouds)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Clouds = clouds.OrderBy(c => c.CreatedUtc).ThenBy(c => c.Id).ToList() };
    }

    /// <summary>
    /// Counts each user's clouds in a document.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The counts by owner.</returns>
    public static IReadOnlyDictionary<Guid, int> CountRecordsByUser(SavedCloudDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (document.Clouds ?? []).Where(c => c is not null).GroupBy(c => c.OwnerId).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => CountRecordsByUser(Store.Current);

    /// <inheritdoc />
    public async Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        await Store.UpdateAsync(
            document =>
            {
                var known = document.KnownUsers ?? KnownUsers.Empty;
                var refreshed = known.Refresh(CountRecordsByUser(document).Keys, currentNames);
                return ReferenceEquals(refreshed, known) ? document : document with { KnownUsers = refreshed };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
        => await Store.PreOpSnapshotAsync(_snapshotDirectory, operation, cancellationToken).ConfigureAwait(false) is { } path
            ? System.IO.Path.GetFileName(path)
            : null;

    /// <inheritdoc />
    public async Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken)
    {
        var affected = 0;
        await Store.UpdateAsync(
            document =>
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var clouds = new List<SavedCloudRecord>();
                affected = 0;
                foreach (var cloud in document.Clouds)
                {
                    if (!cloud.OwnerId.Equals(userId))
                    {
                        clouds.Add(cloud);
                    }
                    else if (cloud.Shared && policy != DeletedUserPolicy.Delete)
                    {
                        if (!cloud.OwnerDeleted)
                        {
                            clouds.Add(cloud with { OwnerDeleted = true, UpdatedUtc = now });
                            affected++;
                        }
                        else
                        {
                            clouds.Add(cloud);
                        }
                    }
                    else
                    {
                        affected++;
                    }
                }

                if (affected == 0)
                {
                    return document;
                }

                var known = document.KnownUsers ?? KnownUsers.Empty;
                return WithClouds(document, clouds) with
                {
                    KnownUsers = clouds.Exists(c => c.OwnerId.Equals(userId)) ? known : known.Without(userId)
                };
            },
            cancellationToken).ConfigureAwait(false);
        return affected;
    }

    /// <inheritdoc />
    public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        var moved = 0;
        await Store.UpdateAsync(
            document =>
            {
                moved = 0;
                var clouds = document.Clouds.Select(c =>
                {
                    if (map.TryGetValue(c.OwnerId, out var newId) && !newId.Equals(c.OwnerId))
                    {
                        moved++;
                        return c with { OwnerId = newId, OwnerDeleted = false };
                    }

                    return c;
                }).ToList();
                return moved == 0
                    ? document
                    : WithClouds(document, clouds) with { KnownUsers = (document.KnownUsers ?? KnownUsers.Empty).Remap(map) };
            },
            cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <inheritdoc />
    public void Dispose() => Store.Dispose();
}
