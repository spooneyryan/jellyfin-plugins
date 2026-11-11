using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// The last seen name of every user a store holds records of (<c>KnownUsers</c>): what
/// <i>Remap by name</i> needs after a data folder was restored onto a rebuilt server, where every stored id is unknown,
/// and what the export writes as <c>Users[]</c>. Immutable; every change returns a new instance, or the same one
/// when nothing changed, so a store skips the write.
/// </summary>
/// <remarks>
/// <para>Refreshed on writes (<see cref="With(User)"/> when a user writes a record) and by the reconciler
/// (<see cref="Refresh"/>): the names of the server's users are updated, the names of ids the server doesn't know are
/// kept (they are the only link back), and entries of ids that no longer own a record are dropped, so the map never
/// outgrows the records.</para>
/// <para>JSON: an object of user id (the "N" format Jellyfin writes, lower case) to name, in ordinal key order, so the file
/// changes only when a name does. Keys in any <see cref="Guid"/> format are read; entries with an unreadable key or a
/// value that isn't a non-blank string are dropped (it is a cache of names, never worth quarantining a store for).</para>
/// </remarks>
[JsonConverter(typeof(KnownUsersJsonConverter))]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Not a collection suffix: the plan's field name.")]
internal sealed class KnownUsers : IReadOnlyDictionary<Guid, string>, IEquatable<KnownUsers>
{
    private readonly ImmutableSortedDictionary<Guid, string> _names;

    private KnownUsers(ImmutableSortedDictionary<Guid, string> names)
    {
        _names = names;
    }

    /// <summary>Gets the empty map.</summary>
    public static KnownUsers Empty { get; } = new(ImmutableSortedDictionary.Create<Guid, string>(KeyOrder.Instance));

    /// <inheritdoc />
    public int Count => _names.Count;

    /// <inheritdoc />
    public IEnumerable<Guid> Keys => _names.Keys;

    /// <inheritdoc />
    public IEnumerable<string> Values => _names.Values;

    /// <inheritdoc />
    public string this[Guid key] => _names[key];

    /// <summary>
    /// Creates a map from pairs; empty ids and blank names are left out, names are trimmed, and a repeated id keeps its last name.
    /// </summary>
    /// <param name="names">The pairs.</param>
    /// <returns>The map.</returns>
    public static KnownUsers From(IEnumerable<KeyValuePair<Guid, string>> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var builder = ImmutableSortedDictionary.CreateBuilder<Guid, string>(KeyOrder.Instance);
        foreach (var (id, name) in names)
        {
            if (!id.Equals(Guid.Empty) && Normalize(name) is { } text)
            {
                builder[id] = text;
            }
        }

        return builder.Count == 0 ? Empty : new KnownUsers(builder.ToImmutable());
    }

    /// <summary>
    /// Formats a user id as the map's JSON keys are written: "N", lower case, as Jellyfin writes user ids.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The text.</returns>
    public static string FormatId(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets a user's last seen name.
    /// </summary>
    /// <param name="id">The user id.</param>
    /// <returns>The name, or <c>null</c>.</returns>
    public string? NameOf(Guid id) => _names.TryGetValue(id, out var name) ? name : null;

    /// <summary>
    /// Records a user's name, when a user writes a record.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The map; the same instance when nothing changed.</returns>
    public KnownUsers With(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return With(user.Id, user.Username);
    }

    /// <summary>
    /// Records a name. An empty id or a blank name changes nothing.
    /// </summary>
    /// <param name="id">The user id.</param>
    /// <param name="name">The name.</param>
    /// <returns>The map; the same instance when nothing changed.</returns>
    public KnownUsers With(Guid id, string? name)
    {
        var text = Normalize(name);
        if (id.Equals(Guid.Empty) || text is null || (_names.TryGetValue(id, out var current) && string.Equals(current, text, StringComparison.Ordinal)))
        {
            return this;
        }

        return new KnownUsers(_names.SetItem(id, text));
    }

    /// <summary>
    /// Forgets a user, for example after the <see cref="DeletedUserPolicy.Delete"/> policy removed all its records.
    /// </summary>
    /// <param name="id">The user id.</param>
    /// <returns>The map; the same instance when the id wasn't in it.</returns>
    public KnownUsers Without(Guid id) => _names.ContainsKey(id) ? Wrap(_names.Remove(id)) : this;

    /// <summary>
    /// Moves names along with remapped records (<i>Remap by name</i>, an import mapped by name): each old id's entry is
    /// removed and its new id gets the name, unless the new id already has one.
    /// </summary>
    /// <param name="map">Old id to new id.</param>
    /// <returns>The map; the same instance when nothing changed.</returns>
    public KnownUsers Remap(IReadOnlyDictionary<Guid, Guid> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var names = _names;
        foreach (var (oldId, newId) in map)
        {
            if (oldId.Equals(newId) || !names.TryGetValue(oldId, out var name))
            {
                continue;
            }

            names = names.Remove(oldId);
            if (!newId.Equals(Guid.Empty) && !names.ContainsKey(newId))
            {
                names = names.Add(newId, name);
            }
        }

        return ReferenceEquals(names, _names) ? this : Wrap(names);
    }

    /// <summary>
    /// The reconciler's refresh: every stored id the server knows gets its current name, stored ids the server doesn't
    /// know keep their last seen name, and ids that own no record any more are dropped.
    /// </summary>
    /// <param name="storedUserIds">The ids that own at least one record.</param>
    /// <param name="currentNames">The server's users, id to name (<see cref="KitUsers.GetAll"/>).</param>
    /// <returns>The map; the same instance when nothing changed.</returns>
    public KnownUsers Refresh(IEnumerable<Guid> storedUserIds, IReadOnlyDictionary<Guid, string> currentNames)
    {
        ArgumentNullException.ThrowIfNull(storedUserIds);
        ArgumentNullException.ThrowIfNull(currentNames);

        var stored = storedUserIds.Where(id => !id.Equals(Guid.Empty)).ToHashSet();
        var builder = ImmutableSortedDictionary.CreateBuilder<Guid, string>(KeyOrder.Instance);
        foreach (var id in stored)
        {
            var name = currentNames.TryGetValue(id, out var current) ? Normalize(current) : null;
            name ??= NameOf(id);
            if (name is not null)
            {
                builder[id] = name;
            }
        }

        var next = builder.ToImmutable();
        return Equal(next, _names) ? this : Wrap(next);
    }

    /// <inheritdoc />
    public bool ContainsKey(Guid key) => _names.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(Guid key, [MaybeNullWhen(false)] out string value) => _names.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<Guid, string>> GetEnumerator() => _names.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(KnownUsers? other) => other is not null && Equal(_names, other._names);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KnownUsers other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var (id, name) in _names)
        {
            hash.Add(id);
            hash.Add(name, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static string? Normalize(string? name)
    {
        var text = name?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool Equal(ImmutableSortedDictionary<Guid, string> a, ImmutableSortedDictionary<Guid, string> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var name) && string.Equals(name, p.Value, StringComparison.Ordinal));

    private static KnownUsers Wrap(ImmutableSortedDictionary<Guid, string> names) => names.Count == 0 ? Empty : new KnownUsers(names);

    // Ordinal order of the "N" text, the order the keys are written in.
    private sealed class KeyOrder : IComparer<Guid>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare(Guid x, Guid y) => string.CompareOrdinal(FormatId(x), FormatId(y));
    }
}
