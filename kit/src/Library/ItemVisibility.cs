namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What an id a client sent is to a user (<see cref="LibraryAccess.Visibility"/>).
/// </summary>
internal enum ItemVisibility
{
    /// <summary>The id names no item at all: a check lets it through, and the core finds nothing either.</summary>
    Unknown,

    /// <summary>The user may see the item.</summary>
    Visible,

    /// <summary>The item exists and the user may not see it: answer as if it didn't (an empty result or 404).</summary>
    Hidden,
}
