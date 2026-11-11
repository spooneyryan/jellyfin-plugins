using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>The server's collection calls, recorded: a create makes a collection in <see cref="Boxes"/>.</summary>
internal sealed class RecordingPersistence : ICollectionPersistence
{
    public List<BoxSet> Boxes { get; } = [];

    public List<string> Calls { get; } = [];

    public int Saves => Calls.Count(c => c == "Save");

    public int Refreshes => Calls.Count(c => c == "Refresh");

    public int Creates => Calls.Count(c => c == "Create");

    /// <summary>Gets or sets a value indicating whether a create throws after making the collection (as the server's can).</summary>
    public bool CreateThrowsAfterMaking { get; set; }

    /// <summary>Gets or sets a value indicating whether a create throws before making anything.</summary>
    public bool CreateThrows { get; set; }

    public Task<BoxSet> CreateAsync(string name, bool isLocked, IReadOnlyDictionary<string, string> providerIds, IReadOnlyList<Guid> itemIds)
    {
        Calls.Add("Create");
        if (CreateThrows)
        {
            throw new InvalidOperationException("create failed");
        }

        var box = new BoxSet
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsLocked = isLocked,
            ProviderIds = new Dictionary<string, string>(providerIds, StringComparer.OrdinalIgnoreCase),
            DateCreated = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(Boxes.Count),
            LinkedChildren = CreateThrowsAfterMaking ? [] : itemIds.Select(id => new LinkedChild { ItemId = id, Type = LinkedChildType.Manual }).ToArray()
        };
        Boxes.Add(box);
        if (CreateThrowsAfterMaking)
        {
            throw new ArgumentException("an item wasn't found");
        }

        return Task.FromResult(box);
    }

    public void UpdateRating(BoxSet box, IReadOnlyList<BaseItem> members) => Calls.Add("Rating");

    /// <summary>Gets or sets which collections' saves fail (none by default).</summary>
    public Func<BoxSet, bool> SaveFails { get; set; } = _ => false;

    public Task SaveAsync(BoxSet box, CancellationToken cancellationToken)
    {
        Calls.Add("Save");
        if (SaveFails(box))
        {
            throw new IOException("the disk is full");
        }

        return Task.CompletedTask;
    }

    public void QueueRefresh(BoxSet box) => Calls.Add("Refresh");

    /// <summary>Gets or sets who can see and delete a collection (everyone by default).</summary>
    public Func<BoxSet, User, bool> Deletable { get; set; } = (_, _) => true;

    public BoxSet? Reload(Guid collectionId) => Boxes.FirstOrDefault(b => b.Id.Equals(collectionId));

    public BoxSet? FindDeletable(Guid id, User user) => Boxes.FirstOrDefault(b => b.Id.Equals(id) && Deletable(b, user));

    public Task DeleteAsync(BoxSet box)
    {
        Calls.Add("Delete");
        Boxes.Remove(box);
        return Task.CompletedTask;
    }

    /// <summary>Gets the items each add call was given.</summary>
    public List<IReadOnlyList<Guid>> Added { get; } = [];

    /// <summary>Gets the items each remove call was given.</summary>
    public List<IReadOnlyList<Guid>> Removed { get; } = [];

    /// <summary>Gets or sets what runs during an add or remove call (the suppression seen from inside).</summary>
    public Action<Guid>? DuringCall { get; set; }

    // As the server's calls: an add appends links, a remove drops the links whose cached id it names.
    public Task AddAsync(Guid collectionId, IReadOnlyList<Guid> itemIds)
    {
        Calls.Add("Add");
        Added.Add(itemIds.ToList());
        DuringCall?.Invoke(collectionId);
        var box = Boxes.Single(b => b.Id.Equals(collectionId));
        box.LinkedChildren = [.. box.LinkedChildren, .. itemIds.Select(id => new LinkedChild { ItemId = id, Type = LinkedChildType.Manual })];
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid collectionId, IReadOnlyList<Guid> itemIds)
    {
        Calls.Add("Remove");
        Removed.Add(itemIds.ToList());
        DuringCall?.Invoke(collectionId);
        var box = Boxes.Single(b => b.Id.Equals(collectionId));
        var gone = itemIds.ToHashSet();
        box.LinkedChildren = box.LinkedChildren.Where(l => l.ItemId is not { } id || !gone.Contains(id)).ToArray();
        return Task.CompletedTask;
    }
}
