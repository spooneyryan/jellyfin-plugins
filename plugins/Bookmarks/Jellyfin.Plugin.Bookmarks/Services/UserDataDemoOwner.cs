using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer user data demo's store as a kit <see cref="IUserDataOwner"/>. Its policies copy
/// those for bookmarks: <c>KeepAsOrphan</c> and <c>Anonymize</c> remove a deleted user's
/// records without a file and keep those with one as tombstones under the old id; <c>Delete</c> removes all of them and
/// the user's name.
/// </summary>
/// <remarks>
/// While the developer setting <c>EnableUserDataDemo</c> is off the owner holds nothing as far as the kit can tell: it
/// never reads or creates its file, so a user deleted then keeps its demo records, and the reconciler lists the id once
/// the demo is back on, as for a user deleted while a plugin was disabled.
/// </remarks>
internal sealed class UserDataDemoOwner : IUserDataOwner, IDisposable
{
    private readonly IPluginConfigurationProvider _configuration;
    private readonly string _snapshotDirectory;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDataDemoOwner"/> class.
    /// </summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="file">The store file (<c>BookmarkPaths.UserDataDemoFile</c>).</param>
    /// <param name="snapshotDirectory">The snapshot folder (<c>BookmarkPaths.SnapshotsDirectory</c>).</param>
    /// <param name="logger">The store's logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public UserDataDemoOwner(IPluginConfigurationProvider configuration, string file, string snapshotDirectory, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        _configuration = configuration;
        _snapshotDirectory = snapshotDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Store = CreateStore(file, logger, _timeProvider);
    }

    /// <inheritdoc />
    public string Name => Store.StoreName;

    /// <summary>
    /// Gets the demo's store.
    /// </summary>
    public JsonDocumentStore<UserDataDemoDocument> Store { get; }

    /// <summary>
    /// Gets the snapshot folder.
    /// </summary>
    public string SnapshotDirectory => _snapshotDirectory;

    /// <summary>
    /// Gets a value indicating whether the demo is on.
    /// </summary>
    public bool IsEnabled => _configuration.GetConfiguration().EnableUserDataDemo;

    /// <inheritdoc />
    public bool IsReadOnly => IsEnabled && Store.IsReadOnly;

    /// <inheritdoc />
    public KnownUsers KnownUsers => IsEnabled ? Store.Current.KnownUsers ?? KnownUsers.Empty : KnownUsers.Empty;

    /// <summary>
    /// Creates the demo's store: schema version 1, no migrations, the kit's format.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <returns>The store.</returns>
    public static JsonDocumentStore<UserDataDemoDocument> CreateStore(string file, ILogger logger, TimeProvider? timeProvider = null)
        => new(file, UserDataDemoDocument.CurrentVersion, UserDataDemoDocument.CreateEmpty, [], logger, JsonStoreFormat.Options, timeProvider);

    /// <summary>
    /// Counts a document's records per user, tombstones included.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>User id to number of records.</returns>
    public static IReadOnlyDictionary<Guid, int> CountRecordsByUser(UserDataDemoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (document.Records ?? []).Where(r => r is not null).GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Returns the document with the given records, ordered by creation time and id.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="records">The records.</param>
    /// <returns>The new document.</returns>
    public static UserDataDemoDocument WithRecords(UserDataDemoDocument document, IEnumerable<UserDataDemoRecord> records)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Records = records.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList() };
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => IsEnabled ? CountRecordsByUser(Store.Current) : new Dictionary<Guid, int>();

    /// <inheritdoc />
    public async Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return;
        }

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
        => IsEnabled && await Store.PreOpSnapshotAsync(_snapshotDirectory, operation, cancellationToken).ConfigureAwait(false) is { } path
            ? System.IO.Path.GetFileName(path)
            : null;

    /// <inheritdoc />
    public async Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return 0;
        }

        var affected = 0;
        await Store.UpdateAsync(
            document =>
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var records = new List<UserDataDemoRecord>();
                affected = 0;
                foreach (var record in document.Records)
                {
                    if (!record.UserId.Equals(userId))
                    {
                        records.Add(record);
                    }
                    else if (policy != DeletedUserPolicy.Delete && record.HasFile)
                    {
                        // A record with a clip file becomes a tombstone owned by the deleted id.
                        if (record.DeletedUtc is null)
                        {
                            records.Add(record with { DeletedUtc = now, UpdatedAt = now });
                            affected++;
                        }
                        else
                        {
                            records.Add(record);
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
                return WithRecords(document, records) with
                {
                    KnownUsers = records.Exists(r => r.UserId.Equals(userId)) ? known : known.Without(userId)
                };
            },
            cancellationToken).ConfigureAwait(false);
        return affected;
    }

    /// <inheritdoc />
    public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!IsEnabled)
        {
            return 0;
        }

        var moved = 0;
        await Store.UpdateAsync(
            document =>
            {
                moved = 0;
                var records = document.Records.Select(r =>
                {
                    if (map.TryGetValue(r.UserId, out var newId) && !newId.Equals(r.UserId))
                    {
                        moved++;
                        return r with { UserId = newId };
                    }

                    return r;
                }).ToList();
                return moved == 0
                    ? document
                    : WithRecords(document, records) with { KnownUsers = (document.KnownUsers ?? KnownUsers.Empty).Remap(map) };
            },
            cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <inheritdoc />
    public void Dispose() => Store.Dispose();
}
