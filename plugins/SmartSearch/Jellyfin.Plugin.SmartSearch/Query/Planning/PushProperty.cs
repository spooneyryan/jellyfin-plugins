namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>The <c>InternalItemsQuery</c> properties the planner pushes.</summary>
internal enum PushProperty
{
    /// <summary><c>IncludeItemTypes</c>: one kind per item.</summary>
    IncludeItemTypes,

    /// <summary><c>TopParentIds</c>: one top parent per item, always the physical folder ids.</summary>
    TopParentIds,

    /// <summary><c>Years</c>: <c>ProductionYear IN (…)</c>.</summary>
    Years,

    /// <summary><c>OfficialRatings</c>: <c>OfficialRating IN (…)</c>, exact strings.</summary>
    OfficialRatings,

    /// <summary><c>Genres</c>: any of the clean values.</summary>
    Genres,

    /// <summary><c>Tags</c>: any of the clean values.</summary>
    Tags,

    /// <summary><c>StudioIds</c>: any of the studios.</summary>
    StudioIds,

    /// <summary><c>AncestorIds</c>: any of the ancestors.</summary>
    AncestorIds,

    /// <summary><c>ExcludeTags</c>: none of the clean values.</summary>
    ExcludeTags,

    /// <summary><c>MinCommunityRating</c>.</summary>
    MinCommunityRating,

    /// <summary><c>MinPremiereDate</c>.</summary>
    MinPremiereDate,

    /// <summary><c>MaxPremiereDate</c>.</summary>
    MaxPremiereDate,

    /// <summary><c>MinDateCreated</c>.</summary>
    MinDateCreated,

    /// <summary><c>MinWidth</c>.</summary>
    MinWidth,

    /// <summary><c>MaxWidth</c>.</summary>
    MaxWidth,

    /// <summary><c>HasOverview</c>.</summary>
    HasOverview,

    /// <summary><c>IsLocked</c>.</summary>
    IsLocked,

    /// <summary><c>IsPlayed</c>.</summary>
    IsPlayed,

    /// <summary><c>IsFavorite</c>.</summary>
    IsFavorite,

    /// <summary><c>IsResumable</c>.</summary>
    IsResumable,

    /// <summary><c>HasTmdbId</c>.</summary>
    HasTmdbId,

    /// <summary><c>HasImdbId</c>.</summary>
    HasImdbId,

    /// <summary><c>HasTvdbId</c>.</summary>
    HasTvdbId,

    /// <summary><c>Name</c>: <c>CleanName</c> equals the clean value.</summary>
    Name,

    /// <summary><c>NameContains</c>: the folded term in <c>CleanName</c> or <c>OriginalTitle</c>.</summary>
    NameContains
}
