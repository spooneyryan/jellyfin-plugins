using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>What <see cref="FakeCore.Add"/> stores: an item as saved, with the stored columns that can differ from it.</summary>
internal sealed class RowSpec
{
    public RowSpec(BaseItemKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public BaseItemKind Kind { get; }

    public string Name { get; }

    public Guid? Id { get; set; }

    /// <summary>Gets or sets the stored clean name; <c>null</c> for today's.</summary>
    public string? CleanName { get; set; }

    public string? OriginalTitle { get; set; }

    public string? Overview { get; set; }

    public int? Year { get; set; }

    /// <summary>Gets or sets the premiere date as the item carries it (its kind decides how it was stored).</summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>Gets or sets the stored instant, when it isn't what <see cref="PremiereDate"/> converts to.</summary>
    public DateTime? PremiereStoredUtc { get; set; }

    public DateTime? DateCreated { get; set; }

    /// <summary>Gets or sets the zone a value without a kind was stored in (the server's).</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public IReadOnlyList<string> Genres { get; set; } = [];

    public IReadOnlyList<string> Tags { get; set; } = [];

    public IReadOnlyList<string> Studios { get; set; } = [];

    public string? OfficialRating { get; set; }

    public float? CommunityRating { get; set; }

    public float? CriticRating { get; set; }

    public long? RunTimeTicks { get; set; }

    public int Width { get; set; }

    public string? Container { get; set; }

    public bool IsLocked { get; set; }

    public List<(string Key, string Value)> Providers { get; } = [];

    public CollectionFolder? Library { get; set; }

    public Guid? TopParentId { get; set; }

    public FakeRow? Series { get; set; }

    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the episode's stored series name (it can lag a rename).</summary>
    public string? SeriesName { get; set; }

    public FakeRow? Owner { get; set; }

    /// <summary>Gets or sets the owner's id, for an owner that isn't a row (or isn't one yet).</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Gets or sets the stored <c>UnratedType</c>; <c>null</c> for the kind's.</summary>
    public string? UnratedType { get; set; }

    /// <summary>Gets or sets the stored <c>AncestorIds</c> rows; <c>null</c> for 10.11.5's (<see cref="FakeCore.AncestorsOf"/>).</summary>
    public IReadOnlyList<Guid>? Ancestors { get; set; }

    /// <summary>Gets or sets an episode's season; <c>null</c> for its series' first season.</summary>
    public Guid? SeasonId { get; set; }

    /// <summary>Gets or sets the stored <c>ItemValuesMap</c> rows (seeded values); <c>null</c> for the columns' values.</summary>
    public IReadOnlyList<(Jellyfin.Database.Implementations.Entities.ItemValueType Type, string Value)>? StoredValues { get; set; }

    /// <summary>Gets or sets what else to set on the loaded item (both of its copies).</summary>
    public Action<BaseItem>? Configure { get; set; }

    public bool IsVirtual { get; set; }

    public int? InheritedRating { get; set; }

    public int? InheritedSubRating { get; set; }

    public string? PresentationUniqueKey { get; set; }

    public List<FakeUserData> UserData { get; } = [];

    /// <summary>Gets the item's images (<c>ImageInfos</c>, loaded only with <c>EnableImages</c>).</summary>
    public List<MediaBrowser.Controller.Entities.ItemImageInfo> Images { get; } = [];

    /// <summary>Gets or sets the stored sort name (default: the clean name).</summary>
    public string? SortName { get; set; }
}
