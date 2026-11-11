using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// One <c>BaseItems</c> row of <see cref="FakeCore"/>: the item as <c>GetItemList</c> loads it (with and without its
/// provider ids, which core loads only when <c>DtoOptions</c> asks), and the columns only SQL sees: the stored clean name,
/// the top parent, the stored item values (shared rows that keep the clean value they were created with), the provider
/// rows, the user-data rows (one per user-data key), the inherited rating, the unrated type, the ancestors and the
/// presentation key. And what only <c>IsVisible(user)</c> reads (<see cref="FakeCore.VisibleInMemory"/>): a box set's
/// linked children, the tags it inherits from parents SQL doesn't look at, and the rating it compares.
/// </summary>
internal sealed class FakeRow
{
    public required BaseItem Item { get; init; }

    public required BaseItem ItemWithoutProviderIds { get; init; }

    /// <summary>Gets the row's item factory (with or without provider ids): a fresh item for loads with user data or images.</summary>
    public required Func<bool, BaseItem> Create { get; init; }

    public BaseItemKind Kind => Item.GetBaseItemKind();

    /// <summary>Gets or sets the row's place in the table (rows by ItemIds come back in table order).</summary>
    public int Index { get; set; }

    public Guid Id => Item.Id;

    public string? CleanName { get; set; }

    public Guid? TopParentId { get; set; }

    public bool IsVirtual { get; set; }

    /// <summary>Gets or sets <c>OwnerId</c> as 10.11 stores it: the zero GUID for items without an owner.</summary>
    public string? StoredOwnerId { get; set; } = Guid.Empty.ToString();

    public int? InheritedRating { get; set; }

    public int? InheritedSubRating { get; set; }

    public required string UnratedType { get; init; }

    public List<Guid> Ancestors { get; } = [];

    public required string PresentationUniqueKey { get; set; }

    /// <summary>Gets or sets a secondary version's primary ("N"), set by <see cref="FakeCore.Merge"/>.</summary>
    public string? PrimaryVersionId { get; set; }

    /// <summary>Gets a primary's linked versions, set by <see cref="FakeCore.Merge"/>.</summary>
    public List<Guid> LinkedVersions { get; } = [];

    /// <summary>Gets the item's images, loaded only with <c>EnableImages</c>.</summary>
    public List<ItemImageInfo> Images { get; } = [];

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public List<(string Key, string Value)> Providers { get; } = [];

    public List<FakeItemValue> Values { get; } = [];

    public List<FakeUserData> UserData { get; } = [];

    public float? CommunityRating { get; set; }

    public DateTime? PremiereDateUtc { get; set; }

    public DateTime? DateCreatedUtc { get; set; }

    public int? Width { get; set; }

    /// <summary>Gets a box set's linked children; their libraries decide <c>BoxSet.IsVisible</c> (BoxSet.cs:156-182).</summary>
    public List<Guid> Children { get; } = [];

    /// <summary>
    /// Gets the tags <c>GetInheritedTags</c> adds that SQL's <c>ExcludeInheritedTags</c> doesn't read (a season's, a
    /// library's): SQL reads the item's own tags and an episode's series' tags only.
    /// </summary>
    public List<string> MemoryTags { get; } = [];

    /// <summary>
    /// Gets or sets the score <c>IsParentalAllowed</c> compares (<c>CustomRatingForComparison</c>, else
    /// <c>OfficialRatingForComparison</c>, inherited from the display parent), <c>null</c> for unrated; it can differ
    /// from the stored <see cref="InheritedRating"/>, which is computed at save.
    /// </summary>
    public int? MemoryScore { get; set; }
}

/// <summary>A shared <c>ItemValues</c> row.</summary>
internal sealed record FakeItemValue(Jellyfin.Database.Implementations.Entities.ItemValueType Type, string Value, string CleanValue);

/// <summary>A <c>UserData</c> row.</summary>
internal sealed record FakeUserData(Guid UserId, string Key, bool Played, bool IsFavorite, long PlaybackPositionTicks, int PlayCount = 0, DateTime? LastPlayedDate = null);
