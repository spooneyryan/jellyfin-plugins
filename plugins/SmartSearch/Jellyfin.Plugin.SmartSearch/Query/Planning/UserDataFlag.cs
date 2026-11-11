namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>A user-data flag core can filter on in SQL (<c>IsPlayed</c>, <c>IsFavorite</c>, <c>IsResumable</c>).</summary>
internal enum UserDataFlag
{
    /// <summary><c>Played</c>.</summary>
    Played,

    /// <summary><c>IsFavorite</c>.</summary>
    Favorite,

    /// <summary><c>PlaybackPositionTicks &gt; 0</c>.</summary>
    Resumable
}
