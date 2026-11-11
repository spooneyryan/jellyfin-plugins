using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Storage;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Builds the context a definition is checked in, from the stores and settings as they are now.</summary>
internal sealed class AutoCollectionCheckContextSource
{
    private readonly AutoCollectionStore _store;
    private readonly SavedQueryStore _savedQueries;
    private readonly IUserManager _users;
    private readonly Func<IReadOnlyCollection<BaseItemKind>> _allowedItemKinds;
    private readonly Func<AutoCollectionSettings> _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionCheckContextSource"/> class.
    /// </summary>
    /// <param name="store">The definitions.</param>
    /// <param name="savedQueries">The saved queries.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="allowedItemKinds">The setting <c>AllowedItemKinds</c>, read on each call.</param>
    /// <param name="settings">The auto-collection settings.</param>
    public AutoCollectionCheckContextSource(AutoCollectionStore store, SavedQueryStore savedQueries, IUserManager users, Func<IReadOnlyCollection<BaseItemKind>> allowedItemKinds, AutoCollectionSettings settings)
        : this(store, savedQueries, users, allowedItemKinds, () => settings)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionCheckContextSource"/> class.
    /// </summary>
    /// <param name="store">The definitions.</param>
    /// <param name="savedQueries">The saved queries.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="allowedItemKinds">The setting <c>AllowedItemKinds</c>, read on each call.</param>
    /// <param name="settings">The auto-collection settings, read on each use.</param>
    public AutoCollectionCheckContextSource(AutoCollectionStore store, SavedQueryStore savedQueries, IUserManager users, Func<IReadOnlyCollection<BaseItemKind>> allowedItemKinds, Func<AutoCollectionSettings> settings)
    {
        _store = store;
        _savedQueries = savedQueries;
        _users = users;
        _allowedItemKinds = allowedItemKinds;
        _settings = settings;
    }

    /// <summary>Gets the auto-collection settings as they are now.</summary>
    public AutoCollectionSettings Settings => _settings();

    /// <summary>Finds a user by id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The user, or <c>null</c> when there is none (deleted).</returns>
    public User? FindUser(Guid id) => _users.GetUserById(id);

    /// <summary>Builds the context now.</summary>
    /// <returns>The context.</returns>
    public AutoCollectionCheckContext Create() => new(
        _store.IsReadOnly,
        _savedQueries.Current,
        id => _users.GetUserById(id),
        _allowedItemKinds(),
        _store.CollectionIds(),
        Settings.MaxMembersPerCollection);
}
